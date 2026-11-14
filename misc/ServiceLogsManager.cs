namespace MusicDimension.misc
{
    using System.Text.Json;

    public static class ServiceLogsManager
    {
        private static readonly string RequestPath = "/var/lib/musicdimension-receiver/requests/log-request.json";
        private static readonly string StatusPath = "/var/lib/musicdimension-receiver/log-status.json";
        private static readonly HashSet<string> Services = new(StringComparer.Ordinal)
        {
            "app", "audio", "soloist", "receiver", "bluetooth", "bluetooth-daemon"
        };
        private static readonly JsonSerializerOptions Settings = new(JsonSerializerDefaults.Web);
        private const long MaximumStatusSize = 192 * 1024;

        public static bool IsManaged => OperatingSystem.IsLinux()
            && File.Exists("/usr/local/lib/musicdimension/read-service-logs.py")
            && Directory.Exists(Path.GetDirectoryName(RequestPath));

        public static ServiceLogsStatus GetStatus()
        {
            if (!IsManaged)
                return new(false, null, null, "unmanaged", null, "Service logs are available after running the updated Raspberry Pi installer.", "");
            try
            {
                var info = new FileInfo(StatusPath);
                if (!info.Exists) return new(true, null, null, "idle", null, "Select a service and load its recent logs.", "");
                if (info.Length > MaximumStatusSize) throw new InvalidDataException("Invalid service log status file.");
                return JsonSerializer.Deserialize<ServiceLogsStatus>(File.ReadAllText(StatusPath), Settings)
                    ?? new(true, null, null, "unknown", null, "Service log status is unavailable.", "");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                return new(true, null, null, "unknown", null, "Service log status could not be read. Try refreshing.", "");
            }
        }

        public static string StartRequest(string service, int lines)
        {
            if (!IsManaged) throw new InvalidOperationException("Service logs are only available after installing on Raspberry Pi OS.");
            if (!Services.Contains(service)) throw new ArgumentException("Choose a supported service.");
            if (lines is not (50 or 100 or 200)) throw new ArgumentException("Choose 50, 100, or 200 lines.");
            if (GetStatus().State == "running") throw new InvalidOperationException("A service log request is already in progress.");

            var requestId = Guid.NewGuid().ToString("N");
            var content = JsonSerializer.Serialize(new { requestId, service, lines }, Settings);
            var temporaryPath = RequestPath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, content);
                File.Move(temporaryPath, RequestPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return requestId;
        }
    }

    public sealed record ServiceLogsRequest(int Lines);
    public sealed record ServiceLogsStatus(bool Managed, string? RequestId, string? Service, string State,
        int? Lines, string? Message, string Logs);
}