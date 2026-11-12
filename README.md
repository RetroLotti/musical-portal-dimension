# MusicDimension
A personal creation in order to play music from Spotify via a Raspberry Pi. a practical and solution and also for fun and curiosity. 

Steps in order to get things started: 
- install Raspberry Pi OS via Raspberry Pi Imager
![image](https://github.com/user-attachments/assets/0caa167a-4c3f-4eee-9cff-778317b3828f)

- connect via SSH and run `sudo bash install.sh`; this installs MusicDimension, Raspotify (including librespot), audio/USB dependencies, and boot services
- create app in [spotify developer dashboard](https://developer.spotify.com/dashboard)

- configure Spotify and the receiver through the web manager's **Spotify settings** tab
- pair the receiver once in the Spotify phone/desktop app on the same LAN; receiver credentials are retained across reboots
- connect Lego dimensions portal
- start musical dimension

## Raspberry Pi 3 Model B

The Pi 3 Model B can run either 32-bit ARM (`linux-arm`) or 64-bit ARM (`linux-arm64`). Raspberry Pi OS 64-bit is recommended; the application cross-publishes successfully for both targets. The deployment is self-contained, but VLC and libusb must be installed from Raspberry Pi OS because this project does not bundle Linux VLC libraries.

To install the latest published Pi build and enable its daemon at boot, copy `install.sh` to the Pi and run `sudo bash install.sh`. It detects `arm64` versus `armhf`, installs native packages and Raspotify from its signed upstream APT repository, preserves existing config/media and receiver credentials on reinstall, and enables the systemd services. Raspotify already includes librespot; do not install a second copy. If `librespot.service` is running or enabled, the installer stops with instructions to disable that conflicting service first. ARMv6 Pi 1/original Pi Zero are not supported. The current Raspotify package targets current Debian Stable; older Raspberry Pi OS versions may need an OS upgrade if APT reports incompatible dependencies.

To install a specific release, pass its exact GitHub tag, for example `sudo bash install.sh v1.2.3`. Use a release that contains the receiver integration files; the installer rejects older archives missing these files. Published builds contain their matching self-contained .NET runtime. If no release exists yet, the script downloads the .NET 10 SDK temporarily and builds from source on 64-bit Pi OS; 32-bit ARM requires a published `linux-arm` release asset.

### Publish

Run one command on the development machine, choosing the RID that matches `uname -m` on the Pi (`aarch64` means `linux-arm64`; `armv7l` means `linux-arm`):

```sh
dotnet publish MusicDimension.csproj -c Release -r linux-arm64 --self-contained true -o publish
# Or, for 32-bit Raspberry Pi OS:
dotnet publish MusicDimension.csproj -c Release -r linux-arm --self-contained true -o publish
```

Copy the `publish` directory to the Pi, for example to `/tmp/musicdimension-publish`.

### Install And Start At Boot

For a locally published build, copy the matching installer to the Pi and use the same integrated installation process:

```sh
sudo env MUSICDIMENSION_PUBLISH_DIR=/tmp/musicdimension-publish bash install.sh
```

The unit runs as the unprivileged `musicdimension` user, restarts on failure, and starts automatically at boot. It needs write access to `/opt/musicdimension/config` and `/opt/musicdimension/media` for tag edits and uploads. Check its state and logs with:

```sh
systemctl status musicdimension
journalctl -u musicdimension -f
```

The systemd service selects VLC's ALSA output directly so it does not depend on a logged-in PulseAudio session. By default it uses the Pi's headphone output (`plughw:CARD=Headphones,DEV=0`); change `MUSICDIMENSION_ALSA_DEVICE` in `musicdimension.service` to `plughw:CARD=vc4hdmi,DEV=0` to use HDMI instead. After changing the unit, run `sudo systemctl daemon-reload` and `sudo systemctl restart musicdimension`. It listens on all Pi network interfaces at port `5080`; from another device on the same trusted LAN, open `http://<pi-ip-address>:5080` (find the address with `hostname -I`). The web manager currently has no login, so do not forward this port through your router or expose it to the public internet. For a loopback-only listener, remove the `MUSICDIMENSION_WEB_URL` line from the service file.

The LEGO Dimensions Toy Pad rule uses vendor/product `0e6f:0241` and grants access to the `plugdev` group. Replug the portal or reboot after installing it. This rule follows the identifier documented by the [LegoDimensions Linux driver instructions](https://github.com/Ellerbach/LegoDimensions/blob/main/linux-driver-rules/99-lego-dimensions.rules).

In **Playback controls → Upload limit**, set the maximum size for each local media or M3U/M3U8 upload. The default remains **512 MiB** for existing installs. Supported values are **1–4096 MiB**; the web UI skips larger files before upload, and the server independently rejects them. This is a per-file limit, not a total library quota, so consider the Pi's available storage before raising it. Existing media files are unaffected.

### Playback Control Tags

In **Playback controls**, scan or enter a different UID for each action, then click **Save controls**. Leave a field blank to remove its assignment. Forward/rewind, **Volume up**, **Volume down**, and **Toggle shuffle** work with both local and Spotify playback. Volume changes by 10 percentage points per presentation and is limited to 0–100. Spotify volume control requires the configured device to expose its current playback volume; another active device is never controlled instead.

Shuffle is a toggle. For local queues it randomizes the unplayed tracks without interrupting the current track or repeating already played entries; turning it off restores the remaining tracks' saved order. If local shuffle is still enabled when another tag starts, its queue starts shuffled. Spotify shuffle toggles the configured device's current shuffle state. Control tags can also operate on a paused music tag without losing its resume state. Shuffle state is a playback setting, not a rewrite of the tag's saved track list.

### Spotify Settings And Login

#### Managed Pi Receiver

The installer automatically configures Raspotify to start at boot, enables LAN discovery and persistent credential caching, and disables audio-file caching to reduce SD-card writes. Credentials stay in `/var/lib/raspotify`, separate from the application's Web API tokens. Existing `/etc/raspotify/conf` is preserved; a managed drop-in overrides only the name, ALSA backend/device, bitrate and cache locations, and ensures discovery/credential caching are enabled. The installer uses your configured Spotify device name or assigns `Music Dimension (<hostname>)` if it is blank.

In **Spotify settings**, the **Pi Spotify Connect receiver** controls let you enable/disable Raspotify, choose an ALSA output (default `plughw:CARD=Headphones,DEV=0`), and select 96/160/320 kbps. **Device name** is shared with the receiver so playback targets the same name it advertises. Save settings to apply; changes restart only Raspotify and may interrupt Spotify playback. A root-owned, constrained systemd helper applies validated settings within 30 seconds; the web application does not run as root or have sudo access. The status refresh shows the running state, last applied settings, and whether receiver credentials are cached. Unchanged settings do not restart playback. Disabling app Spotify support is separate from disabling the receiver.

Pair once from Spotify on a phone/desktop on the same LAN, using the same Premium account as the website's OAuth login. Select the configured Pi receiver in Spotify's device picker; after pairing, refresh receiver status and confirm credentials are cached. On later boots those credentials are reused. First consent/pairing cannot be silently provisioned from the website's client ID, secret, or Web API refresh token. Password authentication is not supported by current librespot. Account revocation or Spotify protocol changes can still require pairing again. Do not copy credentials into the web form.

To inspect ALSA outputs, run `aplay -L` on the Pi. `default` or `plughw:CARD=vc4hdmi,DEV=0` may be appropriate for your setup. This setting controls Raspotify only; VLC's local-file output is configured separately in `musicdimension.service`. Some ALSA devices permit only one player at a time.

Receiver management is available only after running the updated Pi installer. Keep the web manager on a trusted LAN or SSH tunnel: anyone with access can change the receiver configuration. The helper is installed outside the app-writable directories and can change only its fixed receiver configuration and service. It never accepts arbitrary commands, shell snippets, file paths, or credentials from the browser.

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

In **Tags & music**, choose **Spotify** as the playback source, then **Search Spotify**. Selecting a result adds it to the tag's playback queue; if the tag name is blank, it is filled from the result too. After adding a track the dialog stays open, and the search field is focused with all text selected so you can type the next search immediately. Close the dialog when finished. Repeat to add up to 100 individual Spotify tracks in your chosen order. Album/playlist selection still closes the dialog. For manual entry, choose the type, enter its ID, and click **Add Spotify item**. Save the tag to keep its ordered queue. Existing single-track tags remain editable.

Both local and Spotify queues support drag-and-drop using the grip beside each track, with mouse or touch. Drop markers show the insertion point, and the list scrolls when dragging near its top or bottom. Escape cancels a drag. The up/down buttons remain available for keyboard reordering, and the remove button deletes an item from the tag's queue. Save changes to persist the order; Spotify receives the complete ordered track list when the tag is played.

An album or playlist is still used as a single selection, not mixed with individual tracks or other contexts. Selecting one asks before replacing an existing queue. Manual type/ID entry and the Spotify links in search results remain available. Playback and search require the configured Spotify account and receiver as described below.

Search requires enabled Spotify support and an existing OAuth login, but does not require the Pi's playback device to be active. It uses the configured Spotify account on the server and returns up to ten results per submitted search. Spotify developer-app/account restrictions can limit search, particularly playlist availability; when search is unavailable or rate limited, the dialog reports the error and manual ID entry still works. No Spotify credentials are sent to the browser.

### Spotify After A Pi Reboot

There are two separate logins: MusicDimension's Web API OAuth tokens and the librespot/raspotify Spotify Connect receiver's own account session. OAuth tokens do not authenticate the receiver. The application now retries device discovery briefly, explicitly transfers playback to an inactive configured device before play/resume, and targets that device for all playback controls. It never falls back to playing on your phone if the Pi is missing. Startup only checks the Web API connection; it does not take playback away from another device.

If the Pi still only works after selecting it on your phone, check the receiver immediately after reboot, before opening the phone app:

```sh
systemctl status raspotify musicdimension-receiver.path musicdimension-receiver.timer
journalctl -b -u raspotify --no-pager
journalctl -b -u musicdimension-receiver.service --no-pager
systemctl cat raspotify
```

Ensure Raspotify can reach Spotify and the phone can reach the Pi over LAN discovery (mDNS UDP 5353; do not open it to the internet). The installer does not change firewall rules. A receiver that has not been paired may only appear after phone selection. Credential caching is configured by the installer, but initial account pairing still requires the Spotify app. Spotify Web API playback also requires Premium and the appropriate app/account access. Raspotify is a third-party receiver, so installation cannot guarantee compatibility with future Spotify protocol changes; upstream currently documents possible Audio Key errors. Check the [Raspotify troubleshooting guide](https://github.com/dtcooper/raspotify/wiki/Troubleshooting) if the receiver starts but cannot play.

Receiver helper checks: `python3 -m unittest discover -s tests -p test_receiver_configuration.py -v`. Linux additionally checks that a symlinked configuration is rejected. Pi acceptance checks still required: run the installer, pair once, confirm cached credentials, reboot without opening the phone app, scan a Spotify tag, and verify audio and device visibility. Verify output changes and receiver disable/enable from the website.

Cross-publishing validates the managed application and package restore for both ARM RIDs. Actual portal USB access and audio output still require a test on the Pi 3 hardware and its installed Raspberry Pi OS/VLC packages.
  
