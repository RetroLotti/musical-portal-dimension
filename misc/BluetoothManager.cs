namespace MusicDimension.misc
{
    using System.Text.Json;
    using System.Text.RegularExpressions;

    public static class BluetoothManager
    {
        private static readonly string RequestPath = "/var/lib/musicdimension-receiver/requests/bluetooth-request.json";
        private static readonly string StatusPath = "/var/lib/musicdimension-receiver/bluetooth-status.json";
        private static readonly JsonSerializerOptions Settings = new(JsonSerializerDefaults.Web);
        private static readonly Regex AddressPattern = new(@"\A(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\Z", RegexOptions.Compiled);

        public static bool IsManaged => OperatingSystem.IsLinux()
            && File.Exists("/usr/local/lib/musicdimension/configure-bluetooth.py")
            && Directory.Exists(Path.GetDirectoryName(RequestPath));

        public static BluetoothStatus GetStatus()
        {
            if (!IsManaged) return new(false, null, "unmanaged", null, "Bluetooth audio management is available after running the updated Raspberry Pi installer.", []);
            try
            {
                var info = new FileInfo(StatusPath);
                if (!info.Exists) return new(true, null, "idle", null, "Find and pair a Bluetooth audio device.", []);
                if (info.Length > 65536) throw new InvalidDataException("Invalid Bluetooth status file.");
                return JsonSerializer.Deserialize<BluetoothStatus>(File.ReadAllText(StatusPath), Settings)
                    ?? new(true, null, "unknown", null, "Bluetooth status is unavailable.", []);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                return new(true, null, "unknown", null, "Bluetooth status could not be read. Refresh and try again.", []);
            }
        }

        public static string StartOperation(string operation, string? address = null)
        {
            if (!IsManaged) throw new InvalidOperationException("Bluetooth audio management is only available after installing on Raspberry Pi OS.");
            if (operation is not ("scan" or "refresh" or "connect")) throw new ArgumentException("Choose a Bluetooth scan, refresh, or connect operation.");
            if (operation == "connect" && (address == null || !AddressPattern.IsMatch(address)))
                throw new ArgumentException("Choose a Bluetooth device with a valid address.");
            var current = GetStatus();
            if (current.State == "running") throw new InvalidOperationException("A Bluetooth operation is already in progress.");

            var requestId = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.Serialize(new { requestId, operation, address = address?.ToUpperInvariant() }, Settings);
            var temporary = RequestPath + ".tmp";
            try
            {
                File.WriteAllText(temporary, request);
                File.Move(temporary, RequestPath, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return requestId;
        }

        public static async Task<bool> EnsureConnectedAsync(string address, CancellationToken cancellationToken = default)
        {
            if (!IsManaged || !AddressPattern.IsMatch(address)) return false;
            string requestId;
            try
            {
                requestId = StartOperation("connect", address);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            for (var attempt = 0; attempt < 120; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                var status = GetStatus();
                if (!string.Equals(status.RequestId, requestId, StringComparison.Ordinal)) continue;
                if (status.State == "error") return false;
                if (status.State == "complete")
                    return status.Devices.Any(device => string.Equals(device.Address, address, StringComparison.OrdinalIgnoreCase) && device.Connected);
            }
            return false;
        }
    }

    public sealed record BluetoothDevice(string Address, string Name, bool Paired, bool Connected);
    public sealed record BluetoothStatus(bool Managed, string? RequestId, string State, string? Operation,
        string? Message, List<BluetoothDevice> Devices);
    public sealed record BluetoothConnectRequest(string? Address);
}