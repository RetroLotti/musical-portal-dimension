#!/usr/bin/env python3
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tempfile

CONFIG = Path("/opt/musicdimension/config/config.json")
ENVIRONMENT = Path("/etc/raspotify/musicdimension.env")
STATUS = Path("/var/lib/musicdimension-receiver/status.json")


def read_settings():
    descriptor = os.open(CONFIG, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "r", encoding="utf-8") as source:
        metadata = os.fstat(source.fileno())
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > 65536:
            raise ValueError("Invalid configuration file")
        content = source.read(65537)
        if len(content) > 65536:
            raise ValueError("Invalid configuration file")
        config = json.loads(content)
    receiver = config.get("receiver", {})
    enabled = receiver.get("enabled", True)
    name = config.get("spotifyDeviceName", "")
    device = receiver.get("audioDevice", "plughw:CARD=Headphones,DEV=0")
    bitrate = receiver.get("bitrate", 160)
    if type(enabled) is not bool or type(bitrate) is not int or bitrate not in (96, 160, 320):
        raise ValueError("Invalid receiver enablement or bitrate")
    if not isinstance(name, str) or not name.strip() or len(name) > 100 or any(ord(char) < 32 or ord(char) == 127 for char in name):
        raise ValueError("Enter a receiver name between 1 and 100 characters")
    if not isinstance(device, str) or not re.fullmatch(r"[A-Za-z0-9_:,=.\-]{1,128}", device):
        raise ValueError("Invalid ALSA device")
    return enabled, name, device, bitrate


def atomic_write(path, content, mode):
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=".musicdimension-")
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            os.fchmod(output.fileno(), mode)
            output.write(content)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def systemctl(*arguments, check=True):
    return subprocess.run(["/usr/bin/systemctl", *arguments], check=check, capture_output=True, text=True, timeout=30)


def apply_settings():
    enabled, name, device, bitrate = read_settings()
    content = "\n".join([
        "# Managed by MusicDimension. Edit receiver settings in the web manager.",
        "LIBRESPOT_NAME=" + json.dumps(name, ensure_ascii=False),
        "LIBRESPOT_BACKEND=alsa",
        "LIBRESPOT_DEVICE=" + device,
        "LIBRESPOT_BITRATE=" + str(bitrate),
        "LIBRESPOT_SYSTEM_CACHE=/var/lib/raspotify",
        "LIBRESPOT_CACHE=/var/cache/raspotify",
        "LIBRESPOT_DISABLE_AUDIO_CACHE=",
        "",
    ])
    changed = not ENVIRONMENT.exists() or ENVIRONMENT.read_text(encoding="utf-8") != content
    if changed:
        atomic_write(ENVIRONMENT, content, 0o600)
    if enabled:
        systemctl("enable", "raspotify.service")
        if changed:
            systemctl("restart", "raspotify.service")
        else:
            systemctl("start", "raspotify.service")
    else:
        systemctl("disable", "--now", "raspotify.service")
    active = systemctl("is-active", "raspotify.service", check=False).stdout.strip()
    credentials = Path("/var/lib/raspotify/credentials.json").is_file()
    return dict(managed=True, enabled=enabled, name=name, audioDevice=device, bitrate=bitrate,
                activeState=active, credentialsCached=credentials, error=None)


def main():
    try:
        status = apply_settings()
    except Exception:
        status = dict(managed=True, activeState="unknown", credentialsCached=False,
                      error="Receiver settings could not be applied. Check journalctl -u musicdimension-receiver.service.")
        atomic_write(STATUS, json.dumps(status), 0o644)
        raise
    atomic_write(STATUS, json.dumps(status), 0o644)


if __name__ == "__main__":
    main()