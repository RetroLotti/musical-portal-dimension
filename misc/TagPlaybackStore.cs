namespace MusicDimension.misc
{
    using System.Text.Json;
    using MusicDimension.models;

    public sealed class TagPlaybackStore
    {
        private static readonly JsonSerializerOptions Settings = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private readonly object _sync = new();
        private readonly string _filePath;
        private readonly Dictionary<string, TagPlaybackState> _states;

        public TagPlaybackStore(string filePath)
        {
            _filePath = Path.GetFullPath(filePath);
            try
            {
                if (!File.Exists(_filePath))
                {
                    _states = new Dictionary<string, TagPlaybackState>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    var json = File.ReadAllText(_filePath);
                    using var document = JsonDocument.Parse(json);
                    var hasBackendState = document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.EnumerateObject().Any(pair => pair.Value.ValueKind == JsonValueKind.Object
                            && (pair.Value.TryGetProperty("local", out _) || pair.Value.TryGetProperty("spotify", out _)));
                    if (hasBackendState)
                    {
                        var saved = JsonSerializer.Deserialize<Dictionary<string, TagPlaybackState>>(json, Settings);
                        _states = saved?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                            ?? new Dictionary<string, TagPlaybackState>(StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        var legacy = JsonSerializer.Deserialize<Dictionary<string, LocalPlaybackState>>(json, Settings);
                        _states = legacy?.Where(pair => pair.Value != null)
                            .ToDictionary(pair => pair.Key, pair => new TagPlaybackState(pair.Value, null), StringComparer.OrdinalIgnoreCase)
                            ?? new Dictionary<string, TagPlaybackState>(StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Saved tag playback positions could not be read: {exception.Message}");
                _states = new Dictionary<string, TagPlaybackState>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public LocalPlaybackState? GetLocal(string uid)
        {
            lock (_sync)
            {
                var state = _states.GetValueOrDefault(uid)?.Local;
                return state?.PlayOrder != null
                    ? state with { PlayOrder = state.PlayOrder.ToArray() }
                    : null;
            }
        }

        public SpotifyPlaybackState? GetSpotify(string uid)
        {
            lock (_sync)
            {
                return _states.GetValueOrDefault(uid)?.Spotify;
            }
        }

        public void SetLocal(string uid, LocalPlaybackState state) =>
            Update(uid, previous => (previous ?? new(null, null)) with { Local = state });

        public void SetSpotify(string uid, SpotifyPlaybackState state) =>
            Update(uid, previous => (previous ?? new(null, null)) with { Spotify = state });

        public void Remove(string uid)
        {
            lock (_sync)
            {
                if (!_states.Remove(uid, out var previous)) return;
                try
                {
                    Save();
                }
                catch
                {
                    _states[uid] = previous;
                    throw;
                }
            }
        }

        private void Update(string uid, Func<TagPlaybackState?, TagPlaybackState> update)
        {
            lock (_sync)
            {
                var hadPrevious = _states.TryGetValue(uid, out var previous);
                _states[uid] = update(previous);
                try
                {
                    Save();
                }
                catch
                {
                    if (hadPrevious) _states[uid] = previous!;
                    else _states.Remove(uid);
                    throw;
                }
            }
        }

        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temporaryPath = _filePath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_states, Settings));
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporaryPath, _filePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}