namespace MusicDimension.misc
{
    using System.Diagnostics;
    using System.Text.RegularExpressions;

    public static class AudioOutputController
    {
        private const string PulseServer = "unix:/run/musicdimension-audio/native";
        private static readonly Regex BluetoothSinkPattern = new(@"\Abluez_(?:output|sink)\.(?<address>[0-9A-Fa-f_]+)\.", RegexOptions.Compiled);

        public static async Task<bool> IsBluetoothSinkAvailableAsync(string address)
        {
            if (!OperatingSystem.IsLinux() || !Regex.IsMatch(address, @"\A(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\Z")) return false;
            try
            {
                var sinks = await RunPactlAsync("list", "short", "sinks");
                var normalizedAddress = address.Replace(':', '_');
                return sinks.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                    .Any(name => name != null && BluetoothSinkPattern.IsMatch(name)
                        && string.Equals(BluetoothSinkPattern.Match(name).Groups["address"].Value,
                            normalizedAddress, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"Could not check PulseAudio for Bluetooth sink {address}: {exception.Message}");
                return false;
            }
        }

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
                    var normalizedAddress = address.Replace(':', '_');
                    var card = await WaitForBluetoothObjectAsync("cards", name =>
                        name.StartsWith("bluez_card.", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(name[11..], normalizedAddress, StringComparison.OrdinalIgnoreCase),
                        "The Bluetooth connection is active, but PulseAudio has not created its card yet.");
                    await RunPactlAsync("set-card-profile", card, "a2dp_sink");
                    sink = await WaitForBluetoothObjectAsync("sinks", name =>
                        BluetoothSinkPattern.IsMatch(name)
                        && string.Equals(BluetoothSinkPattern.Match(name).Groups["address"].Value,
                            normalizedAddress, StringComparison.OrdinalIgnoreCase),
                        "PulseAudio did not create an A2DP output for the connected device.");
                }

                await RunPactlAsync("set-default-sink", sink);
                await MoveSinkInputsAsync(sink);
                Console.WriteLine($"Selected audio sink '{sink}' for tag output '{output ?? "headphones"}'.");
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
                        Console.WriteLine("Using the headphone output after Bluetooth sink selection failed.");
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

        private static async Task<string> WaitForBluetoothObjectAsync(string objectType, Func<string, bool> matches, string unavailableMessage)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var output = await RunPactlAsync("list", "short", objectType);
                var match = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                    .FirstOrDefault(name => name != null && matches(name));
                if (match != null) return match;
                if (attempt < 39) await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
            throw new InvalidOperationException(unavailableMessage);
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