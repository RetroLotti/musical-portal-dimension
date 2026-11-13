namespace MusicDimension.models
{
    public sealed record SpotifyPlaybackState(
        string QueueSignature,
        string? ContextUri,
        string TrackUri,
        int QueueIndex,
        int PositionMs);
}
