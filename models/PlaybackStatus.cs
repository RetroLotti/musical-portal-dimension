namespace MusicDimension.models
{
    public sealed record PlaybackStatus(
        bool Active,
        string? Source,
        string? Track,
        string? Device,
        bool IsPlaying,
        long PositionMs,
        long DurationMs,
        int? Volume,
        string? AudioOutput);
}