namespace MusicDimension.misc
{
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Text.RegularExpressions;
    using MusicDimension.models;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;

    public static partial class MusicWebHost
    {
        private const long MaximumUploadSize = 512L * 1024 * 1024;
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".aac", ".flac", ".m4a", ".mp3", ".ogg", ".wav", ".wma", ".m3u", ".m3u8"
        };

        public static WebApplication Create(MusicConfigStore store, ReaderCaptureState readerCapture, SpotifyHandler spotify, string configFileName, Func<bool> isReaderConnected, string listenUrl)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = Environment.CurrentDirectory,
                WebRootPath = Path.Combine(Environment.CurrentDirectory, "wwwroot")
            });
            builder.WebHost.UseUrls(listenUrl);
            builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaximumUploadSize);
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

            var app = builder.Build();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapGet("/api/library", () => Results.Ok(store.Snapshot()));
            app.MapGet("/api/status", () => Results.Ok(new { spotifyEnabled = spotify.Configuration.Enable, readerConnected = isReaderConnected() }));
            app.MapGet("/api/settings/spotify", () => Results.Ok(GetSpotifySettings(spotify)));
            app.MapGet("/api/spotify/receiver", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(GetReceiverStatus());
            });
            app.MapGet("/api/spotify/devices", async (HttpContext context, CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return await GetSpotifyDevicesAsync(spotify, cancellationToken);
            });
            app.MapGet("/api/spotify/auth", async (HttpContext context, CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await spotify.GetAuthorizationStatusAsync(cancellationToken));
            });
            app.MapPost("/api/spotify/auth/start", (HttpContext context, CancellationToken cancellationToken) =>
                SpotifyAuthorizationActionAsync(context, async () =>
                {
                    var sessionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                    var authorization = await spotify.BeginAuthorizationAsync(sessionId, cancellationToken);
                    context.Response.Cookies.Append("spotify-auth-session", sessionId, new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = context.Request.IsHttps,
                        SameSite = SameSiteMode.Strict,
                        Path = "/api/spotify/auth",
                        MaxAge = TimeSpan.FromMinutes(10)
                    });
                    return authorization;
                }));
            app.MapPost("/api/spotify/auth/complete", (CompleteSpotifyAuthorizationRequest request, HttpContext context, CancellationToken cancellationToken) =>
                SpotifyAuthorizationActionAsync(context, async () =>
                {
                    var sessionId = context.Request.Cookies["spotify-auth-session"];
                    if (string.IsNullOrEmpty(sessionId))
                        throw new InvalidOperationException("Start Connect Spotify in this browser before completing authorization.");
                    await spotify.CompleteAuthorizationAsync(sessionId, request.CallbackUrl, cancellationToken);
                    context.Response.Cookies.Delete("spotify-auth-session", new CookieOptions { Path = "/api/spotify/auth" });
                    return await spotify.GetAuthorizationStatusAsync(cancellationToken);
                }));
            app.MapPost("/api/spotify/auth/refresh", (HttpContext context, CancellationToken cancellationToken) =>
                SpotifyAuthorizationActionAsync(context, async () =>
                {
                    await spotify.RefreshAuthorizationAsync(cancellationToken);
                    return await spotify.GetAuthorizationStatusAsync(cancellationToken);
                }));
            app.MapGet("/api/spotify/search", (string? q, string? type, CancellationToken cancellationToken) =>
                SearchSpotifyAsync(q, type, spotify, cancellationToken));
            app.MapPut("/api/settings/spotify", async (UpdateSpotifySettingsRequest request) =>
            {
                if (request.Enable && (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.SpotifyDeviceName)))
                    return Results.BadRequest(new { message = "Client ID and device name are required when Spotify is enabled." });

                var current = spotify.Configuration;
                var receiver = request.Receiver ?? current.Receiver;
                if (receiver == null || receiver.Bitrate is not (96 or 160 or 320)
                    || string.IsNullOrEmpty(receiver.AudioDevice) || receiver.AudioDevice.Length > 128
                    || !Regex.IsMatch(receiver.AudioDevice, @"\A[A-Za-z0-9_:,=.\-]+\z"))
                    return Results.BadRequest(new { message = "Choose 96, 160, or 320 kbps and a valid ALSA audio device (for example default or plughw:CARD=Headphones,DEV=0)." });
                var deviceName = request.SpotifyDeviceName?.Trim() ?? "";
                if (deviceName.Length > 100 || deviceName.Any(char.IsControl)
                    || (request.Receiver != null && deviceName.Length == 0))
                    return Results.BadRequest(new { message = "Enter a receiver device name between 1 and 100 characters without control characters." });
                var configuration = new Config
                {
                    Enable = request.Enable,
                    ClientId = request.ClientId?.Trim() ?? "",
                    ClientSecret = request.ClientSecret ?? current.ClientSecret,
                    SpotifyDeviceName = deviceName,
                    Receiver = receiver,
                    TokenFileName = current.TokenFileName
                };
                try
                {
                    await spotify.UpdateConfigurationAsync(configuration, configFileName);
                    return Results.Ok(GetSpotifySettings(spotify));
                }
                catch (Exception exception)
                {
                    return Results.Problem($"Could not save Spotify settings: {exception.Message}");
                }
            });
            app.MapGet("/api/reader/capture", () => Results.Ok(readerCapture.GetSnapshot(isReaderConnected())));
            app.MapPost("/api/reader/capture", () =>
            {
                if (!isReaderConnected()) return Results.Conflict(new { message = "The portal reader is not connected." });

                readerCapture.BeginCapture();
                return Results.Accepted();
            });
            app.MapPut("/api/control-tags", (TrackControlTagsRequest request) => UpdateTrackControlTags(request, store));

            app.MapGet("/api/media", () => Results.Ok(ListMediaFiles()));
            app.MapPost("/api/media", UploadMediaAsync).DisableAntiforgery();
            app.MapPost("/api/tags", (CreateTagRequest request) => CreateTag(request, store, spotify.Configuration.Enable));
            app.MapPut("/api/tags/{uid}", (string uid, CreateTagRequest request) => UpdateTag(uid, request, store, spotify.Configuration.Enable));
            app.MapDelete("/api/tags/{uid}", (string uid) => DeleteTag(uid, store));

            return app;
        }

        private static SpotifySettingsResponse GetSpotifySettings(SpotifyHandler spotify)
        {
            var configuration = spotify.Configuration;
            return new(configuration.Enable, configuration.ClientId, configuration.SpotifyDeviceName,
                !string.IsNullOrEmpty(configuration.ClientSecret), configuration.Receiver);
        }

        private static ReceiverStatusResponse GetReceiverStatus()
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/usr/local/lib/musicdimension/configure-receiver.py"))
                return new(false, "unmanaged", false, "Receiver management is available after running the Raspberry Pi installer.", null, null, null, null);
            try
            {
                return JsonSerializer.Deserialize<ReceiverStatusResponse>(File.ReadAllText("/var/lib/musicdimension-receiver/status.json"),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new(true, "unknown", false, "Waiting for the receiver configuration service.", null, null, null, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return new(true, "unknown", false, "Waiting for the receiver configuration service. Check its systemd logs if this persists.", null, null, null, null);
            }
        }

        /// <summary>Receiver runtime and applied configuration reported by the root-owned systemd helper.</summary>
        public sealed record ReceiverStatusResponse(bool Managed, string ActiveState, bool CredentialsCached, string? Error,
            string? Name, string? AudioDevice, int? Bitrate, bool? Enabled);

        private static async Task<IResult> GetSpotifyDevicesAsync(SpotifyHandler spotify, CancellationToken cancellationToken)
        {
            try
            {
                return Results.Ok(await spotify.GetDevicesAsync(cancellationToken));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
            catch (SpotifyAPI.Web.APIException exception)
            {
                var status = (int?)exception.Response?.StatusCode;
                var message = status switch
                {
                    401 => "Spotify authorization is invalid. Connect Spotify again in Spotify settings.",
                    403 => "Spotify device access is not available for this app or account. Check Spotify app permissions.",
                    429 => "Spotify is rate limiting device requests. Wait before trying again.",
                    _ => "Spotify devices are temporarily unavailable. Try again later."
                };
                return Results.Json(new { message }, statusCode: status == 429 ? 429 : StatusCodes.Status502BadGateway);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(new { message = "Could not load Spotify devices. Check the network and Spotify authorization, then try again." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }

        private static async Task<IResult> SpotifyAuthorizationActionAsync(HttpContext context, Func<Task<object>> action)
        {
            context.Response.Headers.CacheControl = "no-store";
            var origin = context.Request.Headers.Origin.ToString();
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site"
                || (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                    || Uri.Compare(originUri, new Uri($"{context.Request.Scheme}://{context.Request.Host}"),
                        UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)))
                return Results.Json(new { message = "Start Spotify authorization from this web manager." }, statusCode: StatusCodes.Status403Forbidden);
            try
            {
                return Results.Ok(await action());
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(new { message = "Could not save or renew Spotify authorization. Check the network and config directory permissions, then try again. A new Connect Spotify login may be needed." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }

        /// <summary>The full callback URL returned after approving Spotify authorization.</summary>
        public sealed record CompleteSpotifyAuthorizationRequest(string? CallbackUrl);

        private static async Task<IResult> SearchSpotifyAsync(string? query, string? type, SpotifyHandler spotify, CancellationToken cancellationToken)
        {
            query = query?.Trim();
            if (string.IsNullOrEmpty(query) || query.Length > 200)
                return Results.BadRequest(new { message = "Enter a Spotify search between 1 and 200 characters." });
            if (type is not ("track" or "album" or "playlist"))
                return Results.BadRequest(new { message = "Choose tracks, albums, or playlists." });

            try
            {
                return Results.Ok(await spotify.SearchAsync(query, type, cancellationToken));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
            catch (SpotifyAPI.Web.APIException exception)
            {
                var status = (int?)exception.Response?.StatusCode;
                var message = status switch
                {
                    401 => "Spotify login has expired or is invalid. Complete Spotify login again.",
                    403 => "Spotify search is not available for this app or account. Check Spotify developer app access, or enter an ID manually.",
                    429 => "Spotify is rate limiting searches. Wait before trying again, or enter an ID manually.",
                    _ => "Spotify search is temporarily unavailable. Try again, or enter an ID manually."
                };
                return Results.Json(new { message }, statusCode: status == 429 ? 429 : StatusCodes.Status502BadGateway);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(new { message = "Could not connect to Spotify search. Check the connection and Spotify login, or enter an ID manually." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }

        /// <summary>Editable Spotify configuration; a null secret preserves its current value.</summary>
        public sealed record UpdateSpotifySettingsRequest(bool Enable, string? ClientId, string? SpotifyDeviceName, string? ClientSecret, ReceiverConfig? Receiver = null);

        /// <summary>Spotify settings without credentials or token file paths.</summary>
        public sealed record SpotifySettingsResponse(bool Enable, string ClientId, string SpotifyDeviceName, bool HasClientSecret, ReceiverConfig Receiver);

        private static IResult UpdateTrackControlTags(TrackControlTagsRequest request, MusicConfigStore store)
        {
            var forwardUid = NormalizeUid(request.ForwardUid);
            var rewindUid = NormalizeUid(request.RewindUid);
            if ((!string.IsNullOrWhiteSpace(request.ForwardUid) && forwardUid == null)
                || (!string.IsNullOrWhiteSpace(request.RewindUid) && rewindUid == null))
            {
                return Results.BadRequest(new { message = "Enter valid hexadecimal tag UIDs." });
            }

            if (forwardUid != null && string.Equals(forwardUid, rewindUid, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "Forward and rewind must use different tags." });
            }

            try
            {
                store.SetTrackControlTags(forwardUid, rewindUid);
            }
            catch (Exception exception)
            {
                return Results.Problem($"Could not save music.json: {exception.Message}");
            }

            return Results.Ok();
        }

        private static IResult CreateTag(CreateTagRequest request, MusicConfigStore store, bool spotifyEnabled)
        {
            var tag = BuildTag(request, spotifyEnabled, out var error);
            if (tag == null) return Results.BadRequest(new { message = error });

            try
            {
                if (!store.TryAddTag(tag)) return Results.Conflict(new { message = "A tag with this UID is already registered." });
            }
            catch (Exception exception)
            {
                return Results.Problem($"Could not save music.json: {exception.Message}");
            }

            return Results.Created($"/api/tags/{Uri.EscapeDataString(tag.Uid)}", new { uid = tag.Uid });
        }

        private static IResult UpdateTag(string routeUid, CreateTagRequest request, MusicConfigStore store, bool spotifyEnabled)
        {
            var uid = NormalizeUid(routeUid);
            if (uid == null || !string.Equals(uid, NormalizeUid(request.Uid), StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "The tag UID cannot be changed while editing." });
            }

            var tag = BuildTag(request, spotifyEnabled, out var error);
            if (tag == null) return Results.BadRequest(new { message = error });

            try
            {
                if (!store.TryUpdateTag(tag)) return Results.NotFound(new { message = "The tag no longer exists." });
            }
            catch (Exception exception)
            {
                return Results.Problem($"Could not save music.json: {exception.Message}");
            }

            return Results.Ok(new { uid });
        }

        private static IResult DeleteTag(string routeUid, MusicConfigStore store)
        {
            var uid = NormalizeUid(routeUid);
            if (uid == null) return Results.BadRequest(new { message = "Enter a valid hexadecimal tag UID." });

            try
            {
                if (!store.TryDeleteTag(uid)) return Results.NotFound(new { message = "The tag no longer exists." });
            }
            catch (Exception exception)
            {
                return Results.Problem($"Could not save music.json: {exception.Message}");
            }

            return Results.NoContent();
        }

        private static Tag? BuildTag(CreateTagRequest request, bool spotifyEnabled, out string? error)
        {
            error = null;
            var uid = NormalizeUid(request.Uid);
            if (uid == null)
            {
                error = "Enter a valid hexadecimal tag UID.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                error = "Enter a tag name.";
                return null;
            }

            var songs = new List<Song>();
            if (request.Type == SourceTypes.Local)
            {
                if (request.Paths == null || request.Paths.Count == 0)
                {
                    error = "Add at least one audio or playlist file.";
                    return null;
                }

                foreach (var configuredPath in request.Paths)
                {
                    var relativePath = GetLocalMediaPath(configuredPath);
                    if (relativePath == null)
                    {
                        error = "One or more files are outside the media folder.";
                        return null;
                    }

                    var mediaPath = Path.GetFullPath(Path.Combine(GetMediaDirectory(), relativePath));
                    if (!File.Exists(mediaPath))
                    {
                        error = $"The selected media file no longer exists: {Path.GetFileName(mediaPath)}";
                        return null;
                    }

                    songs.Add(new Song
                    {
                        Name = Path.GetFileNameWithoutExtension(mediaPath),
                        Type = SourceTypes.Local,
                        Path = $"media#{relativePath.Replace(Path.DirectorySeparatorChar, '#').Replace(Path.AltDirectorySeparatorChar, '#')}"
                    });
                }
            }
            else if (request.Type == SourceTypes.Spotify)
            {
                if (!spotifyEnabled)
                {
                    error = "Spotify is disabled in config.json.";
                    return null;
                }

                if (request.SpotifyType == null || string.IsNullOrWhiteSpace(request.SpotifyId))
                {
                    error = "Choose a Spotify type and enter its ID.";
                    return null;
                }

                songs.Add(new Song
                {
                    Name = request.Name.Trim(),
                    Type = SourceTypes.Spotify,
                    SpotifyType = request.SpotifyType,
                    SpotifyId = request.SpotifyId.Trim()
                });
            }
            else
            {
                error = "Choose Local or Spotify as the source.";
                return null;
            }

            return new Tag { Uid = uid, Name = request.Name.Trim(), Songs = songs };
        }

        private static async Task<IResult> UploadMediaAsync(HttpRequest request)
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null) return Results.BadRequest(new { message = "Choose a file to upload." });
            if (file.Length == 0) return Results.BadRequest(new { message = "The selected file is empty." });
            if (file.Length > MaximumUploadSize) return Results.BadRequest(new { message = "The upload exceeds the 512 MB limit." });

            var uid = NormalizeUid(form["uid"].FirstOrDefault());
            if (uid == null) return Results.BadRequest(new { message = "Enter or scan a valid tag UID before uploading files." });

            var extension = Path.GetExtension(file.FileName);
            if (!SupportedExtensions.Contains(extension))
            {
                return Results.BadRequest(new { message = "Upload an audio file or an M3U/M3U8 playlist." });
            }

            var originalName = Path.GetFileName(file.FileName);
            var stem = SafeFileStem(Path.GetFileNameWithoutExtension(originalName));
            if (stem.Length == 0) stem = "media";

            var mediaDirectory = Path.Combine(GetMediaDirectory(), uid);
            Directory.CreateDirectory(mediaDirectory);
            var fileName = GetAvailableName(mediaDirectory, stem, extension);
            var destination = Path.Combine(mediaDirectory, fileName);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(output);
            }

            return Results.Created("/api/media", new MediaFileInfo(fileName, $"media#{uid}#{fileName}", extension, file.Length));
        }

        private static List<MediaFileInfo> ListMediaFiles()
        {
            var directory = GetMediaDirectory();
            Directory.CreateDirectory(directory);
            var root = Path.GetFullPath(directory);
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file =>
                {
                    var relativePath = Path.GetRelativePath(root, file.FullName);
                    var portablePath = relativePath.Replace(Path.DirectorySeparatorChar, '#').Replace(Path.AltDirectorySeparatorChar, '#');
                    return new MediaFileInfo(file.Name, $"media#{portablePath}", Path.GetExtension(file.Name), file.Length);
                })
                .ToList();
        }

        private static string? GetLocalMediaPath(string? configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath)) return null;

            var normalized = configuredPath.Replace('#', '/').Replace('\\', '/');
            if (!normalized.StartsWith("media/", StringComparison.OrdinalIgnoreCase)) return null;
            var name = normalized["media/".Length..];
            if (name.Split('/').Any(segment => segment is "" or "." or "..")) return null;
            if (!SupportedExtensions.Contains(Path.GetExtension(name))) return null;

            var mediaRoot = Path.GetFullPath(GetMediaDirectory()) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.Combine(GetMediaDirectory(), name.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return fullPath.StartsWith(mediaRoot, comparison) ? name.Replace('/', Path.DirectorySeparatorChar) : null;
        }

        private static string? NormalizeUid(string? uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return null;

            var compact = new string(uid.Where(character => character != '-' && character != ':' && !char.IsWhiteSpace(character)).ToArray());
            if (compact.Length < 2 || compact.Length % 2 != 0 || compact.Any(character => !Uri.IsHexDigit(character))) return null;

            return string.Join('-', Enumerable.Range(0, compact.Length / 2)
                .Select(index => compact.Substring(index * 2, 2).ToUpperInvariant()));
        }

        private static string GetMediaDirectory() => Path.Combine(Environment.CurrentDirectory, "media");

        private static string SafeFileStem(string value) => SafeFileStemRegex().Replace(value, "_").Trim(' ', '.', '_');

        private static string GetAvailableName(string directory, string stem, string extension)
        {
            var candidate = stem + extension;
            for (var suffix = 2; File.Exists(Path.Combine(directory, candidate)); suffix++)
            {
                candidate = $"{stem}-{suffix}{extension}";
            }

            return candidate;
        }

        [GeneratedRegex("[^\\p{L}\\p{Nd} _.-]")]
        private static partial Regex SafeFileStemRegex();
    }

    public sealed record CreateTagRequest(string Uid, string Name, SourceTypes Type, List<string>? Paths, SpotifyTypes? SpotifyType, string? SpotifyId);
    public sealed record TrackControlTagsRequest(string? ForwardUid, string? RewindUid);
    public sealed record MediaFileInfo(string Name, string Path, string Extension, long Size);
}