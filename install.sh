#!/usr/bin/env bash
set -Eeuo pipefail

REPOSITORY="RetroLotti/musical-portal-dimension"
VERSION="${1:-latest}"
INSTALL_DIR="/opt/musicdimension"
SERVICE_NAME="musicdimension"
SERVICE_USER="musicdimension"

if [[ "${EUID}" -ne 0 ]]; then
    echo "Run this installer as root: sudo bash install.sh [release-tag]" >&2
    exit 1
fi

if ! command -v apt-get >/dev/null 2>&1 || ! command -v systemctl >/dev/null 2>&1; then
    echo "This installer requires a Debian-based Raspberry Pi OS with systemd." >&2
    exit 1
fi

ARCHITECTURE="$(dpkg --print-architecture)"
case "${ARCHITECTURE}" in
    arm64) RUNTIME_ID="linux-arm64" ;;
    armhf) RUNTIME_ID="linux-arm" ;;
    *)
        echo "Unsupported architecture: ${ARCHITECTURE}. Expected arm64 or armhf." >&2
        exit 1
        ;;
esac

if [[ "${ARCHITECTURE}" == "armhf" && "$(uname -m)" == armv6* ]]; then
    echo "Spotify Soloist requires ARMv7 or newer. Pi 1 and original Pi Zero are not supported." >&2
    exit 1
fi

echo "Detected ${ARCHITECTURE}; selecting ${RUNTIME_ID}."
echo "Installing native USB/audio dependencies..."
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y \
    ca-certificates \
    curl \
    libusb-1.0-0 \
    libusb-1.0-0-dev \
    libvlc5 \
    libvlc-dev \
    python3 \
    pulseaudio \
    pulseaudio-module-bluetooth \
    pulseaudio-utils \
    bluez \
    alsa-utils \
    vlc-plugin-pulse \
    libatomic1 \
    libnss3 \
    libglib2.0-0 \
    tar \
    vlc-plugin-base

for group in plugdev audio; do
    if ! getent group "${group}" >/dev/null; then
        groupadd --system "${group}"
    fi
done

if ! id "${SERVICE_USER}" >/dev/null 2>&1; then
    useradd --system --user-group --home-dir /var/lib/musicdimension --create-home --shell /usr/sbin/nologin "${SERVICE_USER}"
fi
usermod -aG plugdev,audio "${SERVICE_USER}"

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TEMP_DIR}"' EXIT
ASSET_URL=""

build_from_source() {
    if [[ "${RUNTIME_ID}" != "linux-arm64" ]]; then
        echo "The source-build fallback requires 64-bit Raspberry Pi OS. Use a current ARM release with Soloist support." >&2
        return 1
    fi

    echo "Building a self-contained .NET 10 ARM64 publish from source."
    curl --fail --location --retry 3 --silent --show-error https://dot.net/v1/dotnet-install.sh -o "${TEMP_DIR}/dotnet-install.sh"
    bash "${TEMP_DIR}/dotnet-install.sh" --channel 10.0 --architecture arm64 --install-dir "${TEMP_DIR}/dotnet"

    if [[ "${VERSION}" == "latest" ]]; then
        SOURCE_URL="https://github.com/${REPOSITORY}/archive/refs/heads/main.tar.gz"
    else
        ENCODED_VERSION="$(python3 - "${VERSION}" <<'PY'
import sys
import urllib.parse
print(urllib.parse.quote(sys.argv[1], safe=""))
PY
)"
        SOURCE_URL="https://github.com/${REPOSITORY}/archive/refs/tags/${ENCODED_VERSION}.tar.gz"
    fi

    mkdir -p "${TEMP_DIR}/source"
    curl --fail --location --retry 3 --silent --show-error "${SOURCE_URL}" -o "${TEMP_DIR}/source.tar.gz"
    tar -xzf "${TEMP_DIR}/source.tar.gz" -C "${TEMP_DIR}/source" --no-same-owner
    PROJECT_FILE="$(find "${TEMP_DIR}/source" -name MusicDimension.csproj -print -quit)"
    if [[ -z "${PROJECT_FILE}" ]]; then
        echo "The source archive does not contain MusicDimension.csproj." >&2
        return 1
    fi

    "${TEMP_DIR}/dotnet/dotnet" publish "${PROJECT_FILE}" --framework net10.0 --runtime linux-arm64 --configuration Release --self-contained true --output "${TEMP_DIR}/publish"
    PUBLISH_DIR="${TEMP_DIR}/publish"
}

if [[ -n "${MUSICDIMENSION_PUBLISH_DIR:-}" ]]; then
    PUBLISH_DIR="$(realpath "${MUSICDIMENSION_PUBLISH_DIR}")"
    if [[ ! -f "${PUBLISH_DIR}/MusicDimension" || "${PUBLISH_DIR}" == "${INSTALL_DIR}" ]]; then
        echo "MUSICDIMENSION_PUBLISH_DIR must be a separate published build directory containing MusicDimension." >&2
        exit 1
    fi
else
API_URL="$(python3 - "${REPOSITORY}" "${VERSION}" <<'PY'
import sys
import urllib.parse

repository, version = sys.argv[1:]
if version == "latest":
    print(f"https://api.github.com/repos/{repository}/releases/latest")
else:
    encoded_version = urllib.parse.quote(version, safe="")
    print(f"https://api.github.com/repos/{repository}/releases/tags/{encoded_version}")
PY
)"

ASSET_URL="$(python3 - "${API_URL}" "${RUNTIME_ID}" <<'PY'
import json
import sys
import urllib.error
import urllib.request

api_url, runtime_id = sys.argv[1:]
request = urllib.request.Request(
    api_url,
    headers={"Accept": "application/vnd.github+json", "User-Agent": "MusicDimension-installer"},
)
try:
    with urllib.request.urlopen(request, timeout=30) as response:
        release = json.load(response)
except urllib.error.HTTPError as error:
    if error.code == 404:
        print("")
        raise SystemExit(0)
    raise SystemExit(f"GitHub release lookup failed: HTTP {error.code}")
except Exception as error:
    raise SystemExit(f"Could not query GitHub releases: {error}")

suffix = f"-{runtime_id}.tgz"
assets = [asset for asset in release.get("assets", []) if asset["name"].startswith("MusicDimension-") and asset["name"].endswith(suffix)]
if len(assets) != 1:
    print("")
else:
    print(assets[0]["browser_download_url"])
PY
)"

if [[ -n "${ASSET_URL}" ]]; then
    echo "Downloading ${ASSET_URL}"
    mkdir -p "${TEMP_DIR}/extracted"
    curl --fail --location --retry 3 --silent --show-error "${ASSET_URL}" -o "${TEMP_DIR}/musicdimension.tgz"
    tar -xzf "${TEMP_DIR}/musicdimension.tgz" -C "${TEMP_DIR}/extracted" --no-same-owner

    APP_HOST="$(find "${TEMP_DIR}/extracted" -type f -name MusicDimension -print -quit)"
    if [[ -z "${APP_HOST}" ]]; then
        echo "The release archive does not contain the MusicDimension app host." >&2
        exit 1
    fi
    PUBLISH_DIR="$(dirname "${APP_HOST}")"
else
    build_from_source
fi
fi

SERVICE_FILE="${PUBLISH_DIR}/deploy/raspberry-pi/musicdimension.service"
UDEV_FILE="${PUBLISH_DIR}/deploy/raspberry-pi/99-lego-dimensions.rules"
if [[ ! -f "${SERVICE_FILE}" || ! -f "${UDEV_FILE}" ]]; then
    echo "The release is missing the Raspberry Pi service or USB rule." >&2
    exit 1
fi
for receiver_file in configure-receiver.py configure-bluetooth.py update-soloist.py run-soloist.py musicdimension-audio.service musicdimension-soloist.service musicdimension-receiver.service musicdimension-receiver.path musicdimension-receiver.timer musicdimension-bluetooth.service musicdimension-bluetooth.path; do
    if [[ ! -f "${PUBLISH_DIR}/deploy/raspberry-pi/${receiver_file}" ]]; then
        if [[ -n "${ASSET_URL}" && "${VERSION}" == "latest" ]]; then
            echo "The latest GitHub release is missing Soloist support (${receiver_file}); building current source instead."
            build_from_source
            break
        fi
        echo "Selected install source lacks Soloist integration (${receiver_file}). Use the latest source installer or a compatible release." >&2
        exit 1
    fi
done

for receiver_file in configure-receiver.py configure-bluetooth.py update-soloist.py run-soloist.py musicdimension-audio.service musicdimension-soloist.service musicdimension-receiver.service musicdimension-receiver.path musicdimension-receiver.timer musicdimension-bluetooth.service musicdimension-bluetooth.path; do
    if [[ ! -f "${PUBLISH_DIR}/deploy/raspberry-pi/${receiver_file}" ]]; then
        echo "Selected source is still missing required Soloist file ${receiver_file}. No existing application or receiver services were stopped." >&2
        exit 1
    fi
done

echo "Checking the official Soloist build on this OS before changing installed services..."
getconf GNU_LIBC_VERSION || true
install -d -o root -g root -m 0755 /opt/musicdimension-soloist /var/lib/musicdimension-receiver
if ! /usr/bin/python3 -I "${PUBLISH_DIR}/deploy/raspberry-pi/update-soloist.py"; then
    echo "Soloist preflight failed. Existing application files and service enablement have not been changed." >&2
    echo "Resolve the reported download/runtime problem before retrying. Do not manually replace the system glibc." >&2
    exit 1
fi

OLD_INSTALLATION_FOUND=false
if [[ -L "${INSTALL_DIR}" ]]; then
    echo "Refusing to install through symbolic-link path: ${INSTALL_DIR}" >&2
    exit 1
elif [[ -d "${INSTALL_DIR}" ]]; then
    OLD_INSTALLATION_FOUND=true
    echo "Existing MusicDimension installation found; preserving config and media and replacing application files."
elif [[ -e "${INSTALL_DIR}" ]]; then
    echo "Refusing to remove unexpected non-directory path: ${INSTALL_DIR}" >&2
    exit 1
else
    echo "No previous MusicDimension installation found at ${INSTALL_DIR}; continuing with a clean install."
fi

mkdir -p "${INSTALL_DIR}/config" "${INSTALL_DIR}/media"
chown root:root "${INSTALL_DIR}"
chmod 0755 "${INSTALL_DIR}"
for data_directory in config media; do
    if [[ -d "${PUBLISH_DIR}/${data_directory}" ]]; then
        cp -an "${PUBLISH_DIR}/${data_directory}/." "${INSTALL_DIR}/${data_directory}/"
    fi
done

python3 - "${INSTALL_DIR}/config/config.json" "${INSTALL_DIR}/config/music.json" <<'PY'
import json
import os
import socket
import sys

config_path, music_path = sys.argv[1:]
if os.path.exists(config_path):
    with open(config_path, encoding="utf-8") as source:
        config = json.load(source)
    if not isinstance(config, dict):
        raise SystemExit("config/config.json must contain a JSON object; existing file was left unchanged.")
else:
    config = {
        "enable": False,
        "clientId": "",
        "clientSecret": "",
        "spotifyDeviceName": "",
        "receiver": {"enabled": True, "audioDevice": "plughw:CARD=Headphones,DEV=0", "bitrate": 160},
        "mediaMaxFileSizeMb": 512,
    }
    print("No existing config/config.json found; creating safe defaults. Configure Spotify in the web manager.")

name = config.get("spotifyDeviceName")
if not isinstance(name, str) or not name.strip():
    config["spotifyDeviceName"] = "Music Dimension (" + socket.gethostname() + ")"
receiver = config.setdefault("receiver", {"enabled": True, "audioDevice": "plughw:CARD=Headphones,DEV=0", "bitrate": 160})
if not isinstance(receiver, dict):
    raise SystemExit("config/config.json receiver setting must be an object; existing file was left unchanged.")
if receiver.get("audioDevice") == "default":
    receiver["audioDevice"] = "plughw:CARD=Headphones,DEV=0"
    print("Updated the receiver's unresolved ALSA default to the Raspberry Pi headphone output.")
config.setdefault("mediaMaxFileSizeMb", 512)
temporary = config_path + ".install-tmp"
with open(temporary, "w", encoding="utf-8") as output:
    json.dump(config, output, indent=2)
os.chmod(temporary, 0o600)
os.replace(temporary, config_path)

if not os.path.exists(music_path):
    music = {
        "validSourceTypes": ["spotify", "local", "other"],
        "validSpotifyTypes": ["album", "track", "playlist"],
        "validControlTypes": ["stop", "pause", "play", "next", "previous", "shuffle", "volumeup", "volumedown"],
        "unusedTags": {}, "registeredTags": [], "shortTags": {}, "controlTags": {}
    }
    temporary = music_path + ".install-tmp"
    with open(temporary, "w", encoding="utf-8") as output:
        json.dump(music, output, indent=2)
    os.replace(temporary, music_path)
    print("No existing config/music.json found; created an empty tag library and control-tag map.")
PY

systemctl stop "${SERVICE_NAME}" >/dev/null 2>&1 || true
systemctl stop musicdimension-receiver.path musicdimension-receiver.timer musicdimension-receiver.service >/dev/null 2>&1 || true
echo "Migrating from Raspotify/librespot to Spotify Soloist; existing receiver caches are retained."
for old_receiver in raspotify.service librespot.service; do
    if systemctl cat "${old_receiver}" >/dev/null 2>&1; then
        systemctl disable --now "${old_receiver}"
    fi
done
if [[ "${OLD_INSTALLATION_FOUND}" == true && -d "${INSTALL_DIR}" ]]; then
    shopt -s dotglob nullglob
    for existing in "${INSTALL_DIR}"/*; do
        name="$(basename "${existing}")"
        case "${name}" in
            config|media) continue ;;
        esac
        rm -rf -- "${existing}"
    done
    shopt -u dotglob nullglob
else
    echo "Skipping removal: no previous application files were found."
fi

shopt -s dotglob nullglob
for item in "${PUBLISH_DIR}"/*; do
    name="$(basename "${item}")"
    case "${name}" in
        config|media) continue ;;
    esac
    cp -a "${item}" "${INSTALL_DIR}/"
done
shopt -u dotglob nullglob

chown -R "${SERVICE_USER}:${SERVICE_USER}" "${INSTALL_DIR}/config" "${INSTALL_DIR}/media"
python3 - "${INSTALL_DIR}/config/config.json" <<'PY'
import json
import os
import socket
import sys

path = sys.argv[1]
with open(path, encoding="utf-8") as source:
    config = json.load(source)
if not config.get("spotifyDeviceName", "").strip():
    config["spotifyDeviceName"] = "Music Dimension (" + socket.gethostname() + ")"
config.setdefault("receiver", {"enabled": True, "audioDevice": "plughw:CARD=Headphones,DEV=0", "bitrate": 160})
with open(path, "w", encoding="utf-8") as output:
    json.dump(config, output, indent=2)
os.chmod(path, 0o600)
PY
chmod 0755 "${INSTALL_DIR}/MusicDimension"
install -m 0644 "${UDEV_FILE}" /etc/udev/rules.d/99-lego-dimensions.rules
install -m 0644 "${SERVICE_FILE}" "/etc/systemd/system/${SERVICE_NAME}.service"
install -d -o root -g root -m 0755 /usr/local/lib/musicdimension /etc/musicdimension-soloist /opt/musicdimension-soloist /var/lib/musicdimension-receiver
install -m 0644 "${INSTALL_DIR}/deploy/raspberry-pi/configure-receiver.py" /usr/local/lib/musicdimension/configure-receiver.py
install -m 0644 "${INSTALL_DIR}/deploy/raspberry-pi/configure-bluetooth.py" /usr/local/lib/musicdimension/configure-bluetooth.py
install -m 0644 "${INSTALL_DIR}/deploy/raspberry-pi/update-soloist.py" /usr/local/lib/musicdimension/update-soloist.py
install -m 0644 "${INSTALL_DIR}/deploy/raspberry-pi/run-soloist.py" /usr/local/lib/musicdimension/run-soloist.py
for unit in musicdimension-audio.service musicdimension-soloist.service musicdimension-receiver.service musicdimension-receiver.path musicdimension-receiver.timer musicdimension-bluetooth.service musicdimension-bluetooth.path; do
    install -m 0644 "${INSTALL_DIR}/deploy/raspberry-pi/${unit}" "/etc/systemd/system/${unit}"
done
install -d -o "${SERVICE_USER}" -g "${SERVICE_USER}" -m 0700 /var/lib/musicdimension-receiver/requests
install -d -o "${SERVICE_USER}" -g "${SERVICE_USER}" -m 0700 /var/lib/musicdimension/soloist /var/cache/musicdimension/soloist

udevadm control --reload-rules
udevadm trigger --subsystem-match=usb
systemctl daemon-reload
systemctl start musicdimension-receiver.service
systemctl enable --now bluetooth.service musicdimension-receiver.path musicdimension-receiver.timer musicdimension-bluetooth.path
systemctl enable --now "${SERVICE_NAME}"

if ! systemctl is-active --quiet "${SERVICE_NAME}"; then
    echo "Music Dimension did not start. Recent service logs:" >&2
    journalctl -u "${SERVICE_NAME}" -n 40 --no-pager >&2 || true
    exit 1
fi

echo
echo "Music Dimension is installed and will start automatically at boot."
echo "Service status: systemctl status ${SERVICE_NAME}"
echo "Logs:           journalctl -u ${SERVICE_NAME} -f"
echo "Web manager:    http://<pi-ip-address>:5080 (trusted LAN only; or use an SSH tunnel)."
echo "Spotify Soloist is installed directly from Spotify. The app checks for updated builds at every service start."
echo "Generate your own Soloist API key at https://developer.spotify.com/dashboard/soloist and save it in Spotify settings."
echo "Pair Soloist once from the Spotify app on the same LAN; its session is retained across updates and reboots."
echo "The Soloist API key and the application's Web API OAuth authorization are separate."
echo "Receiver logs:  journalctl -u musicdimension-soloist -f"
echo "Audio logs:     journalctl -u musicdimension-audio -f"