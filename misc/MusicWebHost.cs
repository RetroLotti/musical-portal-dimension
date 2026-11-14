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
        private const int MaximumUploadFileSizeMb = 4096;
        private const long MaximumBackupFileSizeBytes = 5 * 1024 * 1024;
        private const long MultipartOverheadBytes = 1024 * 1024;
        private static readonly long MaximumRequestSize = (long)MaximumUploadFileSizeMb * 1024 * 1024 + MultipartOverheadBytes;
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".aac", ".flac", ".m4a", ".mp3", ".ogg", ".wav", ".wma", ".m3u", ".m3u8"
        };
        private static readonly JsonSerializerOptions BackupJsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public static WebApplication Create(MusicConfigStore store, ReaderCaptureState readerCapture, SpotifyHandler spotify, string configFileName, Func<bool> isReaderConnected, string listenUrl, Func<string, Task> executePlaybackControlAsync, Func<Task<PlaybackStatus>> getPlaybackStatusAsync)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = Environment.CurrentDirectory,
                WebRootPath = Path.Combine(Environment.CurrentDirectory, "wwwroot")
            });
            builder.WebHost.UseUrls(listenUrl);
            builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaximumRequestSize);
            builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
                options.MultipartBodyLengthLimit = (long)MaximumUploadFileSizeMb * 1024 * 1024);
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

            var app = builder.Build();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapGet("/api/library", () => Results.Ok(store.Snapshot()));
            app.MapGet("/api/backup", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var configuration = spotify.Configuration;
                var backup = new MusicDimensionBackup(1, DateTimeOffset.UtcNow,
                    new BackupSettings(configuration.Enable, configuration.ClientId, configuration.SpotifyDeviceName,
                        configuration.Receiver.Enabled, configuration.Receiver.AudioDevice, configuration.Receiver.Bitrate,
                        configuration.MediaMaxFileSizeMb, configuration.InitialVolume), store.Snapshot());
                var bytes = JsonSerializer.SerializeToUtf8Bytes(backup, BackupJsonOptions);
                return Results.File(bytes, "application/json", "musicdimension-backup.json");
            });
            app.MapPost("/api/backup", (HttpRequest request, CancellationToken cancellationToken) =>
                RestoreBackupAsync(request, cancellationToken, store, spotify, configFileName));
            app.MapGet("/api/status", () => Results.Ok(new { spotifyEnabled = spotify.Configuration.Enable, readerConnected = isReaderConnected() }));
            app.MapGet("/api/settings/spotify", () => Results.Ok(GetSpotifySettings(spotify)));
            app.MapGet("/api/settings/media", () => Results.Ok(new MediaSettingsResponse(spotify.Configuration.MediaMaxFileSizeMb)));
            app.MapGet("/api/settings/playback", () => Results.Ok(new PlaybackSettingsResponse(spotify.Configuration.InitialVolume)));
            app.MapPut("/api/settings/playback", async (UpdatePlaybackSettingsRequest request) =>
            {
                if (request.InitialVolume is < 0 or > 100)
                    return Results.BadRequest(new { message = "Choose an initial volume between 0 and 100, or leave it blank to keep the current volume." });
                try
                {
                    await spotify.UpdateInitialVolumeAsync(request.InitialVolume, configFileName);
                    return Results.Ok(new PlaybackSettingsResponse(spotify.Configuration.InitialVolume));
                }
                catch (Exception exception)
                {
                    return Results.Problem($"Could not save playback settings: {exception.Message}");
                }
            });
            app.MapPost("/api/playback/control/{action}", (string action, HttpContext context) =>
                ExecutePlaybackControlAsync(context, action, executePlaybackControlAsync));
            app.MapGet("/api/playback/status", async (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await getPlaybackStatusAsync());
            });
            app.MapPut("/api/settings/media", async (UpdateMediaSettingsRequest request) =>
            {
                if (request.MaxFileSizeMb is < 1 or > MaximumUploadFileSizeMb)
                    return Results.BadRequest(new { message = $"Choose a file size between 1 and {MaximumUploadFileSizeMb} MB." });
                try
                {
                    await spotify.UpdateMediaMaxFileSizeMbAsync(request.MaxFileSizeMb, configFileName);
                    return Results.Ok(new MediaSettingsResponse(spotify.Configuration.MediaMaxFileSizeMb));
                }
                catch (Exception exception)
                {
                    return Results.Problem($"Could not save media settings: {exception.Message}");
                }
            });
            app.MapGet("/api/spotify/receiver", async (HttpContext context, CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await GetReceiverStatusAsync(cancellationToken));
            });
            app.MapGet("/api/audio/bluetooth", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(BluetoothManager.GetStatus());
            });
            app.MapPost("/api/audio/bluetooth/scan", (HttpContext context) => StartBluetoothOperation(context, "scan"));
            app.MapPost("/api/audio/bluetooth/refresh", (HttpContext context) => StartBluetoothOperation(context, "refresh"));
            app.MapPost("/api/audio/bluetooth/connect", (BluetoothConnectRequest request, HttpContext context) =>
                StartBluetoothOperation(context, "connect", request.Address));
            app.MapGet("/api/service-logs", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(ServiceLogsManager.GetStatus());
            });
            app.MapPost("/api/service-logs/{service}", (string service, ServiceLogsRequest request, HttpContext context) =>
                StartServiceLogRequest(context, service, request.Lines));
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
                if (receiver.SoloistApiKey != null && (receiver.SoloistApiKey.Length > 4096
                    || receiver.SoloistApiKey.Any(character => character < 33 || character > 126)))
                    return Results.BadRequest(new { message = "Enter a valid Soloist API key without whitespace (maximum 4096 characters)." });
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
                    MediaMaxFileSizeMb = current.MediaMaxFileSizeMb,
                    InitialVolume = current.InitialVolume,
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
            app.MapPost("/api/media", (HttpRequest request) => UploadMediaAsync(request, () => spotify.Configuration.MediaMaxFileSizeMb)).DisableAntiforgery();
            app.MapPost("/api/tags", (CreateTagRequest request) => CreateTag(request, store, spotify.Configuration.Enable));
            app.MapPut("/api/tags/{uid}", (string uid, CreateTagRequest request) => UpdateTag(uid, request, store, spotify.Configuration.Enable));
            app.MapDelete("/api/tags/{uid}", (string uid) => DeleteTag(uid, store));

            return app;
        }

        private static async Task<IResult> RestoreBackupAsync(HttpRequest request, CancellationToken cancellationToken,
            MusicConfigStore store, SpotifyHandler spotify, string configFileName)
        {
            if (request.ContentLength is null or <= 0 or > MaximumBackupFileSizeBytes)
                return Results.BadRequest(new { message = "Choose a valid backup file smaller than 5 MiB." });

            MusicDimensionBackup? backup;
            try
            {
                backup = await JsonSerializer.DeserializeAsync<MusicDimensionBackup>(request.Body, BackupJsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { message = "The selected file is not a valid Music Dimension backup." });
            }

            if (backup?.Version != 1 || backup.Settings == null || backup.Library == null)
                return Results.BadRequest(new { message = "The selected file is not a supported Music Dimension backup." });

            var validationError = ValidateBackup(backup);
            if (validationError != null) return Results.BadRequest(new { message = validationError });

            var current = spotify.Configuration;
            var previousLibrary = store.Snapshot();
            var settings = backup.Settings;
            var restoredConfiguration = new Config
            {
                Enable = settings.Enable,
                ClientId = settings.ClientId!,
                ClientSecret = current.ClientSecret,
                SpotifyDeviceName = settings.SpotifyDeviceName!,
                Receiver = new ReceiverConfig
                {
                    Enabled = settings.ReceiverEnabled,
                    AudioDevice = settings.AudioDevice!,
                    Bitrate = settings.Bitrate
                },
                MediaMaxFileSizeMb = settings.MediaMaxFileSizeMb,
                InitialVolume = settings.InitialVolume,
                TokenFileName = current.TokenFileName
            };

            var configurationUpdated = false;
            try
            {
                await spotify.UpdateConfigurationAsync(restoredConfiguration, configFileName);
                configurationUpdated = true;
                store.Replace(backup.Library);
                return Results.Ok(new { restored = true, mediaIncluded = false, secretsIncluded = false });
            }
            catch (Exception exception)
            {
                if (configurationUpdated)
                {
                    try { await spotify.UpdateConfigurationAsync(current, configFileName); }
                    catch (Exception rollbackException) { Console.WriteLine($"Could not roll back config after backup restore: {rollbackException.Message}"); }
                }
                try { store.Replace(previousLibrary); }
                catch (Exception rollbackException) { Console.WriteLine($"Could not roll back music.json after backup restore: {rollbackException.Message}"); }
                return Results.Problem($"Could not restore the backup: {exception.Message}");
            }
        }

        private static string? ValidateBackup(MusicDimensionBackup backup)
        {
            var settings = backup.Settings!;
            if (settings.ClientId == null || settings.ClientId.Length > 256
                || string.IsNullOrWhiteSpace(settings.SpotifyDeviceName) || settings.SpotifyDeviceName.Length > 100
                || settings.SpotifyDeviceName.Any(char.IsControl)
                || (settings.Enable && string.IsNullOrWhiteSpace(settings.ClientId)))
                return "The backup contains invalid Spotify settings.";
            if (settings.Bitrate is not (96 or 160 or 320)
                || settings.AudioDevice == null || !Regex.IsMatch(settings.AudioDevice, @"\A[A-Za-z0-9_:,.\-]{1,128}\Z")
                || settings.MediaMaxFileSizeMb is < 1 or > MaximumUploadFileSizeMb
                || settings.InitialVolume is < 0 or > 100)
                return "The backup contains invalid audio or media settings.";

            var library = backup.Library!;
            if (library.RegisteredTags == null || library.RegisteredTags.Count > 10000)
                return "The backup tag library is invalid or too large.";
            var tagUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in library.RegisteredTags)
            {
                if (tag == null || (tag.Uid = NormalizeUid(tag.Uid) ?? "").Length == 0 || !tagUids.Add(tag.Uid)
                    || string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Length > 100)
                    return "The backup contains an invalid tag or duplicate tag UID.";
                tag.AudioOutput ??= "headphones";
                if (!string.Equals(tag.AudioOutput, "headphones", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(tag.AudioOutput, @"\Abluetooth:(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\Z"))
                    return $"Tag {tag.Uid} has an invalid audio output.";
                if (tag.Songs == null || tag.Songs.Count is < 1 or > 100)
                    return $"Tag {tag.Uid} has an invalid song list.";
                if (tag.Songs.Any(song => song == null))
                    return $"Tag {tag.Uid} contains an empty song entry.";

                var source = tag.Songs[0].Type;
                if (source is not (SourceTypes.Local or SourceTypes.Spotify) || tag.Songs.Any(song => song.Type != source))
                    return $"Tag {tag.Uid} mixes unsupported playback sources.";
                if (source == SourceTypes.Local && tag.Songs.Any(song => GetLocalMediaPath(song.Path) == null))
                    return $"Tag {tag.Uid} has an invalid media path.";
                if (source == SourceTypes.Spotify && (tag.Songs.Count > 1 && tag.Songs.Any(song => song.SpotifyType != SpotifyTypes.Track)
                    || tag.Songs.Any(song => song.SpotifyType == null || !Enum.IsDefined(song.SpotifyType.Value)
                        || string.IsNullOrWhiteSpace(song.SpotifyId) || !Regex.IsMatch(song.SpotifyId, @"\A[A-Za-z0-9]{1,128}\Z"))))
                    return $"Tag {tag.Uid} has invalid Spotify items.";
            }

            if (library.ControlTags == null || library.ControlTags.Count > 10000)
                return "The backup control-tag map is invalid.";
            var normalizedControls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var validActions = new HashSet<string>(["next", "previous", "volumeup", "volumedown", "shuffle", "stop", "pause", "play"], StringComparer.Ordinal);
            foreach (var (uid, action) in library.ControlTags)
            {
                var normalizedUid = NormalizeUid(uid);
                if (normalizedUid == null || string.IsNullOrWhiteSpace(action))
                    return "The backup contains an invalid control-tag assignment.";
                var normalizedAction = MusicConfigStore.NormalizeControlAction(action);
                if (!validActions.Contains(normalizedAction) || !normalizedControls.TryAdd(normalizedUid, normalizedAction))
                    return "The backup contains an invalid control-tag assignment.";
            }
            library.ControlTags = normalizedControls;
            return null;
        }

        private static SpotifySettingsResponse GetSpotifySettings(SpotifyHandler spotify)
        {
            var configuration = spotify.Configuration;
            return new(configuration.Enable, configuration.ClientId, configuration.SpotifyDeviceName,
                !string.IsNullOrEmpty(configuration.ClientSecret), new ReceiverSettingsResponse(configuration.Receiver.Enabled,
                    configuration.Receiver.AudioDevice, configuration.Receiver.Bitrate, spotify.HasSoloistApiKey));
        }

        private static IResult StartBluetoothOperation(HttpContext context, string operation, string? address = null)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site"
                || (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                    || Uri.Compare(originUri, new Uri($"{context.Request.Scheme}://{context.Request.Host}"),
                        UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)))
                return Results.Json(new { message = "Start Bluetooth setup from this web manager." }, statusCode: StatusCodes.Status403Forbidden);

            try
            {
                return Results.Accepted(value: new { requestId = BluetoothManager.StartOperation(operation, address) });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Problem("Could not queue Bluetooth setup. Check that the updated Pi installer is installed.");
            }
        }

        private static IResult StartServiceLogRequest(HttpContext context, string service, int lines)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site"
                || (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                    || Uri.Compare(originUri, new Uri($"{context.Request.Scheme}://{context.Request.Host}"),
                        UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)))
                return Results.Json(new { message = "Read service logs from this web manager." }, statusCode: StatusCodes.Status403Forbidden);

            try
            {
                return Results.Accepted(value: new { requestId = ServiceLogsManager.StartRequest(service, lines) });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Problem("Could not queue the service log request. Check that the updated Pi installer is installed.");
            }
        }

        private static async Task<IResult> ExecutePlaybackControlAsync(HttpContext context, string action, Func<string, Task> execute)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site"
                || (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                    || Uri.Compare(originUri, new Uri($"{context.Request.Scheme}://{context.Request.Host}"),
                        UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)))
                return Results.Json(new { message = "Use the playback controls from this web manager." }, statusCode: StatusCodes.Status403Forbidden);
            if (action is not ("next" or "previous" or "volumeup" or "volumedown" or "shuffle"))
                return Results.BadRequest(new { message = "Choose a supported playback action." });

            try
            {
                await execute(action);
                return Results.Ok(new { action });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
            catch (Exception exception)
            {
                return Results.Problem($"Could not run the playback action: {exception.Message}");
            }
        }

        private static async Task<ReceiverStatusResponse> GetReceiverStatusAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/usr/local/lib/musicdimension/configure-receiver.py"))
                return new(false, "unmanaged", false, "Receiver management is available after running the Raspberry Pi installer.", null, null, null, null);
            try
            {
                var status = JsonSerializer.Deserialize<ReceiverStatusResponse>(await File.ReadAllTextAsync("/var/lib/musicdimension-receiver/status.json", cancellationToken),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new(true, "unknown", false, "Waiting for the receiver configuration service.", null, null, null, null);
                if (status.Backend != "soloist")
                    return status with { Error = "Run the updated Pi installer to replace the old Raspotify receiver with Spotify Soloist." };
                if (status.ActiveState == "active")
                {
                    var loggedIn = await ReadSoloistLoginAsync(new Uri("ws://127.0.0.1:9876"), cancellationToken);
                    return status with { LoggedIn = loggedIn, CredentialsCached = loggedIn == true };
                }
                return status;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return new(true, "unknown", false, "Waiting for the receiver configuration service. Check its systemd logs if this persists.", null, null, null, null);
            }
        }

        private static async Task<bool?> ReadSoloistLoginAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            if (!endpoint.IsLoopback || endpoint.Scheme != "ws") throw new ArgumentException("Soloist status must use a loopback WebSocket.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var socket = new System.Net.WebSockets.ClientWebSocket();
            socket.Options.Proxy = null;
            try
            {
                await socket.ConnectAsync(endpoint, timeout.Token);
                var buffer = new byte[16384];
                for (var message = 0; message < 8; message++)
                {
                    var count = 0;
                    System.Net.WebSockets.WebSocketReceiveResult result;
                    do
                    {
                        if (count == buffer.Length) return null;
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), timeout.Token);
                        if (result.MessageType != System.Net.WebSockets.WebSocketMessageType.Text) return null;
                        count += result.Count;
                    } while (!result.EndOfMessage);
                    using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
                    if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "auth_state"
                        && document.RootElement.TryGetProperty("logged_in", out var loggedIn) && loggedIn.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        return loggedIn.GetBoolean();
                }
            }
            catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or JsonException
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            return null;
        }

        /// <summary>Receiver runtime and applied configuration reported by the root-owned systemd helper.</summary>
        public sealed record ReceiverStatusResponse(bool Managed, string ActiveState, bool CredentialsCached, string? Error,
            string? Name, string? AudioDevice, int? Bitrate, bool? Enabled, string? Backend = null,
            bool? LoggedIn = null, bool ApiKeyConfigured = false, JsonElement? Update = null);

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
        public sealed record SpotifySettingsResponse(bool Enable, string ClientId, string SpotifyDeviceName, bool HasClientSecret, ReceiverSettingsResponse Receiver);

        /// <summary>Receiver settings without the private Spotify Soloist API key.</summary>
        public sealed record ReceiverSettingsResponse(bool Enabled, string AudioDevice, int Bitrate, bool HasSoloistApiKey);

        /// <summary>Maximum accepted media file size in MiB.</summary>
        public sealed record MediaSettingsResponse(int MaxFileSizeMb);

        public sealed record PlaybackSettingsResponse(int? InitialVolume);

        public sealed record UpdatePlaybackSettingsRequest(int? InitialVolume);

        /// <summary>Requested maximum media file size in MiB.</summary>
        public sealed record UpdateMediaSettingsRequest(int MaxFileSizeMb);

        private static IResult UpdateTrackControlTags(TrackControlTagsRequest request, MusicConfigStore store)
        {
            var controls = new Dictionary<string, string?>();
            var values = new[] { ("next", request.ForwardUid), ("previous", request.RewindUid),
                ("volumeup", request.VolumeUpUid), ("volumedown", request.VolumeDownUid), ("shuffle", request.ShuffleUid) };
            foreach (var (action, value) in values)
            {
                if (value == null && action is not ("next" or "previous")) continue;
                var normalized = NormalizeUid(value);
                if (!string.IsNullOrWhiteSpace(value) && normalized == null)
                    return Results.BadRequest(new { message = "Enter valid hexadecimal tag UIDs." });
                controls[action] = normalized;
            }

            var assignedUids = controls.Values.Where(uid => uid != null).ToList();
            if (assignedUids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != assignedUids.Count)
            {
                return Results.BadRequest(new { message = "Each playback control must use a different tag." });
            }

            try
            {
                store.SetControlTags(controls);
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
            var audioOutput = request.AudioOutput?.Trim() ?? "headphones";
            if (!string.Equals(audioOutput, "headphones", StringComparison.OrdinalIgnoreCase)
                && !Regex.IsMatch(audioOutput, @"\Abluetooth:(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\Z"))
            {
                error = "Choose Headphones or a valid paired Bluetooth device.";
                return null;
            }
            if (audioOutput.StartsWith("bluetooth:", StringComparison.OrdinalIgnoreCase))
                audioOutput = "bluetooth:" + audioOutput[10..].ToUpperInvariant();

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

                var items = request.SpotifyItems ?? [new SpotifyTagItemRequest(request.Name, request.SpotifyType, request.SpotifyId)];
                if (items.Count is < 1 or > 100)
                {
                    error = "Add between 1 and 100 Spotify tracks, or one album or playlist.";
                    return null;
                }

                if (items.Count > 1 && items.Any(item => item == null || item.SpotifyType != SpotifyTypes.Track))
                {
                    error = "Only individual Spotify tracks can be combined. Use an album or playlist on its own.";
                    return null;
                }

                foreach (var item in items)
                {
                    if (item == null || item.SpotifyType == null || !Enum.IsDefined(item.SpotifyType.Value) || string.IsNullOrWhiteSpace(item.SpotifyId))
                    {
                        error = "Choose a valid Spotify type and enter an ID for every item.";
                        return null;
                    }
                    songs.Add(new Song
                    {
                        Name = string.IsNullOrWhiteSpace(item.Name) ? request.Name.Trim() : item.Name.Trim(),
                        Type = SourceTypes.Spotify,
                        SpotifyType = item.SpotifyType,
                        SpotifyId = item.SpotifyId.Trim()
                    });
                }
            }
            else
            {
                error = "Choose Local or Spotify as the source.";
                return null;
            }

            return new Tag
            {
                Uid = uid,
                Name = request.Name.Trim(),
                Songs = songs,
                ResumePlayback = request.ResumePlayback,
                AudioOutput = audioOutput
            };
        }

        private static async Task<IResult> UploadMediaAsync(HttpRequest request, Func<int> getMaximumFileSizeMb)
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null) return Results.BadRequest(new { message = "Choose a file to upload." });
            if (file.Length == 0) return Results.BadRequest(new { message = "The selected file is empty." });
            var maximumFileSize = getMaximumFileSizeMb();
            var maximumBytes = (long)maximumFileSize * 1024 * 1024;
            if (file.Length > maximumBytes)
                return Results.Json(new { message = $"The selected file is {FormatMegabytes(file.Length)} MiB; the current limit is {maximumFileSize} MiB." }, statusCode: StatusCodes.Status413PayloadTooLarge);

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

        private static string FormatMegabytes(long bytes) => (bytes / (1024d * 1024d)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

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

    public sealed record CreateTagRequest(string Uid, string Name, SourceTypes Type, List<string>? Paths, SpotifyTypes? SpotifyType, string? SpotifyId,
        List<SpotifyTagItemRequest>? SpotifyItems = null, bool ResumePlayback = false, string? AudioOutput = null);

    /// <summary>A Spotify item in a tag's saved playback order.</summary>
    public sealed record SpotifyTagItemRequest(string? Name, SpotifyTypes? SpotifyType, string? SpotifyId);
    public sealed record TrackControlTagsRequest(string? ForwardUid, string? RewindUid,
        string? VolumeUpUid = null, string? VolumeDownUid = null, string? ShuffleUid = null);
    public sealed record MediaFileInfo(string Name, string Path, string Extension, long Size);
}