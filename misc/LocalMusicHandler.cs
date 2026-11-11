namespace MusicDimension.misc
{
    using LibVLCSharp.Shared;

    public class LocalMusicHandler
    {
        private LibVLC Library { get; }
        private MediaPlayer Player { get; }
        private readonly object _playlistLock = new();
        private List<string> _tracks = [];
        private int _currentTrackIndex = -1;
        private Media? _currentMedia;

        public LocalMusicHandler()
        {
            Core.Initialize();

            Library = new LibVLC();
            Player = new MediaPlayer(Library);
            Player.EndReached += (_, _) => _ = Task.Run(PlayNextTrack);
        }

        public void Play(string track)
        {
            Play([track]);
        }

        public void Play(IEnumerable<string> tracksToPlay)
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
                _tracks = tracks;
                _currentTrackIndex = 0;
                PlayCurrentTrack();
            }
        }

        public void Stop()
        {
            lock (_playlistLock)
            {
                Player.Stop();
                _tracks = [];
                _currentTrackIndex = -1;
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
            _currentMedia = new Media(Library, _tracks[_currentTrackIndex], FromType.FromPath);
            Player.Play(_currentMedia);
        }

        private void PlayNextTrack()
        {
            lock (_playlistLock)
            {
                if (_currentTrackIndex < 0 || _currentTrackIndex + 1 >= _tracks.Count) return;

                _currentTrackIndex++;
                PlayCurrentTrack();
            }
        }
    }
}
