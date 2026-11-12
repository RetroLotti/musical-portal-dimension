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
sudo apt install -y libusb-1.0-0 libusb-1.0-0-dev libvlc5 libvlc-dev vlc-plugin-base
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

The systemd service selects VLC's ALSA output directly so it does not depend on a logged-in PulseAudio session. By default it uses the Pi's headphone output (`plughw:CARD=Headphones,DEV=0`); change `MUSICDIMENSION_ALSA_DEVICE` in `musicdimension.service` to `plughw:CARD=vc4hdmi,DEV=0` to use HDMI instead. After changing the unit, run `sudo systemctl daemon-reload` and `sudo systemctl restart musicdimension`. It listens on all Pi network interfaces at port `5080`; from another device on the same trusted LAN, open `http://<pi-ip-address>:5080` (find the address with `hostname -I`). The web manager currently has no login, so do not forward this port through your router or expose it to the public internet. For a loopback-only listener, remove the `MUSICDIMENSION_WEB_URL` line from the service file.

The LEGO Dimensions Toy Pad rule uses vendor/product `0e6f:0241` and grants access to the `plugdev` group. Replug the portal or reboot after installing it. This rule follows the identifier documented by the [LegoDimensions Linux driver instructions](https://github.com/Ellerbach/LegoDimensions/blob/main/linux-driver-rules/99-lego-dimensions.rules).

### Spotify Settings And Login

Next to **Device name**, click the refresh icon (**Find Spotify devices**) to load the exact names visible to your Spotify account. Choose your Pi from **Available Spotify devices**, then click **Save Spotify settings**. Active devices are marked; restricted devices are shown but cannot be selected because Spotify does not allow controlling them. Loading devices never transfers playback or changes the saved name. Manual name entry remains available. This read-only lookup also works with Spotify playback disabled, but requires account authorization. If no Pi appears, its librespot/raspotify receiver must be running and signed in before Spotify can list it.

The web manager's **Spotify settings** section edits `enable`, `spotifyDeviceName`, `clientId`, and `clientSecret` in `config/config.json`. Saved changes apply immediately without restarting, including enabling Spotify after a local-only startup. Disabling prevents further Spotify playback/control requests; it does not shut down librespot. The secret is never returned to the browser: leave the field blank to keep it, or select **Clear saved client secret** to remove it. This application uses PKCE, so the client secret is not needed for login or token refresh. The token file location is internal and is not web-editable. These settings are available to everyone who can reach the unauthenticated web manager; use only a trusted LAN or an SSH tunnel.

Spotify requires a person's consent once to issue a user refresh token. It cannot be generated unattended from the client ID/secret, and Spotify does not provide a device-code login for this application. After consent, the Pi renews access automatically without a display or browser. It refreshes on startup/use when access has less than a minute remaining, retries an expired/rejected access token once, preserves the existing refresh token when Spotify omits a replacement, and saves any rotated refresh token atomically. No continuously running browser or periodic manual token copying is needed. A revoked/invalid refresh token requires consent again; temporary network errors do not delete saved credentials.

#### First Login From The Website (Headless Pi)

1. Register **exactly** `http://127.0.0.1:8888/callback` as a redirect URI in your Spotify developer app. The client secret is not required for PKCE.
2. Open the Pi's web manager from your computer or phone. In **Spotify settings**, save your client ID, device name, and desired enabled state.
3. Click **Connect Spotify**, then **Authorize with Spotify**, and approve access in the new browser tab.
4. Spotify redirects to `http://127.0.0.1:8888/callback?code=...&state=...`. With this website flow there is no listener on your computer, so a connection-error page is expected. Copy the **complete address from the address bar**, not the error-page text.
5. Return to the original web manager tab/browser, paste that address into **Returned callback URL**, and click **Complete connection** within ten minutes. The Pi exchanges the one-time code and saves `config/tokens.json` itself; the website never returns access/refresh tokens. Treat the returned URL as sensitive and do not share it or commit it to source control.

The website shows **Connected** once credentials are saved. **Refresh access** tests renewal immediately; normal playback/search renews automatically. Reconnect in the same way after revoking app access, changing the client ID, or losing the token file. No need to delete the old file before reconnecting. A new login replaces the previous pending login; restarting the service cancels pending logins. Settings and Spotify authorization are still available to anyone who can reach the unauthenticated web manager. Prefer an SSH tunnel and do not expose it publicly:

```sh
# Run on your computer, then open http://127.0.0.1:5080 there:
ssh -N -L 5080:127.0.0.1:5080 pi@YOUR_PI_ADDRESS
```

#### Optional CLI Login

`./MusicDimension --spotify-login` (or `dotnet run -- --spotify-login` from source) starts a loopback callback listener before printing/opening the authorization link. For a headless Pi, run the following tunnel on your computer, open the printed link there, and leave the login command running on the Pi:

```sh
ssh -N -L 8888:127.0.0.1:8888 pi@YOUR_PI_ADDRESS
```

In this CLI flow the callback completes automatically; no URL pasting is needed. Run the login command from the installation directory as the service user, or ensure the resulting token file belongs to that user. The listener times out after ten minutes and validates OAuth state and PKCE. Normal service startup never opens a browser or waits for consent.

Keep `config/tokens.json` readable/writable by the `musicdimension` service user. On Linux, newly saved credentials are owner-only (`0600`). The token file is ignored by Git; ignore rules do not remove files already tracked or erase past commits. Revoke exposed Spotify authorization if credentials were previously committed. Keep `config/config.json`, `config/music.json`, `config/tokens.json`, and `media/` when updating the published binaries.

### Search Spotify For A Tag

In **Tags & music**, choose **Spotify** as the playback source, then **Search Spotify**. Search for tracks, albums, or playlists by title, artist, or playlist name. Results show artwork and artist/owner details, with links to open the items in Spotify. Selecting a result fills its type and ID; if the tag name is blank, it is filled from the result too. Save the tag as usual. Each tag selects one Spotify track, album, or playlist. Manual type/ID entry remains available.

Search requires enabled Spotify support and an existing OAuth login, but does not require the Pi's playback device to be active. It uses the configured Spotify account on the server and returns up to ten results per submitted search. Spotify developer-app/account restrictions can limit search, particularly playlist availability; when search is unavailable or rate limited, the dialog reports the error and manual ID entry still works. No Spotify credentials are sent to the browser.

### Spotify After A Pi Reboot

There are two separate logins: MusicDimension's Web API OAuth tokens and the librespot/raspotify Spotify Connect receiver's own account session. OAuth tokens do not authenticate the receiver. The application now retries device discovery briefly, explicitly transfers playback to an inactive configured device before play/resume, and targets that device for all playback controls. It never falls back to playing on your phone if the Pi is missing. Startup only checks the Web API connection; it does not take playback away from another device.

If the Pi still only works after selecting it on your phone, check the receiver immediately after reboot, before opening the phone app:

```sh
systemctl status librespot
journalctl -b -u librespot --no-pager
systemctl cat librespot
# If using raspotify instead, substitute raspotify for librespot above.
```

Ensure the receiver service is enabled at boot, can reach Spotify, and uses the exact device name saved in the web manager. Configure persistent authentication/cache according to your installed librespot/raspotify version, with the cache owned by its service user and available at boot. An unauthenticated receiver that only advertises through local discovery may need a phone connection before Spotify's Web API can see it; MusicDimension cannot wake or sign in such a receiver. The receiver service/cache configuration is not installed or managed by this repository. Spotify Web API playback also requires Spotify Premium and the appropriate app/account access.

Cross-publishing validates the managed application and package restore for both ARM RIDs. Actual portal USB access and audio output still require a test on the Pi 3 hardware and its installed Raspberry Pi OS/VLC packages.
  
