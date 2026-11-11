# MusicDimension
A personal creation in order to play music from Spotify via a Raspberry Pi. a practical and solution and also for fun and curiosity. 

Steps in order to get things started: 
- install Raspberry Pi OS via Raspberry Pi Imager
![image](https://github.com/user-attachments/assets/0caa167a-4c3f-4eee-9cff-778317b3828f)

- connect via ssh or however you like and install librespot
  - sudo apt update && sudo apt upgrade -y
  - curl https://sh.rustup.rs -sSf | sh
  - cargo install librespot
- configure librespot
  - (validation needed)
  - sudo nano /etc/systemd/system/librespot.service
  - 

- download linux version of musical dimension
- create app in [spotify developer dashboard](https://developer.spotify.com/dashboard)

- note clientid ( and secret)
- update config.json
- also update music.cfg.json for your hearing pleasure
- connect Lego dimensions portal
- start musical dimension
- 

## Raspberry Pi 3 Model B

The Pi 3 Model B can run either 32-bit ARM (`linux-arm`) or 64-bit ARM (`linux-arm64`). Raspberry Pi OS 64-bit is recommended; the application cross-publishes successfully for both targets. The deployment is self-contained, but VLC and libusb must be installed from Raspberry Pi OS because this project does not bundle Linux VLC libraries.

To install the latest published Pi build and enable its daemon at boot, copy `install.sh` to the Pi and run `sudo bash install.sh`. It detects `arm64` versus `armhf`, installs native packages, preserves existing config and media on reinstall, and enables the systemd service. To install a specific release, pass its exact GitHub tag, for example `sudo bash install.sh v1.2.3`. Published builds contain their matching self-contained .NET runtime. If no release exists yet, the script downloads the .NET 10 SDK temporarily and builds from source on 64-bit Pi OS; 32-bit ARM requires a published `linux-arm` release asset.

### Publish

Run one command on the development machine, choosing the RID that matches `uname -m` on the Pi (`aarch64` means `linux-arm64`; `armv7l` means `linux-arm`):

```sh
dotnet publish MusicDimension.csproj -c Release -r linux-arm64 --self-contained true -o publish
# Or, for 32-bit Raspberry Pi OS:
dotnet publish MusicDimension.csproj -c Release -r linux-arm --self-contained true -o publish
```

Copy the `publish` directory to the Pi, for example to `/tmp/musicdimension-publish`.

### Install And Start At Boot

On Raspberry Pi OS, install the native libraries and create the service account:

```sh
sudo apt update
sudo apt install -y libusb-1.0-0 libusb-1.0-0-dev libvlc5 vlc-plugin-base
getent group plugdev >/dev/null || sudo groupadd --system plugdev
sudo useradd --system --user-group --home-dir /var/lib/musicdimension --create-home --shell /usr/sbin/nologin musicdimension
sudo usermod -aG plugdev,audio musicdimension
```

Install the publish output and the supplied USB rule/service:

```sh
sudo mkdir -p /opt/musicdimension
sudo cp -a /tmp/musicdimension-publish/. /opt/musicdimension/
sudo chown -R musicdimension:musicdimension /opt/musicdimension
sudo install -m 0644 /opt/musicdimension/deploy/raspberry-pi/99-lego-dimensions.rules /etc/udev/rules.d/99-lego-dimensions.rules
sudo install -m 0644 /opt/musicdimension/deploy/raspberry-pi/musicdimension.service /etc/systemd/system/musicdimension.service
sudo udevadm control --reload-rules
sudo udevadm trigger --subsystem-match=usb
sudo systemctl daemon-reload
sudo systemctl enable --now musicdimension
```

The unit runs as the unprivileged `musicdimension` user, restarts on failure, and starts automatically at boot. It needs write access to `/opt/musicdimension/config` and `/opt/musicdimension/media` for tag edits and uploads. Check its state and logs with:

```sh
systemctl status musicdimension
journalctl -u musicdimension -f
```

The web manager listens only on `127.0.0.1:5080`. To open it from another computer, create an SSH tunnel with `ssh -L 5080:127.0.0.1:5080 pi@raspberrypi.local`, then browse to `http://127.0.0.1:5080` on that computer.

The LEGO Dimensions Toy Pad rule uses vendor/product `0e6f:0241` and grants access to the `plugdev` group. Replug the portal or reboot after installing it. This rule follows the identifier documented by the [LegoDimensions Linux driver instructions](https://github.com/Ellerbach/LegoDimensions/blob/main/linux-driver-rules/99-lego-dimensions.rules).

For Spotify, complete the first OAuth login interactively before enabling the system service, then keep the generated `config/tokens.json`. A headless systemd service cannot open the browser for the initial login. Set `enable` to `false` for local-only playback. Keep `config/config.json`, `config/music.json`, `config/tokens.json`, and `media/` when updating the published binaries.

Cross-publishing validates the managed application and package restore for both ARM RIDs. Actual portal USB access and audio output still require a test on the Pi 3 hardware and its installed Raspberry Pi OS/VLC packages.
  
