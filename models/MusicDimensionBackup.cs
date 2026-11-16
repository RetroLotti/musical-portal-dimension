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
        bool? BluetoothEnabled,
        string? AudioDevice,
        string? DefaultAudioOutput,
        int Bitrate,
        int MediaMaxFileSizeMb,
        int? InitialVolume);
}
