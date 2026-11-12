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
        self.status = root / "status.json"
        self.settings = {"spotifyDeviceName": 'Music Dimension "Living Room"',
                         "receiver": {"enabled": True, "audioDevice": "plughw:CARD=Headphones,DEV=0", "bitrate": 160}}
        self.save()
        for name, value in (("CONFIG", self.config), ("ENVIRONMENT", self.environment), ("STATUS", self.status)):
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
        self.assertIn('LIBRESPOT_NAME="Music Dimension \\"Living Room\\""', content)
        self.assertIn("LIBRESPOT_SYSTEM_CACHE=/var/lib/raspotify", content)
        self.assertNotIn("DISABLE_CREDENTIAL_CACHE", content)
        self.assertIn(("restart", "raspotify.service"), self.commands)
        self.assertEqual(applied["name"], self.settings["spotifyDeviceName"])
        self.commands.clear()
        receiver.apply_settings()
        self.assertNotIn(("restart", "raspotify.service"), self.commands)
        self.assertIn(("start", "raspotify.service"), self.commands)

    def test_name_or_audio_change_restarts_only_receiver(self):
        receiver.apply_settings()
        self.commands.clear()
        self.settings["receiver"]["audioDevice"] = "plughw:CARD=vc4hdmi,DEV=0"
        self.save()
        receiver.apply_settings()
        self.assertIn(("restart", "raspotify.service"), self.commands)
        self.assertTrue(all("musicdimension.service" not in command for command in self.commands))

    def test_disable_stops_receiver(self):
        self.settings["receiver"]["enabled"] = False
        self.save()
        receiver.apply_settings()
        self.assertIn(("disable", "--now", "raspotify.service"), self.commands)
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

    def test_dropin_enables_credentials_and_discovery(self):
        content = (ROOT / "deploy/raspberry-pi/raspotify-musicdimension.conf").read_text()
        self.assertIn("UnsetEnvironment=LIBRESPOT_DISABLE_CREDENTIAL_CACHE LIBRESPOT_DISABLE_DISCOVERY", content)
        self.assertIn("EnvironmentFile=/etc/raspotify/musicdimension.env", content)


if __name__ == "__main__":
    unittest.main()