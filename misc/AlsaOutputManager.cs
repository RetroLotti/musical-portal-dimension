namespace MusicDimension.misc
{
    using System.Diagnostics;
    using System.Text.RegularExpressions;

    public static class AlsaOutputManager
    {
        private static readonly Regex DevicePattern = new(
            @"\A(?:plughw|hw|sysdefault|front|surround21|surround40|surround41|surround50|surround51|surround71):[A-Za-z0-9_:,.=\-]{1,128}\Z",
            RegexOptions.Compiled);
        private static readonly Regex PlaybackDeviceLine = new(
            @"\A\s*card\s+(?<card>[0-9]+):\s*[^\[]+\[(?<cardName>[^\]]+)\],\s*device\s+(?<device>[0-9]+):\s*[^\[]+\[(?<deviceName>[^\]]+)\]\s*\Z",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsValidDevice(string? device) => device != null && DevicePattern.IsMatch(device);

        public static async Task<IReadOnlyList<AlsaOutputDevice>> GetOutputsAsync()
        {
            if (!OperatingSystem.IsLinux()) return [];
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo("/usr/bin/aplay", "-l")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                if (!process.Start()) return [];
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var output = await outputTask;
                _ = await errorTask;
                if (process.ExitCode != 0) return [];

                var results = new List<AlsaOutputDevice>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in output.Split('\n'))
                {
                    if (results.Count >= 50) break;
                    var match = PlaybackDeviceLine.Match(line);
                    if (!match.Success) continue;
                    var card = int.Parse(match.Groups["card"].Value);
                    var device = int.Parse(match.Groups["device"].Value);
                    var id = $"plughw:{card},{device}";
                    if (!seen.Add(id)) continue;
                    var cardName = match.Groups["cardName"].Value.Trim();
                    var deviceName = match.Groups["deviceName"].Value.Trim();
                    results.Add(new AlsaOutputDevice(id, $"{cardName} · {deviceName} (card {card}, device {device})"));
                }
                return results;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"Could not enumerate ALSA output devices: {exception.Message}");
                return [];
            }
        }
    }

    public sealed record AlsaOutputDevice(string Id, string Name);
}