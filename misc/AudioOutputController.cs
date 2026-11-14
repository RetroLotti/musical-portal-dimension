namespace MusicDimension.misc
{
    using System.Diagnostics;
    using System.Text.RegularExpressions;

    public static class AudioOutputController
    {
        private const string PulseServer = "unix:/run/musicdimension-audio/native";
        private static readonly Regex BluetoothSinkPattern = new(@"\Abluez_(?:output|sink)\.(?<address>[0-9A-Fa-f_]+)\.", RegexOptions.Compiled);

        public static async Task<bool> SelectAsync(string? output)
        {
            if (!OperatingSystem.IsLinux()) return true;

            var sink = "musicdimension";
            var bluetoothRequested = output?.StartsWith("bluetooth:", StringComparison.OrdinalIgnoreCase) == true;
            try
            {
                if (bluetoothRequested)
                {
                    var address = output![10..];
                    if (!Regex.IsMatch(address, @"\A(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\Z"))
                        throw new InvalidOperationException("Invalid Bluetooth output address.");
                    var cards = await RunPactlAsync("list", "short", "cards");
                    var card = cards.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => line.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                        .FirstOrDefault(name => name != null && name.StartsWith("bluez_card.", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(name[11..], address.Replace(':', '_'), StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("The paired Bluetooth audio card is not available. Reconnect it in Spotify settings.");
                    await RunPactlAsync("set-card-profile", card, "a2dp_sink");
                    var sinks = await RunPactlAsync("list", "short", "sinks");
                    sink = sinks.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => line.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                        .FirstOrDefault(name => name != null && BluetoothSinkPattern.IsMatch(name)
                            && string.Equals(BluetoothSinkPattern.Match(name).Groups["address"].Value,
                                address.Replace(':', '_'), StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("The paired Bluetooth audio sink is not available. Reconnect it in Spotify settings.");
                }

                await RunPactlAsync("set-default-sink", sink);
                await MoveSinkInputsAsync(sink);
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"Could not select audio output '{output}': {exception.Message}");
                if (bluetoothRequested)
                {
                    try
                    {
                        await RunPactlAsync("set-default-sink", "musicdimension");
                        await MoveSinkInputsAsync("musicdimension");
                        return false;
                    }
                    catch (Exception fallbackException) when (fallbackException is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
                    {
                        Console.WriteLine($"Could not restore the headphone audio output: {fallbackException.Message}");
                    }
                }
                return false;
            }
        }

        private static async Task MoveSinkInputsAsync(string sink)
        {
            var inputs = await RunPactlAsync("list", "short", "sink-inputs");
            foreach (var line in inputs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var id = line.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (id == null || !int.TryParse(id, out _)) continue;
                try
                {
                    await RunPactlAsync("move-sink-input", id, sink);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    Console.WriteLine($"Could not move PulseAudio stream {id}: {exception.Message}");
                }
            }
        }

        private static async Task<string> RunPactlAsync(params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("/usr/bin/pactl")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.Environment["PULSE_SERVER"] = PulseServer;
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) throw new IOException("Could not start pactl.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "PulseAudio rejected the output change." : error.Trim());
            return output;
        }
    }
}