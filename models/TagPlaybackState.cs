namespace MusicDimension.models
{
    public sealed record TagPlaybackState(
        LocalPlaybackState? Local,
        SpotifyPlaybackState? Spotify);
}