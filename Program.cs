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
        private static MusicConfig _musicConfig = null!;
        private static string? _currentTagUid;
        private static SourceTypes? _currentPlaybackSource;
        private static string? _pausedTagUid;
        private static SourceTypes? _pausedPlaybackSource;

        private static async Task Main(string[] _)
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
            _musicConfig = JsonFileHandler.ReadObjectFromJsonFile<MusicConfig>(MusicFileName) ?? new MusicConfig();
            Console.WriteLine($"Object read from {MusicFileName}");

            //_localMusicHandler = new LocalMusicHandler();
            //_localMusicHandler.Play(Path.Combine($"{Environment.CurrentDirectory}", "media", "rick.ogg"));

            // init handler
            Console.WriteLine("Initializing LEGO Dimensions Portal...");
            _portalHandler = new PortalHandler();
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

            // try to connect/identify the portal
            _portalHandler.Connect();

            // check if connection was not successful
            if (!_portalHandler.IsConnected)
            {
                Console.WriteLine("Press any key to exit");
                Console.ReadKey();
                return;
            }

            // add event to react on placing an nfc tag on any pad
            _portalHandler.Portal!.LegoTagEvent += PortalOnLegoTagEvent;

            Console.WriteLine("Portal is now active!");
            Console.WriteLine("Press any key to deactivate.");

            if (Environment.UserInteractive)
            {
                // loop for terminal sessions
                while (!Console.KeyAvailable) { Thread.Sleep(1000); }
            }
            else
            {
                // loop for running as a daemon 
                while (true) { Thread.Sleep(1000); }
            }

            // set to disconnected
            _portalHandler.Disconnect();
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
            var uid = BitConverter.ToString(e.CardUid);

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
            if (_config.Enable && _musicConfig.ControlTags.TryGetValue(uid, out var value))
            {
                switch (value)
                {
                    
                    case "next":
                        await _spotifyHandler.NextTrackAsync();
                        break;
                    case "previous":
                        await _spotifyHandler.PreviousTrackAsync();
                        break;
                    default:
                        await _spotifyHandler.PauseAsync();
                        break;
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
                    var localPath = song.Path?.Replace('#', Path.DirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(localPath))
                    {
                        Console.WriteLine($"No local media path configured for {song.Name}.");
                        break;
                    }

                    var resolvedLocalPath = Path.GetFullPath(localPath);
                    if (!File.Exists(resolvedLocalPath))
                    {
                        Console.WriteLine($"Local media file not found: {resolvedLocalPath}");
                        break;
                    }

                    _localMusicHandler.Play(resolvedLocalPath);
                    _portalHandler.IsTransferring = true;
                    _currentTagUid = uid;
                    _currentPlaybackSource = SourceTypes.Local;
                    Console.WriteLine($"Now playing: {song.Name}");
                    break;
                case SourceTypes.Other:
                    Console.WriteLine("This source type is not handled yet.");
                    break;
                default:
                    Console.WriteLine("Given source type does not exist.");
                    break;
            }
        }

        private static List<Song> GetMusic(string tagUid)
        {
            foreach (var tag in _musicConfig.RegisteredTags.Where(tag => tag.Uid.Equals(tagUid)))
            {
                return tag.Songs;
            }

            return [];
        }
    }
}
