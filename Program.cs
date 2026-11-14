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
        private static TagPlaybackStore _tagPlaybackStore = null!;

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

            if (args.Contains("--spotify-login"))
            {
                await SpotifyHandler.CreateAsync(_config);
                return;
            }

            // read music config from file
            _musicStore = new MusicConfigStore(MusicFileName);
            _tagPlaybackStore = new TagPlaybackStore(Path.Combine(Environment.CurrentDirectory, "config", "tag-playback-state.json"));
            Console.WriteLine($"Object read from {MusicFileName}");

            //_localMusicHandler = new LocalMusicHandler();
            //_localMusicHandler.Play(Path.Combine($"{Environment.CurrentDirectory}", "media", "rick.ogg"));

            // init handler
            Console.WriteLine("Initializing LEGO Dimensions Portal...");
            _portalHandler = new PortalHandler();
            _spotifyHandler = new SpotifyHandler(_config);
            var listenUrl = Environment.GetEnvironmentVariable("MUSICDIMENSION_WEB_URL") ?? "http://127.0.0.1:5080";
                var app = MusicWebHost.Create(_musicStore, ReaderCapture, _spotifyHandler, ConfigFileName,
                    () => _portalHandler.IsConnected, listenUrl, ExecuteWebPlaybackControlAsync, GetPlaybackStatusAsync);
            await app.StartAsync();
            Console.WriteLine($"Music manager: {listenUrl}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await _spotifyHandler.InitializeAsync();
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"Spotify initialization failed; playback will retry: {exception.Message}");
                }
            });
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
                await SaveCurrentTagPlaybackStateAsync();
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
                await SaveCurrentTagPlaybackStateAsync();
                if (_pausedPlaybackSource == SourceTypes.Spotify && _spotifyHandler.Configuration.Enable)
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

            var isControlTag = _musicStore.Snapshot().ControlTags.TryGetValue(uid, out var controlAction);
            if (_pausedTagUid != null && !isControlTag)
            {
                if (string.Equals(uid, _pausedTagUid, StringComparison.OrdinalIgnoreCase))
                {
                    var pausedTag = _musicStore.Snapshot().RegisteredTags
                        .FirstOrDefault(item => string.Equals(item.Uid, uid, StringComparison.OrdinalIgnoreCase));
                    if (pausedTag != null) await SelectTagOutputAsync(pausedTag);
                        if (_pausedPlaybackSource is SourceTypes playbackSource)
                            await ApplyInitialVolumeAsync(playbackSource);
                    if (_pausedPlaybackSource == SourceTypes.Spotify && !_spotifyHandler.Configuration.Enable)
                    {
                        Console.WriteLine("Spotify is disabled; this tag cannot be resumed.");
                        return;
                    }
                    if (_pausedPlaybackSource == SourceTypes.Spotify)
                    {
                        var savedState = pausedTag?.ResumePlayback == true ? _tagPlaybackStore.GetSpotify(uid) : null;
                        if (pausedTag?.ResumePlayback == true && savedState != null)
                        {
                            if (await _spotifyHandler.TryResumeAsync(pausedTag.Songs, savedState))
                            {
                                Console.WriteLine("Restored saved Spotify position.");
                            }
                            else
                            {
                                RemoveSavedTagPlaybackState(uid);
                                await _spotifyHandler.PlayAsync(pausedTag.Songs);
                            }
                        }
                        else
                        {
                            await _spotifyHandler.ResumeAsync();
                        }
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
            if (isControlTag)
            {
                switch (MusicConfigStore.NormalizeControlAction(controlAction!))
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
                    case "volumeup":
                        await ChangeVolumeAsync(10);
                        return;
                    case "volumedown":
                        await ChangeVolumeAsync(-10);
                        return;
                    case "shuffle":
                        await ToggleShuffleAsync();
                        return;
                    default:
                        Console.WriteLine($"Unknown control action '{controlAction}' for tag {uid}.");
                        return;
                }
            }

            // if tag is read => play associated song / album
            var tag = _musicStore.Snapshot().RegisteredTags
                .FirstOrDefault(item => string.Equals(item.Uid, uid, StringComparison.OrdinalIgnoreCase));
            var songs = tag?.Songs ?? [];

            // quit if no songs are configured
            if (songs.Count == 0) { Console.WriteLine($"No song configured for uid {uid}!"); return;}
            var song = songs[0];

            await SaveCurrentTagPlaybackStateAsync();
            if (tag != null) await SelectTagOutputAsync(tag);

            // TODO: get current player info - if current song or album are the same as the requested one the playback should not be reset

            switch (song.Type)
            {
                case SourceTypes.Spotify:
                    if (!_spotifyHandler.Configuration.Enable)
                    {
                        Console.WriteLine("Spotify is disabled; this tag cannot be played.");
                        break;
                    }

                    if (tag!.ResumePlayback)
                    {
                        var savedState = _tagPlaybackStore.GetSpotify(uid);
                        if (savedState == null || !await _spotifyHandler.TryResumeAsync(songs, savedState))
                        {
                            RemoveSavedTagPlaybackState(uid);
                            await _spotifyHandler.PlayAsync(songs);
                        }
                    }
                    else
                    {
                        RemoveSavedTagPlaybackState(uid);
                        await _spotifyHandler.PlayAsync(songs);
                    }
                        await ApplyInitialVolumeAsync(SourceTypes.Spotify);
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

                    if (tag!.ResumePlayback)
                    {
                        var savedState = _tagPlaybackStore.GetLocal(uid);
                        if (savedState != null && !_localMusicHandler.Play(localPaths, savedState))
                            RemoveSavedTagPlaybackState(uid);
                        else if (savedState == null)
                            _localMusicHandler.Play(localPaths);
                    }
                    else
                    {
                        RemoveSavedTagPlaybackState(uid);
                        _localMusicHandler.Play(localPaths);
                    }
                        await ApplyInitialVolumeAsync(SourceTypes.Local);
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
            if (_currentPlaybackSource == SourceTypes.Spotify && _spotifyHandler.Configuration.Enable)
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

        private static async Task ChangeVolumeAsync(int delta)
        {
            if (_currentPlaybackSource == SourceTypes.Spotify && _spotifyHandler.Configuration.Enable)
                await _spotifyHandler.ChangeVolumeAsync(delta);
            else if (_currentPlaybackSource == SourceTypes.Local)
                _localMusicHandler.ChangeVolume(delta);
            else Console.WriteLine("No playback volume to control.");
        }

        private static async Task ToggleShuffleAsync()
        {
            if (_currentPlaybackSource == SourceTypes.Spotify && _spotifyHandler.Configuration.Enable)
                await _spotifyHandler.ToggleShuffleAsync();
            else if (_currentPlaybackSource == SourceTypes.Local)
                _localMusicHandler.ToggleShuffle();
            else Console.WriteLine("No playlist to shuffle.");
        }

        private static async Task PausePlaybackAsync()
        {
            if (_currentPlaybackSource == SourceTypes.Spotify && _spotifyHandler.Configuration.Enable)
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
            if (_currentPlaybackSource == SourceTypes.Spotify && _spotifyHandler.Configuration.Enable)
            {
                await _spotifyHandler.ResumeAsync();
            }
            else if (_currentPlaybackSource == SourceTypes.Local)
            {
                _localMusicHandler.Resume();
            }
        }

        private static async Task ExecuteWebPlaybackControlAsync(string action)
        {
            if (_currentPlaybackSource == null)
                throw new InvalidOperationException("Read a music tag before using playback controls.");
            if (_currentPlaybackSource == SourceTypes.Spotify && !_spotifyHandler.Configuration.Enable)
                throw new InvalidOperationException("Spotify is disabled.");

            switch (action)
            {
                case "next": await SkipTrackAsync(forward: true); break;
                case "previous": await SkipTrackAsync(forward: false); break;
                case "volumeup": await ChangeVolumeAsync(10); break;
                case "volumedown": await ChangeVolumeAsync(-10); break;
                case "shuffle": await ToggleShuffleAsync(); break;
                default: throw new ArgumentException("Choose a supported playback action.", nameof(action));
            }
        }

        private static Task<PlaybackStatus> GetPlaybackStatusAsync()
        {
            var audioOutput = _currentTagUid == null ? null : _musicStore.Snapshot().RegisteredTags
                .FirstOrDefault(tag => string.Equals(tag.Uid, _currentTagUid, StringComparison.OrdinalIgnoreCase))?.AudioOutput;
            return _currentPlaybackSource switch
            {
                SourceTypes.Local => Task.FromResult(_localMusicHandler.GetPlaybackStatus(audioOutput)),
                SourceTypes.Spotify when _spotifyHandler.Configuration.Enable => _spotifyHandler.GetPlaybackStatusAsync(audioOutput),
                _ => Task.FromResult(new PlaybackStatus(false, null, null, null, false, 0, 0, null, audioOutput))
            };
        }

        private static async Task ApplyInitialVolumeAsync(SourceTypes source)
        {
            if (_config.InitialVolume is not int volume) return;
            try
            {
                if (source == SourceTypes.Local)
                    _localMusicHandler.SetVolume(volume);
                else if (source == SourceTypes.Spotify && _spotifyHandler.Configuration.Enable)
                    await _spotifyHandler.SetVolumeAsync(volume);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Could not apply initial volume {volume}: {exception.Message}");
            }
        }

        private static async Task SaveCurrentTagPlaybackStateAsync()
        {
            if (string.IsNullOrWhiteSpace(_currentTagUid)) return;
            var tag = _musicStore.Snapshot().RegisteredTags
                .FirstOrDefault(item => string.Equals(item.Uid, _currentTagUid, StringComparison.OrdinalIgnoreCase));
            if (tag?.ResumePlayback != true) return;

            try
            {
                if (_currentPlaybackSource == SourceTypes.Local)
                {
                    var state = _localMusicHandler.CapturePlaybackState();
                    if (state != null) _tagPlaybackStore.SetLocal(_currentTagUid, state);
                }
                else if (_currentPlaybackSource == SourceTypes.Spotify && _spotifyHandler.Configuration.Enable)
                {
                    var state = await _spotifyHandler.CapturePlaybackStateAsync(tag.Songs);
                    if (state != null) _tagPlaybackStore.SetSpotify(_currentTagUid, state);
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Could not save playback position for tag {_currentTagUid}: {exception.Message}");
            }
        }

        private static async Task SelectTagOutputAsync(Tag tag)
        {
            if (tag.AudioOutput.StartsWith("bluetooth:", StringComparison.OrdinalIgnoreCase)
                && !await BluetoothManager.EnsureConnectedAsync(tag.AudioOutput[10..]))
            {
                Console.WriteLine($"Bluetooth output for tag {tag.Uid} is unavailable; using the headphone output.");
                await AudioOutputController.SelectAsync("headphones");
                return;
            }
            if (!await AudioOutputController.SelectAsync(tag.AudioOutput))
                Console.WriteLine($"Bluetooth output for tag {tag.Uid} is unavailable; using the headphone output.");
        }

        private static void RemoveSavedTagPlaybackState(string uid)
        {
            try
            {
                _tagPlaybackStore.Remove(uid);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Could not clear playback position for tag {uid}: {exception.Message}");
            }
        }
    }
}
