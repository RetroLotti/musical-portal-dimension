namespace MusicDimension.misc
{
    using System.Diagnostics;
    using System.Text.RegularExpressions;

    public static class AlsaOutputManager
    {
        private static readonly Regex DevicePattern = new(
            @"\A(?:plughw|hw|sysdefault|front|surround21|surround40|surround41|surround50|surround51|surround71):[A-Za-z0-9_:,.=\-]{1,128}\Z",
            RegexOptions.Compiled);

        public static bool IsValidDevice(string? device) => device != null && DevicePattern.IsMatch(device);

        public static async Task<IReadOnlyList<AlsaOutputDevice>> GetOutputsAsync()
        {
            if (!OperatingSystem.IsLinux()) return [];
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo("/usr/bin/aplay", "-L")
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
                var lines = output.Split('\n');
                for (var index = 0; index < lines.Length && results.Count < 50; index++)
                {
                    var name = lines[index].Trim();
                    if (name.Length == 0 || !IsValidDevice(name) || results.Any(device => device.Id == name)) continue;
                    var description = index + 1 < lines.Length && lines[index + 1].Length > 0 && char.IsWhiteSpace(lines[index + 1][0])
                        ? lines[index + 1].Trim()
                        : name;
                    if (description.Length > 90) description = description[..90];
                    results.Add(new AlsaOutputDevice(name, description));
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