import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
INSTALLER = ROOT / "install.sh"


def config_initialization_script():
    installer = INSTALLER.read_text(encoding="utf-8")
    match = re.search(
        r"python3 - \"\$\{INSTALL_DIR\}/config/config\.json\" \"\$\{INSTALL_DIR\}/config/music\.json\" <<'PY'\n(.*?)\nPY",
        installer,
        re.DOTALL,
    )
    if not match:
        raise AssertionError("Installer config initialization block not found")
    return match.group(1)


class InstallerConfigDefaultsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name) / "config"
        self.directory.mkdir()
        self.config = self.directory / "config.json"
        self.music = self.directory / "music.json"

    def run_installer_config_initialization(self):
        subprocess.run(
            [sys.executable, "-", str(self.config), str(self.music)],
            input=config_initialization_script(),
            text=True,
            check=True,
            capture_output=True,
        )

    def test_partial_install_creates_missing_config_and_music(self):
        self.run_installer_config_initialization()
        config = json.loads(self.config.read_text(encoding="utf-8"))
        music = json.loads(self.music.read_text(encoding="utf-8"))
        self.assertFalse(config["enable"])
        self.assertEqual(config["clientId"], "")
        self.assertTrue(config["spotifyDeviceName"].startswith("Music Dimension ("))
        self.assertEqual(config["mediaMaxFileSizeMb"], 512)
        self.assertTrue(config["receiver"]["enabled"])
        self.assertEqual(config["receiver"]["audioDevice"], "plughw:CARD=Headphones,DEV=0")
        self.assertEqual(music["registeredTags"], [])
        self.assertEqual(music["controlTags"], {})

    def test_existing_settings_and_library_are_preserved(self):
        config = {"enable": False, "clientId": "kept-client", "clientSecret": "kept-secret",
                  "spotifyDeviceName": "Kitchen", "mediaMaxFileSizeMb": 42,
                  "receiver": {"enabled": False, "audioDevice": "hw:2,0", "bitrate": 320}}
        music = {"registeredTags": [{"uid": "AA-BB", "name": "Kept tag", "songs": []}], "controlTags": {"CC-DD": "shuffle"}}
        self.config.write_text(json.dumps(config), encoding="utf-8")
        self.music.write_text(json.dumps(music), encoding="utf-8")
        self.run_installer_config_initialization()
        self.assertEqual(json.loads(self.config.read_text(encoding="utf-8")), config)
        self.assertEqual(json.loads(self.music.read_text(encoding="utf-8")), music)

    def test_missing_music_preserves_existing_spotify_settings(self):
        config = {"enable": True, "clientId": "kept-client", "clientSecret": "kept-secret", "spotifyDeviceName": "Kitchen"}
        self.config.write_text(json.dumps(config), encoding="utf-8")
        self.run_installer_config_initialization()
        updated = json.loads(self.config.read_text(encoding="utf-8"))
        self.assertEqual(updated["clientId"], "kept-client")
        self.assertEqual(updated["clientSecret"], "kept-secret")
        self.assertEqual(updated["spotifyDeviceName"], "Kitchen")
        self.assertEqual(updated["receiver"]["audioDevice"], "plughw:CARD=Headphones,DEV=0")
        self.assertEqual(json.loads(self.music.read_text(encoding="utf-8"))["registeredTags"], [])

    def test_legacy_default_audio_device_migrates_to_headphone_output(self):
        config = {"enable": True, "clientId": "kept-client", "spotifyDeviceName": "Kitchen",
                  "receiver": {"enabled": True, "audioDevice": "default", "bitrate": 320}}
        self.config.write_text(json.dumps(config), encoding="utf-8")
        self.run_installer_config_initialization()
        updated = json.loads(self.config.read_text(encoding="utf-8"))
        self.assertEqual(updated["receiver"]["audioDevice"], "plughw:CARD=Headphones,DEV=0")
        self.assertEqual(updated["receiver"]["bitrate"], 320)
        self.assertEqual(updated["clientId"], "kept-client")


if __name__ == "__main__":
    unittest.main()