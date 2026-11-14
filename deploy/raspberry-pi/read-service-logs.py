#!/usr/bin/env python3
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tempfile


REQUEST = Path("/var/lib/musicdimension-receiver/requests/log-request.json")
STATUS = Path("/var/lib/musicdimension-receiver/log-status.json")
JOURNALCTL = "/usr/bin/journalctl"
SERVICES = {
    "app": "musicdimension.service",
    "audio": "musicdimension-audio.service",
    "soloist": "musicdimension-soloist.service",
    "receiver": "musicdimension-receiver.service",
    "bluetooth": "musicdimension-bluetooth.service",
    "bluetooth-daemon": "bluetooth.service",
    "power": None,
}
SECRET_PATTERN = re.compile(
    r"(?i)\b(api[-_ ]?key|client[-_ ]?secret|access[-_ ]?token|refresh[-_ ]?token|authorization)\b"
    r"(\s*[:=]\s*)(?:\"[^\"]*\"|'[^']*'|[^\s,;]+)"
)
MAX_OUTPUT_BYTES = 64 * 1024


def read_request():
    descriptor = os.open(REQUEST, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "r", encoding="utf-8") as source:
        metadata = os.fstat(source.fileno())
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > 4096:
            raise ValueError("Invalid log request")
        request = json.loads(source.read(4097))
    if not isinstance(request, dict):
        raise ValueError("Invalid log request")
    request_id = request.get("requestId")
    service = request.get("service")
    lines = request.get("lines")
    if not isinstance(request_id, str) or not re.fullmatch(r"[A-Fa-f0-9-]{1,64}", request_id):
        raise ValueError("Invalid log request identifier")
    if service not in SERVICES:
        raise ValueError("Choose a supported Music Dimension service")
    if type(lines) is not int or lines not in (50, 100, 200):
        raise ValueError("Choose 50, 100, or 200 log lines")
    return request_id, service, lines


def redact_logs(content):
    return SECRET_PATTERN.sub(lambda match: match.group(1) + match.group(2) + "[REDACTED]", content)


def read_power_status():
    try:
        result = subprocess.run(["/usr/bin/vcgencmd", "get_throttled"], capture_output=True, text=True, timeout=5, check=False)
    except FileNotFoundError as exception:
        raise RuntimeError("vcgencmd is unavailable on this Raspberry Pi OS installation") from exception
    if result.returncode != 0:
        raise RuntimeError("vcgencmd could not read Raspberry Pi power status")
    match = re.fullmatch(r"\s*throttled=0x([0-9a-fA-F]+)\s*", result.stdout)
    if not match:
        raise RuntimeError("vcgencmd returned an unrecognized power-status value")

    flags = int(match.group(1), 16)
    lines = ["Raspberry Pi firmware power status"]
    for label, current_bit, historical_bit in (
        ("Under-voltage", 0, 16),
        ("ARM frequency capped", 1, 17),
        ("CPU throttled", 2, 18),
        ("Soft temperature limit", 3, 19),
    ):
        current = bool(flags & (1 << current_bit))
        historical = bool(flags & (1 << historical_bit))
        lines.append(f"{label}: current={'yes' if current else 'no'}, since boot={'yes' if historical else 'no'}")

    core = subprocess.run(["/usr/bin/vcgencmd", "measure_volts", "core"], capture_output=True, text=True, timeout=5, check=False)
    if core.returncode == 0 and core.stdout.strip():
        lines.append(f"SoC core voltage reading: {core.stdout.strip()}")
    lines.append("Note: SoC core voltage is not the 5 V input rail; firmware reports input power via the under-voltage flags above.")
    return "\n".join(lines) + "\n"


def read_logs(service, lines):
    if service == "power":
        return read_power_status()
    result = subprocess.run(
        [JOURNALCTL, "--unit", SERVICES[service], "--lines", str(lines), "--no-pager", "--output=short-iso-precise"],
        capture_output=True, text=True, timeout=15, check=False
    )
    if result.returncode != 0:
        raise RuntimeError("journalctl could not read this service's logs")
    output = redact_logs(result.stdout)
    encoded = output.encode("utf-8", errors="replace")
    if len(encoded) > MAX_OUTPUT_BYTES:
        output = encoded[-MAX_OUTPUT_BYTES:].decode("utf-8", errors="replace")
        output = "[Earlier output truncated to the latest 64 KiB]\n" + output
    return output


def atomic_write(path, content):
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=".logs-")
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            os.fchmod(output.fileno(), 0o644)
            output.write(content)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def main():
    request_id, service, lines = read_request()
    status = {"requestId": request_id, "service": service, "lines": lines, "state": "running", "logs": ""}
    atomic_write(STATUS, json.dumps(status))
    try:
        status["logs"] = read_logs(service, lines)
        status["state"] = "complete"
    except Exception:
        status["state"] = "error"
        status["message"] = "Could not read service logs. Check journalctl and the log-reader service."
    atomic_write(STATUS, json.dumps(status))


if __name__ == "__main__":
    main()