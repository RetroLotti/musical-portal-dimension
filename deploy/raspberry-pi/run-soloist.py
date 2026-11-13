#!/usr/bin/env python3
import json
import os
from pathlib import Path
import sys


def main():
    settings = json.loads(Path("/etc/musicdimension-soloist/receiver.json").read_text(encoding="utf-8"))
    key = settings.get("apiKey", "")
    if not key:
        print("Save a Spotify Soloist API key in Spotify settings before starting the receiver.", file=sys.stderr)
        return 1
    executable = "/opt/musicdimension-soloist/soloist"
    os.execv(executable, [executable, "--device-name", settings["name"], "--api-key", key,
                          "--data-dir", "/var/lib/musicdimension/soloist", "--cache-dir", "/var/cache/musicdimension/soloist",
                          "--cache-size", "256", "--ws", "127.0.0.1:9876"])


if __name__ == "__main__":
    sys.exit(main())