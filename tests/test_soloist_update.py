import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

SPEC = importlib.util.spec_from_file_location("updater", Path(__file__).resolve().parents[1] / "deploy/raspberry-pi/update-soloist.py")
updater = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(updater)


def elf(machine=183, content=b"working build"):
    header = bytearray(20)
    header[:4] = b"\x7fELF"
    header[5] = 1
    header[18:20] = machine.to_bytes(2, "little")
    return bytes(header) + content


def archive(name="soloist", payload=None, link=False):
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as output:
        item = tarfile.TarInfo(name)
        data = elf() if payload is None else payload
        item.size = len(data)
        if link:
            item.type = tarfile.SYMTYPE
            item.linkname = "/etc/passwd"
        output.addfile(item, io.BytesIO(data))
    return buffer.getvalue()


class Response(io.BytesIO):
    headers = {"ETag": '"build-one"'}

    def geturl(self):
        return "https://soloist-builds.spotifycdn.com/soloist_release_arm64.tar.gz"


class SoloistUpdateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for name, value in (("DIRECTORY", self.root), ("STATUS", self.root / "update.json")):
            patcher = patch.object(updater, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        patcher = patch.object(updater.platform, "machine", return_value="aarch64")
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_installs_official_download_and_leaves_existing_session_alone(self):
        session = self.root / "session"
        session.write_text("private session")
        with patch.object(updater, "urlopen", return_value=Response(archive())), patch.object(updater, "installed_version", return_value="Soloist fixture"):
            result = updater.update()
        self.assertEqual(result["state"], "updated")
        self.assertEqual((self.root / "soloist").read_bytes(), elf())
        self.assertEqual(session.read_text(), "private session")

    def test_same_bytes_are_not_reinstalled(self):
        executable = self.root / "soloist"
        executable.write_bytes(elf())
        before = executable.stat().st_mtime_ns
        with patch.object(updater, "urlopen", return_value=Response(archive())), patch.object(updater, "installed_version", return_value="Soloist fixture"):
            self.assertEqual(updater.update()["state"], "current")
        self.assertEqual(executable.stat().st_mtime_ns, before)

    def test_arm32_userspace_on_arm64_kernel(self):
        with patch.object(updater.struct, "calcsize", return_value=4), patch.object(updater, "urlopen", return_value=Response(archive(payload=elf(40)))) as download, patch.object(updater, "installed_version", return_value="ARM32 build"):
            self.assertEqual(updater.update()["state"], "updated")
            self.assertTrue(download.call_args.args[0].full_url.endswith("soloist_release_arm32.tar.gz"))

    def test_etag_checked_on_every_start(self):
        (self.root / "soloist").write_bytes(elf())
        (self.root / "build.json").write_text(json.dumps({"etag": '"build-one"'}))
        with patch.object(updater, "urlopen", side_effect=HTTPError("url", 304, "Unchanged", {}, None)) as download, patch.object(updater, "installed_version", return_value="Soloist fixture"):
            self.assertEqual(updater.update()["state"], "current")
            self.assertEqual(download.call_args.args[0].get_header("If-none-match"), '"build-one"')

    def test_network_failure_retains_working_binary(self):
        executable = self.root / "soloist"
        executable.write_bytes(elf())
        with patch.object(updater, "urlopen", side_effect=OSError("offline")), patch.object(updater, "installed_version", return_value="Soloist fixture"):
            self.assertEqual(updater.update()["state"], "offline")
        self.assertEqual(executable.read_bytes(), elf())

    def test_network_failure_without_usable_binary_is_error(self):
        with patch.object(updater, "urlopen", side_effect=OSError("offline")):
            with self.assertRaises(RuntimeError):
                updater.update()

    def test_bad_candidate_does_not_replace_installed_binary(self):
        executable = self.root / "soloist"
        executable.write_bytes(elf())
        with patch.object(updater, "urlopen", return_value=Response(archive(payload=elf(62)))), patch.object(updater, "installed_version", return_value="Soloist fixture"):
            self.assertEqual(updater.update()["state"], "offline")
        self.assertEqual(executable.read_bytes(), elf())

    def test_unsafe_archive_rejected(self):
        for name, link in (("../soloist", False), ("/soloist", False), ("soloist", True)):
            with self.subTest(name=name, link=link):
                source = self.root / "download.tar.gz"
                source.write_bytes(archive(name=name, link=link))
                with self.assertRaises(ValueError):
                    updater.extract_executable(source, self.root / "candidate", 183)

    def test_expired_binary_does_not_suppress_new_download(self):
        (self.root / "soloist").write_bytes(elf())
        (self.root / "build.json").write_text(json.dumps({"etag": '"expired"'}))
        with patch.object(updater, "urlopen", return_value=Response(archive(payload=elf(content=b"new")))) as download, patch.object(updater, "installed_version", side_effect=[None, "New build"]):
            self.assertEqual(updater.update()["state"], "updated")
            self.assertIsNone(download.call_args.args[0].get_header("If-none-match"))


if __name__ == "__main__":
    unittest.main()