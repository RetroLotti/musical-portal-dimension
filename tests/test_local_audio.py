import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
HARNESS = r'''
using System.Reflection;
using MusicDimension.misc;

var parse = typeof(AudioOutputController).GetMethod("GetObjectName", BindingFlags.NonPublic | BindingFlags.Static)!;
var card = "1\tbluez_card.F4_B6_88_5B_8C_4F\tmodule-bluez5-device.c";
var sink = "2\tbluez_sink.F4_B6_88_5B_8C_4F.a2dp_sink\tmodule-bluez5-device.c\tSUSPENDED";
if ((string?)parse.Invoke(null, [card]) != "bluez_card.F4_B6_88_5B_8C_4F"
    || (string?)parse.Invoke(null, [sink]) != "bluez_sink.F4_B6_88_5B_8C_4F.a2dp_sink"
    || parse.Invoke(null, ["invalid"]) != null)
    throw new Exception("PulseAudio short-list parsing regression");

var findSink = typeof(AudioOutputController).GetMethod("FindAlsaSink", BindingFlags.NonPublic | BindingFlags.Static)!;
var sinksJson = """
    [{"name":"musicdimension","properties":{"device.string":"plughw:CARD=BT600,DEV=0"}},
     {"name":"other","properties":{"device.string":"plughw:0,0"}}]
    """;
if ((string?)findSink.Invoke(null, [sinksJson, "plughw:CARD=BT600,DEV=0"]) != "musicdimension"
    || findSink.Invoke(null, [sinksJson, "plughw:9,0"]) != null)
    throw new Exception("Existing ALSA sink lookup regression");
if (!AlsaOutputManager.IsValidDevice("plughw:CARD=BT600,DEV=0")
    || !AlsaOutputManager.IsValidDevice("plughw:2,0")
    || AlsaOutputManager.IsValidDevice("plughw:2,0 sink_name=injected"))
    throw new Exception("ALSA device validation regression");

Environment.SetEnvironmentVariable("MUSICDIMENSION_AUDIO_OUTPUT", "dummy");
Environment.SetEnvironmentVariable("MUSICDIMENSION_ALSA_DEVICE", null);
var path = Path.Combine(Environment.CurrentDirectory, "fixture.wav");
using (var writer = new BinaryWriter(File.Create(path)))
{
    var samples = 44100 * 10;
    writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
    writer.Write(36 + samples * 2);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(44100);
    writer.Write(88200);
    writer.Write((short)2);
    writer.Write((short)16);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
    writer.Write(samples * 2);
    for (var index = 0; index < samples; index++) writer.Write((short)0);
}

var player = new LocalMusicHandler();
await Task.Run(async () =>
{
    player.Play(new[] { path, path });
    await WaitForPositionAsync(100);
    var saved = player.CapturePlaybackState()! with { PositionMs = 1500 };
    player.Stop();
    if (!player.Play(new[] { path, path }, saved)) throw new Exception("Resume state was rejected");
    await WaitForPositionAsync(1400);
    player.Stop();
    for (var iteration = 0; iteration < 15; iteration++)
    {
        player.Play(path);
        await Task.Delay(25);
        _ = player.GetPlaybackStatus("headphones");
        player.Stop();
    }
}).WaitAsync(TimeSpan.FromSeconds(30));
Console.WriteLine("PASS: pactl column parsing, local playback, seeking, status polling, repeated stop/start");

async Task WaitForPositionAsync(long position)
{
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < deadline)
    {
        var status = player.GetPlaybackStatus("headphones");
        if (status.IsPlaying && status.PositionMs >= position) return;
        await Task.Delay(50);
    }
    throw new Exception("VLC did not start or seek within the deadline");
}
'''


class LocalAudioTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt" and shutil.which("dotnet"), "Requires .NET SDK and bundled Windows VLC")
    def test_native_playback_and_pactl_parsing(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
            properties = ET.SubElement(project, "PropertyGroup")
            for name, value in (("OutputType", "Exe"), ("TargetFramework", "net10.0"),
                                ("ImplicitUsings", "enable"), ("Nullable", "enable"), ("UseAppHost", "false")):
                ET.SubElement(properties, name).text = value
            items = ET.SubElement(project, "ItemGroup")
            ET.SubElement(items, "ProjectReference", Include=str(ROOT / "MusicDimension.csproj"))
            ET.ElementTree(project).write(directory / "AudioCheck.csproj", encoding="utf-8", xml_declaration=True)
            (directory / "Program.cs").write_text(HARNESS, encoding="utf-8")
            environment = dict(os.environ, DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE="true")
            result = subprocess.run(
                ["dotnet", "run", "--project", str(directory / "AudioCheck.csproj"), "--verbosity", "quiet"],
                cwd=directory, env=environment, capture_output=True, text=True, timeout=120,
            )
            self.assertEqual(result.returncode, 0, (result.stdout + result.stderr)[-6000:])
            self.assertIn("PASS:", result.stdout)


if __name__ == "__main__":
    unittest.main()