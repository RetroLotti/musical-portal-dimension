namespace MusicDimension.models
{
    public sealed record LocalPlaybackState(
        string QueueSignature,
        int TrackIndex,
        long PositionMs,
        int[] PlayOrder,
        bool Shuffle);
}