#!/usr/bin/env python3
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import struct
import subprocess
import tarfile
import tempfile
from urllib.error import HTTPError, URLError
from urllib.parse import urlparse
from urllib.request import Request, urlopen

DIRECTORY = Path("/opt/musicdimension-soloist")
STATUS = Path("/var/lib/musicdimension-receiver/update.json")
ARCHITECTURES = {"aarch64": ("arm64", 183), "arm64": ("arm64", 183), "armv7l": ("arm32", 40), "armv8l": ("arm32", 40), "x86_64": ("x86_64", 62)}
MAX_ARCHIVE_BYTES = 256 * 1024 * 1024
MAX_EXECUTABLE_BYTES = 512 * 1024 * 1024


def atomic_json(path, value):
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=".soloist-")
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            output.write(json.dumps(value))
        os.chmod(temporary, 0o644)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def installed_version(executable, required=False):
    try:
        result = subprocess.run([str(executable), "--version"], capture_output=True, text=True, timeout=15, check=False)
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()[:4096]
        if required:
            output = result.stderr + "\n" + result.stdout
            versions = sorted(set(re.findall(r"(?:GLIBC|GLIBCXX|CXXABI)_[0-9.]+", output)))
            if versions and ("not found" in output or "not available" in output):
                libc = " ".join(platform.libc_ver()).strip() or "unknown libc"
                raise RuntimeError("The downloaded Soloist cannot run on this OS: missing " + ", ".join(versions)
                                   + f" (detected {libc}). Use a supported newer Raspberry Pi OS; do not replace glibc manually.")
            library = re.search(r"error while loading shared libraries:\s*([A-Za-z0-9_.+\-]+)", output)
            if library:
                raise RuntimeError("The downloaded Soloist cannot run: missing shared library " + library.group(1)
                                   + ". Install its OS package or use a compatible Raspberry Pi OS.")
            if result.returncode == 10:
                raise RuntimeError("Spotify's downloaded Soloist build reports that it has expired (exit 10). Check the Pi clock and Spotify's downloads page.")
            raise RuntimeError(f"The downloaded Soloist --version check failed (exit {result.returncode}). Check OS compatibility and Spotify's current build requirements.")
        return None
    except (OSError, subprocess.SubprocessError) as error:
        if required:
            if isinstance(error, subprocess.TimeoutExpired):
                raise RuntimeError("The downloaded Soloist --version check timed out.") from None
            raise RuntimeError("The downloaded Soloist could not be executed. Check its architecture, dynamic loader, permissions, and OS compatibility.") from None
        return None


def failure_reason(error):
    if isinstance(error, HTTPError):
        return f"Spotify's Soloist download returned HTTP {error.code}. Check CDN access and the official downloads page."
    if isinstance(error, URLError):
        return "The Soloist download could not connect over HTTPS. Check DNS, TLS certificates, the Pi clock, proxy settings, and internet access."
    if isinstance(error, (RuntimeError, ValueError)):
        return str(error)
    if isinstance(error, tarfile.TarError):
        return "The Soloist archive is incomplete or invalid. Retry the official download."
    if isinstance(error, OSError):
        return "The Soloist update encountered an OS/filesystem error. Check free disk space, permissions, and download access."
    return "The Soloist update failed unexpectedly. Check the updater service logs."


def file_hash(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        while True:
            chunk = source.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def extract_executable(archive, target, machine):
    with tarfile.open(archive, "r:gz") as package:
        candidates = [item for item in package.getmembers() if PurePosixPath(item.name).name == "soloist"]
        if len(candidates) != 1:
            raise ValueError("Archive must contain exactly one Soloist executable")
        item = candidates[0]
        path = PurePosixPath(item.name)
        if not item.isfile() or path.is_absolute() or ".." in path.parts or not 0 < item.size <= MAX_EXECUTABLE_BYTES:
            raise ValueError("Unsafe Soloist archive entry")
        with package.extractfile(item) as source, open(target, "xb") as output:
            shutil.copyfileobj(source, output, length=1024 * 1024)
    with open(target, "rb") as source:
        header = source.read(20)
    if len(header) < 20 or header[:4] != b"\x7fELF" or header[5] != 1 or int.from_bytes(header[18:20], "little") != machine:
        raise ValueError("Soloist executable does not match the device architecture")
    os.chmod(target, 0o755)


def update():
    platform_name = platform.machine().lower()
    if platform_name in ("aarch64", "arm64") and struct.calcsize("P") == 4:
        platform_name = "armv7l"
    architecture, machine = ARCHITECTURES[platform_name]
    executable = DIRECTORY / "soloist"
    metadata_file = DIRECTORY / "build.json"
    current_version = installed_version(executable) if executable.is_file() else None
    try:
        metadata = json.loads(metadata_file.read_text()) if metadata_file.is_file() else {}
    except (OSError, ValueError):
        metadata = {}
    headers = {"User-Agent": "MusicDimension-Soloist-Updater"}
    if current_version and metadata.get("etag"):
        headers["If-None-Match"] = metadata["etag"]
    request = Request(f"https://soloist-builds.spotifycdn.com/soloist_release_{architecture}.tar.gz", headers=headers)
    try:
        with tempfile.TemporaryDirectory(dir=DIRECTORY, prefix=".update-") as temporary:
            archive = Path(temporary) / "soloist.tar.gz"
            with urlopen(request, timeout=30) as response, open(archive, "wb") as output:
                final = urlparse(response.geturl())
                if final.scheme != "https" or final.hostname != "soloist-builds.spotifycdn.com":
                    raise ValueError("Unexpected Soloist download host")
                total = 0
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    total += len(chunk)
                    if total > MAX_ARCHIVE_BYTES:
                        raise ValueError("Soloist download exceeds the size limit")
                    output.write(chunk)
                etag = response.headers.get("ETag")
            candidate = Path(temporary) / "soloist"
            extract_executable(archive, candidate, machine)
            version = installed_version(candidate, required=True)
            if not version:
                raise ValueError("Downloaded Soloist failed its version/startup check")
            digest = file_hash(candidate)
            changed = not executable.is_file() or file_hash(executable) != digest
            if changed:
                os.replace(candidate, executable)
            atomic_json(metadata_file, {"version": version, "etag": etag, "sha256": digest})
            result = dict(state="updated" if changed else "current", version=version, error=None)
    except HTTPError as error:
        if error.code == 304 and current_version:
            result = dict(state="current", version=current_version, error=None)
        elif current_version:
            result = dict(state="offline", version=current_version, error=failure_reason(error) + " Retaining the installed working build.")
        else:
            raise RuntimeError(failure_reason(error) + " No working installed Soloist build is available.") from None
    except Exception as error:
        if not current_version:
            raise RuntimeError(failure_reason(error) + " No working installed Soloist build is available.") from None
        result = dict(state="offline", version=current_version, error=failure_reason(error) + " Retaining the installed working build.")
    atomic_json(STATUS, result)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--startup", action="store_true")
    arguments = parser.parse_args()
    DIRECTORY.mkdir(mode=0o755, parents=True, exist_ok=True)
    STATUS.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
    try:
        result = update()
        print("Soloist update check: " + result["state"])
        if arguments.startup and result["state"] == "updated":
            subprocess.run(["/usr/bin/systemctl", "try-restart", "musicdimension-soloist.service"], check=True, timeout=30)
    except Exception as error:
        message = failure_reason(error)
        atomic_json(STATUS, dict(state="failed", version=None, error=message))
        print("Soloist update failed: " + message)
        if arguments.startup:
            print("Soloist is unavailable; local playback and web settings remain available.")
            return
        raise


if __name__ == "__main__":
    main()