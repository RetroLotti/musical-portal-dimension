#!/usr/bin/env python3
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tempfile


REQUEST = Path("/var/lib/musicdimension-receiver/requests/bluetooth-request.json")
STATUS = Path("/var/lib/musicdimension-receiver/bluetooth-status.json")
BLUETOOTHCTL = "/usr/bin/bluetoothctl"
ADDRESS_PATTERN = re.compile(r"\A(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\Z")


def read_request():
    descriptor = os.open(REQUEST, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "r", encoding="utf-8") as source:
        metadata = os.fstat(source.fileno())
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > 4096:
            raise ValueError("Invalid Bluetooth request")
        request = json.loads(source.read(4097))
    if not isinstance(request, dict) or request.get("operation") not in ("scan", "connect"):
        raise ValueError("Invalid Bluetooth operation")
    request_id = request.get("requestId")
    if not isinstance(request_id, str) or not re.fullmatch(r"[A-Fa-f0-9-]{1,64}", request_id):
        raise ValueError("Invalid Bluetooth request identifier")
    address = request.get("address")
    if request["operation"] == "connect" and (not isinstance(address, str) or not ADDRESS_PATTERN.fullmatch(address)):
        raise ValueError("Choose a Bluetooth device with a valid address")
    return request


def run_bluetoothctl(*arguments, timeout=15):
    return subprocess.run([BLUETOOTHCTL, *arguments], capture_output=True, text=True, timeout=timeout, check=False)


def device_info(address):
    result = run_bluetoothctl("info", address)
    fields = {}
    for line in result.stdout.splitlines():
        key, separator, value = line.strip().partition(":")
        if separator:
            fields[key] = value.strip()
    name = fields.get("Alias") or fields.get("Name") or address
    name = "".join(character for character in name if ord(character) >= 32 and ord(character) != 127)[:100]
    return {"address": address.upper(), "name": name, "paired": fields.get("Paired") == "yes",
            "connected": fields.get("Connected") == "yes"}


def known_device_addresses():
    result = run_bluetoothctl("devices")
    addresses = []
    for line in result.stdout.splitlines():
        match = re.match(r"^Device ((?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2})(?:\s|$)", line)
        if match and match.group(1).upper() not in addresses:
            addresses.append(match.group(1).upper())
        if len(addresses) >= 40:
            break
    return addresses


def scan_devices():
    run_bluetoothctl("power", "on")
    run_bluetoothctl("--timeout", "10", "scan", "on", timeout=15)
    run_bluetoothctl("scan", "off")
    return [device_info(address) for address in known_device_addresses()]


def connect_device(address):
    run_bluetoothctl("power", "on")
    if not device_info(address)["paired"]:
        result = run_bluetoothctl("--agent", "NoInputNoOutput", "--timeout", "25", "pair", address, timeout=30)
        if result.returncode != 0 and not device_info(address)["paired"]:
            raise RuntimeError("Pairing failed. Put the speaker in pairing mode and try again; devices that require a PIN need an interactive pairing agent.")
    run_bluetoothctl("trust", address)
    result = run_bluetoothctl("--timeout", "20", "connect", address, timeout=25)
    device = device_info(address)
    if result.returncode != 0 and not device["connected"]:
        detail = (result.stderr or result.stdout).strip()
        raise RuntimeError("Could not connect to the Bluetooth device." + (" " + detail[:240] if detail else ""))
    if not device["connected"]:
        raise RuntimeError("Bluetooth pairing completed, but the device did not connect. Make sure it is powered on and in range.")
    return [device_info(known_address) for known_address in known_device_addresses()]


def atomic_write(path, content):
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=".bluetooth-")
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            os.fchmod(output.fileno(), 0o644)
            output.write(content)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def main():
    request = read_request()
    request_id = request["requestId"]
    status = {"requestId": request_id, "state": "running", "operation": request["operation"], "devices": []}
    atomic_write(STATUS, json.dumps(status))
    try:
        if request["operation"] == "scan":
            status["devices"] = scan_devices()
            status["message"] = "Scan complete. Pair a device before assigning it to a tag."
        else:
            status["devices"] = connect_device(request["address"])
            status["message"] = "Bluetooth device paired and connected."
        status["state"] = "complete"
    except Exception as exception:
        status["state"] = "error"
        status["message"] = str(exception)[:400]
    atomic_write(STATUS, json.dumps(status))


if __name__ == "__main__":
    main()