namespace MusicDimension
{
    using misc;
    using models;
    using LegoTagEventArgs = LegoDimensions.LegoTagEventArgs;

    internal static class Program
    {
        private static readonly string MusicFileName = Path.Combine($"{Environment.CurrentDirectory}", "config", "music.json");
        private static readonly string ConfigFileName = Path.Combine($"{Environment.CurrentDirectory}", "config", "config.json");
        private static readonly string TokenFileName = Path.Combine($"{Environment.CurrentDirectory}", "config", "tokens.json");

        private static PortalHandler _portalHandler = null!;
        private static SpotifyHandler _spotifyHandler = null!;
        private static LocalMusicHandler _localMusicHandler = null!;

        private static Config _config = null!;
        private static MusicConfigStore _musicStore = null!;
        private static readonly ReaderCaptureState ReaderCapture = new();
        private static string? _currentTagUid;
        private static SourceTypes? _currentPlaybackSource;
        private static string? _pausedTagUid;
        private static SourceTypes? _pausedPlaybackSource;

        private static async Task Main(string[] args)
        {
            // check for config files
            if (!CheckFiles()) return;

            // read general config from file
#pragma warning disable CS8601 // Possible null reference assignment.
            _config = JsonFileHandler.ReadObjectFromJsonFile<Config>(ConfigFileName);
            Console.WriteLine($"Object read from {ConfigFileName}");
#pragma warning restore CS8601 // Possible null reference assignment.
            if (_config == null) return;
            _config.TokenFileName = TokenFileName;

            // read music config from file
            _musicStore = new MusicConfigStore(MusicFileName);
            Console.WriteLine($"Object read from {MusicFileName}");

            //_localMusicHandler = new LocalMusicHandler();
            //_localMusicHandler.Play(Path.Combine($"{Environment.CurrentDirectory}", "media", "rick.ogg"));

            // init handler
            Console.WriteLine("Initializing LEGO Dimensions Portal...");
            _portalHandler = new PortalHandler();
            var listenUrl = Environment.GetEnvironmentVariable("MUSICDIMENSION_WEB_URL") ?? "http://127.0.0.1:5080";
            var app = MusicWebHost.Create(_musicStore, ReaderCapture, _config.Enable, () => _portalHandler.IsConnected, listenUrl);
            await app.StartAsync();
            Console.WriteLine($"Music manager: {listenUrl}");

            if (_config.Enable)
            {
                Console.WriteLine("Initializing Spotify...");
                _spotifyHandler = await SpotifyHandler.CreateAsync(_config);
            }
            else
            {
                Console.WriteLine("Spotify is disabled. Local file playback only.");
            }
            Console.WriteLine("Initializing VLC library...");
            _localMusicHandler = new LocalMusicHandler();

            _ = Task.Run(() =>
            {
                _portalHandler.Connect();
                if (_portalHandler.IsConnected)
                {
                    _portalHandler.Portal!.LegoTagEvent += PortalOnLegoTagEvent;
                    Console.WriteLine("Portal is connected.");
                }
                else
                {
                    Console.WriteLine("Portal not connected. You can still add tags with a manually entered UID.");
                }
            });

            try
            {
                await app.WaitForShutdownAsync();
            }
            finally
            {
                _portalHandler.Disconnect();
            }
        }

        private static bool CheckFiles()
        {
            var configFile = new FileInfo(ConfigFileName);
            if (!configFile.Exists) Console.WriteLine($"{configFile.FullName} does not exist or permission is denied.");
            var musicFile = new FileInfo(MusicFileName);
            if(!musicFile.Exists) Console.WriteLine($"{musicFile.FullName} does not exist or permission is denied.");

            return configFile.Exists && musicFile.Exists;
        }

        // ReSharper disable once AsyncVoidMethod
        private static async void PortalOnLegoTagEvent(object? sender, LegoTagEventArgs e)
        {
            try
            {
                await HandlePortalTagEventAsync(e);
            }
            catch (Exception exception)
            {
                _portalHandler.IsTransferring = false;
                Console.WriteLine($"Could not handle portal tag event: {exception.Message}");
            }
        }

        private static async Task HandlePortalTagEventAsync(LegoTagEventArgs e)
        {
            var uid = BitConverter.ToString(e.CardUid);

            if (e.Present && ReaderCapture.TagPresented(uid))
            {
                Console.WriteLine($"Captured tag UID: {uid}");
                return;
            }

            if (!e.Present)
            {
                if (!_portalHandler.IsTransferring || !string.Equals(uid, _currentTagUid, StringComparison.OrdinalIgnoreCase)) return;

                _portalHandler.IsTransferring = false;
                _pausedTagUid = _currentTagUid;
                _pausedPlaybackSource = _currentPlaybackSource;
                if (_pausedPlaybackSource == SourceTypes.Spotify)
                {
                    await _spotifyHandler.PauseAsync();
                }
                else if (_pausedPlaybackSource == SourceTypes.Local)
                {
                    _localMusicHandler.Pause();
                }
                Console.WriteLine("Playback paused!");
                return;
            }

            Console.WriteLine($"{uid} | {e.LegoTag?.GetType().Name} | {e.LegoTag?.Name}");

            if (_pausedTagUid != null)
            {
                if (string.Equals(uid, _pausedTagUid, StringComparison.OrdinalIgnoreCase))
                {
                    if (_pausedPlaybackSource == SourceTypes.Spotify)
                    {
                        await _spotifyHandler.ResumeAsync();
                    }
                    else if (_pausedPlaybackSource == SourceTypes.Local)
                    {
                        _localMusicHandler.Resume();
                    }

                    _portalHandler.IsTransferring = true;
                    _pausedTagUid = null;
                    _pausedPlaybackSource = null;
                    Console.WriteLine("Playback resumed!");
                    return;
                }

                _pausedTagUid = null;
                _pausedPlaybackSource = null;
                _currentTagUid = null;
                _currentPlaybackSource = null;
            }

            // check if tag is control tag
            if (_musicStore.Snapshot().ControlTags.TryGetValue(uid, out var value))
            {
                switch (value)
                {
                    case "next":
                    case "forward":
                        await SkipTrackAsync(forward: true);
                        return;
                    case "previous":
                    case "rewind":
                        await SkipTrackAsync(forward: false);
                        return;
                    case "play":
                        await ResumePlaybackAsync();
                        return;
                    case "stop":
                    case "pause":
                        await PausePlaybackAsync();
                        return;
                    default:
                        Console.WriteLine($"Unknown control action '{value}' for tag {uid}.");
                        return;
                }
            }

            // if tag is read => play associated song / album
            var songs = GetMusic(uid);

            // quit if no songs are configured
            if (songs.Count == 0) { Console.WriteLine($"No song configured for uid {uid}!"); return;}
            var song = songs[0];

            // TODO: get current player info - if current song or album are the same as the requested one the playback should not be reset

            switch (song.Type)
            {
                case SourceTypes.Spotify:
                    if (!_config.Enable)
                    {
                        Console.WriteLine("Spotify is disabled; this tag cannot be played.");
                        break;
                    }

                    // play first song in the list for now (or the album or playlist)
                    await _spotifyHandler.PlayAsync(song.SpotifyType.ToString()!.ToLower(), song.SpotifyId!);
                    _portalHandler.IsTransferring = true;
                    _currentTagUid = uid;
                    _currentPlaybackSource = SourceTypes.Spotify;
                    Console.WriteLine($"Now playing: {song.Name}");
                    break;
                case SourceTypes.Local:
                    var localPaths = songs
                        .Where(item => item.Type == SourceTypes.Local)
                        .Select(item => item.Path?.Replace('#', Path.DirectorySeparatorChar))
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Select(path => Path.GetFullPath(path!))
                        .ToList();
                    if (localPaths.Count == 0)
                    {
                        Console.WriteLine($"No local media paths configured for {song.Name}.");
                        break;
                    }

                    var missingPath = localPaths.FirstOrDefault(path => !File.Exists(path));
                    if (missingPath != null)
                    {
                        Console.WriteLine($"Local media file not found: {missingPath}");
                        break;
                    }

                    _localMusicHandler.Play(localPaths);
                    _portalHandler.IsTransferring = true;
                    _currentTagUid = uid;
                    _currentPlaybackSource = SourceTypes.Local;
                    Console.WriteLine($"Now playing {localPaths.Count} local file(s) for: {song.Name}");
                    break;
                case SourceTypes.Other:
                    Console.WriteLine("This source type is not handled yet.");
                    break;
                default:
                    Console.WriteLine("Given source type does not exist.");
                    break;
            }
        }

        private static async Task SkipTrackAsync(bool forward)
        {
            if (_currentPlaybackSource == SourceTypes.Spotify && _config.Enable)
            {
                if (forward) await _spotifyHandler.NextTrackAsync();
                else await _spotifyHandler.PreviousTrackAsync();
            }
            else if (_currentPlaybackSource == SourceTypes.Local)
            {
                if (forward) _localMusicHandler.NextTrack();
                else _localMusicHandler.PreviousTrack();
            }
            else
            {
                Console.WriteLine("No active playlist to control.");
            }
        }

        private static async Task PausePlaybackAsync()
        {
            if (_currentPlaybackSource == SourceTypes.Spotify && _config.Enable)
            {
                await _spotifyHandler.PauseAsync();
            }
            else if (_currentPlaybackSource == SourceTypes.Local)
            {
                _localMusicHandler.Pause();
            }
        }

        private static async Task ResumePlaybackAsync()
        {
            if (_currentPlaybackSource == SourceTypes.Spotify && _config.Enable)
            {
                await _spotifyHandler.ResumeAsync();
            }
            else if (_currentPlaybackSource == SourceTypes.Local)
            {
                _localMusicHandler.Resume();
            }
        }

        private static List<Song> GetMusic(string tagUid)
        {
            return _musicStore.GetSongs(tagUid);
        }
    }
}
