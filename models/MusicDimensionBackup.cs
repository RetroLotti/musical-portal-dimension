namespace MusicDimension.models
{
    public sealed record MusicDimensionBackup(
        int Version,
        DateTimeOffset CreatedAt,
        BackupSettings? Settings,
        MusicConfig? Library);

    public sealed record BackupSettings(
        bool Enable,
        string? ClientId,
        string? SpotifyDeviceName,
        bool ReceiverEnabled,
        string? AudioDevice,
        int Bitrate,
        int MediaMaxFileSizeMb,
        int? InitialVolume);
}
