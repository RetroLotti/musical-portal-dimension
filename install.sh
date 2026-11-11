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

echo "Detected ${ARCHITECTURE}; selecting ${RUNTIME_ID}."
echo "Installing native USB/audio dependencies..."
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y \
    ca-certificates \
    curl \
    libusb-1.0-0 \
    libusb-1.0-0-dev \
    libvlc5 \
    python3 \
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

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TEMP_DIR}"' EXIT

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
    if [[ "${RUNTIME_ID}" != "linux-arm64" ]]; then
        echo "No ${RUNTIME_ID} release asset exists. The automatic source-build fallback requires 64-bit Raspberry Pi OS." >&2
        echo "Use Raspberry Pi OS 64-bit or publish a linux-arm release first." >&2
        exit 1
    fi

    echo "No ARM64 release asset exists; building a self-contained .NET 10 ARM64 publish on this Pi."
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
        exit 1
    fi

    SOURCE_DIR="$(dirname "${PROJECT_FILE}")"
    "${TEMP_DIR}/dotnet/dotnet" publish "${PROJECT_FILE}" --framework net10.0 --runtime linux-arm64 --configuration Release --self-contained true --output "${TEMP_DIR}/publish"
    PUBLISH_DIR="${TEMP_DIR}/publish"
fi

SERVICE_FILE="${PUBLISH_DIR}/deploy/raspberry-pi/musicdimension.service"
UDEV_FILE="${PUBLISH_DIR}/deploy/raspberry-pi/99-lego-dimensions.rules"
if [[ ! -f "${SERVICE_FILE}" || ! -f "${UDEV_FILE}" ]]; then
    echo "The release is missing the Raspberry Pi service or USB rule." >&2
    exit 1
fi

mkdir -p "${INSTALL_DIR}/config" "${INSTALL_DIR}/media"
for data_directory in config media; do
    if [[ -d "${PUBLISH_DIR}/${data_directory}" ]]; then
        cp -an "${PUBLISH_DIR}/${data_directory}/." "${INSTALL_DIR}/${data_directory}/"
    fi
done

systemctl stop "${SERVICE_NAME}" >/dev/null 2>&1 || true
shopt -s dotglob nullglob
for existing in "${INSTALL_DIR}"/*; do
    name="$(basename "${existing}")"
    case "${name}" in
        config|media) continue ;;
    esac
    rm -rf -- "${existing}"
done
for item in "${PUBLISH_DIR}"/*; do
    name="$(basename "${item}")"
    case "${name}" in
        config|media) continue ;;
    esac
    cp -a "${item}" "${INSTALL_DIR}/"
done
shopt -u dotglob nullglob

chown -R "${SERVICE_USER}:${SERVICE_USER}" "${INSTALL_DIR}/config" "${INSTALL_DIR}/media"
chmod 0755 "${INSTALL_DIR}/MusicDimension"
install -m 0644 "${UDEV_FILE}" /etc/udev/rules.d/99-lego-dimensions.rules
install -m 0644 "${SERVICE_FILE}" "/etc/systemd/system/${SERVICE_NAME}.service"

udevadm control --reload-rules
udevadm trigger --subsystem-match=usb
systemctl daemon-reload
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
echo "Web manager:    http://127.0.0.1:5080 (use an SSH tunnel from another computer)."
echo "Spotify is disabled by default; configure config/config.json and complete OAuth interactively before enabling it."