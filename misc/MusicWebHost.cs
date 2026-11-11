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

        public static WebApplication Create(MusicConfigStore store, ReaderCaptureState readerCapture, bool spotifyEnabled, Func<bool> isReaderConnected)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = Environment.CurrentDirectory,
                WebRootPath = Path.Combine(Environment.CurrentDirectory, "wwwroot")
            });
            builder.WebHost.UseUrls("http://127.0.0.1:5080");
            builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaximumUploadSize);
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

            var app = builder.Build();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapGet("/api/library", () => Results.Ok(store.Snapshot()));
            app.MapGet("/api/status", () => Results.Ok(new { spotifyEnabled, readerConnected = isReaderConnected() }));
            app.MapGet("/api/reader/capture", () => Results.Ok(readerCapture.GetSnapshot(isReaderConnected())));
            app.MapPost("/api/reader/capture", () =>
            {
                if (!isReaderConnected()) return Results.Conflict(new { message = "The portal reader is not connected." });

                readerCapture.BeginCapture();
                return Results.Accepted();
            });

            app.MapPost("/api/media", UploadMediaAsync).DisableAntiforgery();
            app.MapPost("/api/tags", (CreateTagRequest request) => CreateTag(request, store, spotifyEnabled));

            return app;
        }

        private static IResult CreateTag(CreateTagRequest request, MusicConfigStore store, bool spotifyEnabled)
        {
            var uid = NormalizeUid(request.Uid);
            if (uid == null) return Results.BadRequest(new { message = "Enter a valid hexadecimal tag UID." });
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { message = "Enter a tag name." });
            if (request.Type == SourceTypes.Spotify && !spotifyEnabled)
            {
                return Results.BadRequest(new { message = "Spotify is disabled in config.json." });
            }

            var songs = new List<Song>();
            if (request.Type == SourceTypes.Local)
            {
                if (request.Paths == null || request.Paths.Count == 0)
                {
                    return Results.BadRequest(new { message = "Upload at least one audio or playlist file." });
                }

                foreach (var configuredPath in request.Paths)
                {
                    var relativePath = GetLocalMediaPath(configuredPath, uid);
                    if (relativePath == null)
                    {
                        return Results.BadRequest(new { message = "One or more files do not belong to this tag's upload folder." });
                    }

                    var mediaPath = Path.GetFullPath(Path.Combine(GetMediaDirectory(), relativePath));
                    if (!File.Exists(mediaPath)) return Results.BadRequest(new { message = $"The selected media file no longer exists: {Path.GetFileName(mediaPath)}" });

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
                if (request.SpotifyType == null || string.IsNullOrWhiteSpace(request.SpotifyId))
                {
                    return Results.BadRequest(new { message = "Choose a Spotify type and enter its ID." });
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
                return Results.BadRequest(new { message = "Choose Local or Spotify as the source." });
            }

            var tag = new Tag { Uid = uid, Name = request.Name.Trim(), Songs = songs };
            try
            {
                if (!store.TryAddTag(tag)) return Results.Conflict(new { message = "A tag with this UID is already registered." });
            }
            catch (Exception exception)
            {
                return Results.Problem($"Could not save music.json: {exception.Message}");
            }

            return Results.Created($"/api/tags/{Uri.EscapeDataString(uid)}", new { uid });
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

        private static string? GetLocalMediaPath(string? configuredPath, string uid)
        {
            if (string.IsNullOrWhiteSpace(configuredPath)) return null;

            var normalized = configuredPath.Replace('#', '/').Replace('\\', '/');
            var expectedPrefix = $"media/{uid}/";
            if (!normalized.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) return null;
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
    public sealed record MediaFileInfo(string Name, string Path, string Extension, long Size);
}