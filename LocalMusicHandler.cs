namespace MusicDimension
{
    using LibVLCSharp.Shared;

    public class LocalMusicHandler
    {
        private LibVLC Library { get; }
        private MediaPlayer Player { get; }

        public LocalMusicHandler()
        {
            Core.Initialize();

            Library = new LibVLC();
            Player = new MediaPlayer(Library);
        }

        public void Play(string track)
        {
            using var media = new Media(Library, track);
            Player.Play(media);
        }

        public void Stop()
        {
            Player.Stop();
        }

        public void Pause()
        {
            Player.Pause();
        }
    }
}
