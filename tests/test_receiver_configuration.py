import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("receiver", ROOT / "deploy/raspberry-pi/configure-receiver.py")
receiver = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(receiver)


class ReceiverConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        root = Path(self.directory.name)
        self.config = root / "config.json"
        self.environment = root / "receiver.env"
        self.audio = root / "pulse.pa"
        self.key = root / "soloist-key.json"
        self.key.write_text(json.dumps("fixture-private-key"))
        self.status = root / "status.json"
        self.settings = {"spotifyDeviceName": 'Music Dimension "Living Room"',
                         "receiver": {"enabled": True, "audioDevice": "plughw:CARD=Headphones,DEV=0", "bitrate": 160}}
        self.save()
        for name, value in (("CONFIG", self.config), ("KEY", self.key), ("ENVIRONMENT", self.environment), ("AUDIO", self.audio), ("STATUS", self.status), ("UPDATE", root / "update.json")):
            patcher = patch.object(receiver, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        if os.name == "nt":
            for flag in ("O_NOFOLLOW", "O_NONBLOCK"):
                patcher = patch.object(receiver.os, flag, 0, create=True)
                patcher.start()
                self.addCleanup(patcher.stop)
            patcher = patch.object(receiver.os, "fchmod", lambda *arguments: None, create=True)
            patcher.start()
            self.addCleanup(patcher.stop)
        self.commands = []
        patcher = patch.object(receiver, "set_runtime_owner")
        patcher.start()
        self.addCleanup(patcher.stop)

        def fake_systemctl(*arguments, **options):
            self.commands.append(arguments)
            return subprocess.CompletedProcess(arguments, 0, "active\n", "")

        patcher = patch.object(receiver, "systemctl", fake_systemctl)
        patcher.start()
        self.addCleanup(patcher.stop)

    def save(self):
        self.config.write_text(json.dumps(self.settings), encoding="utf-8")

    def test_apply_and_unchanged_poll_never_restarts(self):
        applied = receiver.apply_settings()
        content = self.environment.read_text(encoding="utf-8")
        self.assertEqual(json.loads(content)["name"], self.settings["spotifyDeviceName"])
        self.assertEqual(json.loads(content)["apiKey"], "fixture-private-key")
        self.assertIn("module-alsa-sink device=plughw:CARD=Headphones,DEV=0", self.audio.read_text())
        self.assertIn(("restart", "musicdimension-soloist.service"), self.commands)
        self.assertEqual(applied["name"], self.settings["spotifyDeviceName"])
        self.commands.clear()
        receiver.apply_settings()
        self.assertNotIn(("restart", "musicdimension-soloist.service"), self.commands)
        self.assertNotIn(("restart", "musicdimension-audio.service"), self.commands)
        self.assertIn(("start", "musicdimension-soloist.service"), self.commands)

    def test_name_or_audio_change_restarts_only_receiver(self):
        receiver.apply_settings()
        self.commands.clear()
        self.settings["receiver"]["audioDevice"] = "plughw:CARD=vc4hdmi,DEV=0"
        self.save()
        receiver.apply_settings()
        self.assertIn(("restart", "musicdimension-soloist.service"), self.commands)
        self.assertIn(("restart", "musicdimension-audio.service"), self.commands)
        self.assertTrue(all("musicdimension.service" not in command for command in self.commands))

    def test_disable_stops_receiver(self):
        self.settings["receiver"]["enabled"] = False
        self.save()
        receiver.apply_settings()
        self.assertIn(("disable", "--now", "musicdimension-soloist.service", "musicdimension-audio.service"), self.commands)
        self.assertFalse(any(command[0] in ("start", "restart", "enable") for command in self.commands))

    def test_rejects_injected_or_invalid_values_before_writes(self):
        invalid = [
            ("spotifyDeviceName", "Pi\nLIBRESPOT_ONEVENT=/tmp/command"),
            ("spotifyDeviceName", ""),
            ("spotifyDeviceName", "x" * 101),
            ("audioDevice", "$(touch /tmp/injected)"),
            ("audioDevice", "/tmp/audio"),
            ("audioDevice", "default\nLIBRESPOT_ONEVENT=test"),
            ("bitrate", True), ("bitrate", 321), ("enabled", "false"),
        ]
        for key, value in invalid:
            with self.subTest(key=key, value=value):
                original = json.loads(json.dumps(self.settings))
                target = self.settings if key == "spotifyDeviceName" else self.settings["receiver"]
                target[key] = value
                self.save()
                with self.assertRaises(ValueError):
                    receiver.apply_settings()
                self.settings = original
                self.assertFalse(self.environment.exists())
                self.assertEqual(self.commands, [])

    def test_status_is_written_without_credentials_or_other_config(self):
        self.settings["clientSecret"] = "private-secret"
        self.save()
        receiver.main()
        content = self.status.read_text(encoding="utf-8")
        self.assertNotIn("private-secret", content)
        self.assertNotIn("fixture-private-key", content)
        self.assertTrue(json.loads(content)["managed"])

    def test_restart_failure_reports_safe_error(self):
        with patch.object(receiver, "systemctl", side_effect=RuntimeError("private error")):
            with self.assertRaises(RuntimeError):
                receiver.main()
        content = self.status.read_text(encoding="utf-8")
        self.assertNotIn("private error", content)
        self.assertIn("could not be applied", content)

    def test_large_config_rejected(self):
        self.config.write_text(" " * 65537, encoding="utf-8")
        with self.assertRaises(ValueError):
            receiver.read_settings()

    @unittest.skipIf(os.name == "nt", "Linux O_NOFOLLOW is validated on Linux")
    def test_symlink_config_rejected(self):
        target = self.config.with_suffix(".target")
        self.config.rename(target)
        self.config.symlink_to(target)
        with self.assertRaises(OSError):
            receiver.read_settings()

    def test_missing_api_key_keeps_web_setup_possible(self):
        self.key.unlink()
        self.save()
        status = receiver.apply_settings()
        self.assertFalse(status["apiKeyConfigured"])
        self.assertIn("Save your Spotify Soloist API key", status["error"])
        self.assertFalse(any(command[0] in ("enable", "start", "restart") for command in self.commands))

    def test_startup_checks_and_private_local_api(self):
        unit = (ROOT / "deploy/raspberry-pi/musicdimension.service").read_text()
        self.assertIn("update-soloist.py --startup", unit)
        launcher = (ROOT / "deploy/raspberry-pi/run-soloist.py").read_text()
        self.assertIn('"127.0.0.1:9876"', launcher)
        self.assertIn('"/var/lib/musicdimension/soloist"', launcher)
        self.assertIn('"/var/cache/musicdimension/soloist"', launcher)

    def test_invalid_key_rejected(self):
        for key in ("private\nkey", "x" * 4097, {"unexpected": "object"}):
            self.key.write_text(json.dumps(key))
            with self.assertRaises(ValueError):
                receiver.apply_settings()


if __name__ == "__main__":
    unittest.main()