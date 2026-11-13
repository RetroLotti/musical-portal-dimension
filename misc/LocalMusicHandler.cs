namespace MusicDimension.misc
{
    using LibVLCSharp.Shared;
    using MusicDimension.models;
    using System.Security.Cryptography;
    using System.Text;

    public class LocalMusicHandler
    {
        private LibVLC Library { get; }
        private MediaPlayer Player { get; }
        private readonly object _playlistLock = new();
        private List<string> _tracks = [];
        private List<int> _playOrder = [];
        private bool _shuffle;
        private int _currentTrackIndex = -1;
        private Media? _currentMedia;
        private long? _pendingSeekMs;

        public LocalMusicHandler()
        {
            Core.Initialize();

            var audioOutput = Environment.GetEnvironmentVariable("MUSICDIMENSION_AUDIO_OUTPUT");
            var alsaDevice = Environment.GetEnvironmentVariable("MUSICDIMENSION_ALSA_DEVICE");
            var options = new List<string>();
            if (!string.IsNullOrWhiteSpace(audioOutput)) options.Add($"--aout={audioOutput}");
            if (!string.IsNullOrWhiteSpace(alsaDevice)) options.Add($"--alsa-audio-device={alsaDevice}");
            Library = options.Count == 0 ? new LibVLC() : new LibVLC(options.ToArray());
            Player = new MediaPlayer(Library);
            Player.EndReached += (_, _) => _ = Task.Run(PlayNextTrack);
            Player.Playing += (_, _) => ApplyPendingSeek();
        }

        public void Play(string track)
        {
            Play([track]);
        }

        public void Play(IEnumerable<string> tracksToPlay)
        {
            Play(tracksToPlay, null);
        }

        public bool Play(IEnumerable<string> tracksToPlay, LocalPlaybackState? resumeState)
        {
            var tracks = tracksToPlay.SelectMany(track => Path.GetExtension(track).ToLowerInvariant() switch
            {
                ".m3u" or ".m3u8" => ReadPlaylist(track),
                _ => [Path.GetFullPath(track)]
            }).ToList();
            if (tracks.Count == 0) throw new InvalidDataException("The local playlist contains no playable tracks.");

            lock (_playlistLock)
            {
                Player.Stop();
                _pendingSeekMs = null;
                _tracks = tracks;
                var order = Enumerable.Range(0, tracks.Count).ToArray();
                var restored = resumeState != null
                    && string.Equals(resumeState.QueueSignature, QueueSignature(tracks), StringComparison.Ordinal)
                    && IsValidPlayOrder(resumeState.PlayOrder, tracks.Count)
                    && resumeState.TrackIndex >= 0
                    && resumeState.TrackIndex < tracks.Count
                    && resumeState.PlayOrder.Contains(resumeState.TrackIndex);
                if (restored)
                {
                    _playOrder = resumeState!.PlayOrder.ToList();
                    _currentTrackIndex = _playOrder.IndexOf(resumeState.TrackIndex);
                    _shuffle = resumeState.Shuffle;
                    _pendingSeekMs = Math.Max(0, resumeState.PositionMs);
                }
                else
                {
                    if (_shuffle) Random.Shared.Shuffle(order);
                    _playOrder = order.ToList();
                    _currentTrackIndex = 0;
                }
                PlayCurrentTrack();
                return restored;
            }
        }

        public LocalPlaybackState? CapturePlaybackState()
        {
            lock (_playlistLock)
            {
                if (_currentTrackIndex < 0 || _currentTrackIndex >= _playOrder.Count) return null;

                return new LocalPlaybackState(
                    QueueSignature(_tracks),
                    _playOrder[_currentTrackIndex],
                    Math.Max(0, Player.Time),
                    _playOrder.ToArray(),
                    _shuffle);
            }
        }

        public void Stop()
        {
            lock (_playlistLock)
            {
                Player.Stop();
                _tracks = [];
                _playOrder = [];
                _currentTrackIndex = -1;
                _pendingSeekMs = null;
                _currentMedia?.Dispose();
                _currentMedia = null;
            }
        }

        public void Pause()
        {
            Player.Pause();
        }

        public void Resume()
        {
            Player.Play();
        }

        public void NextTrack()
        {
            lock (_playlistLock)
            {
                if (_currentTrackIndex < 0 || _currentTrackIndex + 1 >= _tracks.Count) return;

                _currentTrackIndex++;
                PlayCurrentTrack();
            }
        }

        public void PreviousTrack()
        {
            lock (_playlistLock)
            {
                if (_currentTrackIndex <= 0) return;

                _currentTrackIndex--;
                PlayCurrentTrack();
            }
        }

        public void ChangeVolume(int delta)
        {
            lock (_playlistLock)
            {
                var volume = Player.Volume;
                if (volume >= 0) Player.Volume = (int)Math.Clamp((long)volume + delta, 0, 100);
            }
        }

        public void ToggleShuffle()
        {
            lock (_playlistLock)
            {
                _shuffle = !_shuffle;
                var remaining = _playOrder.Skip(_currentTrackIndex + 1).ToArray();
                if (_shuffle) Random.Shared.Shuffle(remaining);
                else Array.Sort(remaining);
                _playOrder.RemoveRange(_currentTrackIndex + 1, remaining.Length);
                _playOrder.AddRange(remaining);
            }
        }

        private List<string> ReadPlaylist(string playlistPath)
        {
            var playlistDirectory = Path.GetDirectoryName(Path.GetFullPath(playlistPath))!;
            var tracks = new List<string>();

            foreach (var line in File.ReadLines(playlistPath))
            {
                var entry = line.Trim();
                if (entry.Length == 0 || entry.StartsWith('#')) continue;

                string trackPath;
                if (Uri.TryCreate(entry, UriKind.Absolute, out var uri) && uri.IsFile)
                {
                    trackPath = uri.LocalPath;
                }
                else
                {
                    trackPath = Path.IsPathRooted(entry) ? entry : Path.Combine(playlistDirectory, entry);
                }

                trackPath = Path.GetFullPath(trackPath);
                var mediaDirectory = Path.GetFullPath(playlistDirectory) + Path.DirectorySeparatorChar;
                var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!trackPath.StartsWith(mediaDirectory, pathComparison))
                {
                    throw new InvalidDataException($"Playlist tracks must be stored beside the playlist: {entry}");
                }

                if (!File.Exists(trackPath))
                {
                    var fileName = Path.GetFileName(entry.Replace('\\', '/'));
                    var matches = Directory.EnumerateFiles(playlistDirectory, fileName, SearchOption.AllDirectories)
                        .Select(Path.GetFullPath)
                        .Where(path => path.StartsWith(mediaDirectory, pathComparison))
                        .ToList();

                    if (matches.Count == 1)
                    {
                        trackPath = matches[0];
                    }
                    else if (matches.Count > 1)
                    {
                        throw new InvalidDataException($"Playlist track name is ambiguous: {entry}");
                    }
                    else
                    {
                        throw new FileNotFoundException($"Playlist track not found: {entry}", trackPath);
                    }
                }

                tracks.Add(trackPath);
            }

            if (tracks.Count == 0)
            {
                throw new InvalidDataException("The playlist contains no playable local tracks.");
            }

            return tracks;
        }

        private void PlayCurrentTrack()
        {
            _currentMedia?.Dispose();
            _currentMedia = new Media(Library, _tracks[_playOrder[_currentTrackIndex]], FromType.FromPath);
            Player.Play(_currentMedia);
        }

        private void ApplyPendingSeek()
        {
            lock (_playlistLock)
            {
                if (_pendingSeekMs is not long position) return;
                if (Player.Length > 0) position = Math.Min(position, Math.Max(0, Player.Length - 1000));
                Player.Time = position;
                _pendingSeekMs = null;
            }
        }

        private static bool IsValidPlayOrder(IReadOnlyCollection<int> playOrder, int trackCount) =>
            playOrder.Count == trackCount
            && playOrder.Distinct().Count() == trackCount
            && playOrder.All(index => index >= 0 && index < trackCount);

        private static string QueueSignature(IEnumerable<string> tracks) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", tracks))));

        private void PlayNextTrack()
        {
            NextTrack();
        }
    }
}
