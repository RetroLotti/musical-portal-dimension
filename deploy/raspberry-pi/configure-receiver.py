#!/usr/bin/env python3
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tempfile

CONFIG = Path("/opt/musicdimension/config/config.json")
KEY = Path("/opt/musicdimension/config/soloist-key.json")
ENVIRONMENT = Path("/etc/musicdimension-soloist/receiver.json")
AUDIO = Path("/etc/musicdimension-soloist/pulse.pa")
STATUS = Path("/var/lib/musicdimension-receiver/status.json")
UPDATE = Path("/var/lib/musicdimension-receiver/update.json")


def read_json(path, maximum):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "r", encoding="utf-8") as source:
        metadata = os.fstat(source.fileno())
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > maximum:
            raise ValueError("Invalid configuration file")
        content = source.read(maximum + 1)
        if len(content) > maximum:
            raise ValueError("Invalid configuration file")
        return json.loads(content)


def read_settings():
    config = read_json(CONFIG, 65536)
    receiver = config.get("receiver", {})
    enabled = receiver.get("enabled", True)
    name = config.get("spotifyDeviceName", "")
    device = receiver.get("audioDevice", "plughw:CARD=Headphones,DEV=0")
    bitrate = receiver.get("bitrate", 160)
    key = read_json(KEY, 8192) if KEY.exists() else ""
    if type(enabled) is not bool or type(bitrate) is not int or bitrate not in (96, 160, 320):
        raise ValueError("Invalid receiver enablement or bitrate")
    if not isinstance(name, str) or not name.strip() or len(name) > 100 or any(ord(char) < 32 or ord(char) == 127 for char in name):
        raise ValueError("Enter a receiver name between 1 and 100 characters")
    if not isinstance(device, str) or not re.fullmatch(r"[A-Za-z0-9_:,=.\-]{1,128}", device):
        raise ValueError("Invalid ALSA device")
    if not isinstance(key, str) or len(key) > 4096 or any(ord(char) < 33 or ord(char) > 126 for char in key):
        raise ValueError("Invalid Soloist API key")
    return enabled, name, device, key


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


def set_runtime_owner():
    import grp
    os.chown(ENVIRONMENT, 0, grp.getgrnam("musicdimension").gr_gid)
    os.chmod(ENVIRONMENT, 0o640)


def apply_settings():
    enabled, name, device, key = read_settings()
    content = json.dumps({"name": name, "apiKey": key}, ensure_ascii=False)
    audio = "\n".join([
        ".fail",
        "load-module module-native-protocol-unix socket=/run/musicdimension-audio/native",
        "load-module module-alsa-sink device=" + device + " sink_name=musicdimension",
        "set-default-sink musicdimension",
        "load-module module-suspend-on-idle timeout=5",
        "",
    ])
    changed = not ENVIRONMENT.exists() or ENVIRONMENT.read_text(encoding="utf-8") != content
    audio_changed = not AUDIO.exists() or AUDIO.read_text(encoding="utf-8") != audio
    if changed:
        atomic_write(ENVIRONMENT, content, 0o600)
    if audio_changed:
        atomic_write(AUDIO, audio, 0o644)
    set_runtime_owner()
    if enabled and key:
        systemctl("enable", "musicdimension-audio.service", "musicdimension-soloist.service")
        systemctl("restart" if audio_changed else "start", "musicdimension-audio.service")
        if changed or audio_changed:
            systemctl("restart", "musicdimension-soloist.service")
        else:
            systemctl("start", "musicdimension-soloist.service")
    else:
        systemctl("disable", "--now", "musicdimension-soloist.service", "musicdimension-audio.service")
    active = systemctl("is-active", "musicdimension-soloist.service", check=False).stdout.strip()
    try:
        update = json.loads(UPDATE.read_text())
    except (OSError, ValueError):
        update = None
    return dict(managed=True, backend="soloist", enabled=enabled, name=name, audioDevice=device,
                activeState=active, credentialsCached=False, apiKeyConfigured=bool(key), update=update,
                error=None if key or not enabled else "Save your Spotify Soloist API key in Spotify settings. It is separate from Web API authorization.")


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