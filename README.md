# MusicDimension
A personal creation in order to play music from Spotify via a Raspberry Pi. Main reason is fun and curiosity.
Also now with improved functionality thanks to AI.

<img width="1254" height="1254" alt="image" src="https://github.com/user-attachments/assets/c15ef1e7-773c-48b2-94f7-30dc540f4147" />

## Installation
I am using my Raspberry Pi 3 Model B. No other model has been tested.

- install Raspberry Pi OS (Trixie) via Raspberry Pi Imager
- connect via SSH and run `sudo bash install.sh`; this installs MusicDimension, official Spotify Soloist, a private audio server, USB dependencies, and boot services
- create app in [spotify developer dashboard](https://developer.spotify.com/dashboard)

- configure Spotify and the receiver through the web manager's **Spotify settings** tab
- generate your personal [Soloist API key](https://developer.spotify.com/dashboard/soloist), save it in Spotify settings, and pair the receiver once in the Spotify app on the same (W)LAN
- connect Lego dimensions portal
- start musical dimension

## Additional Notes

The Pi 3 Model B can run either 32-bit ARM (`linux-arm`) or 64-bit ARM (`linux-arm64`). Raspberry Pi OS 64-bit is recommended; the application cross-publishes successfully for both targets. The deployment is self-contained, but VLC and libusb must be installed from Raspberry Pi OS because this project does not bundle Linux VLC libraries.

To install the latest published Pi build, copy the matching `install.sh` to the Pi and run `sudo bash install.sh`. It detects `arm64` versus `armhf`, downloads Soloist directly from Spotify's official CDN, installs a private PulseAudio server and native dependencies, preserves existing config/media, and enables boot services. ARMv6 Pi 1/original Pi Zero are not supported. Use current Raspberry Pi OS; Spotify primarily tests ARM64 on Debian Trixie-based Pi OS (glibc 2.41). An older OS may not run the downloaded binary; the installer checks `soloist --version` before accepting it. It does not upgrade the OS automatically.

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

Local VLC and Spotify audio share the private PulseAudio server. The Pi headphone jack (`plughw:CARD=Headphones,DEV=0`) is the default output; the ALSA device can be changed in Spotify settings. In the tag editor, choose either the headphone jack or a paired Bluetooth speaker/headset as that tag's output. When the tag is read, the app reconnects its assigned Bluetooth device if needed, switches audio to it, and falls back to the headphone jack if it is unavailable. The app listens on all Pi network interfaces at port `5080`; from another device on the same trusted LAN, open `http://<pi-ip-address>:5080` (find the address with `hostname -I`). The web manager currently has no login, so do not forward this port through your router or expose it to the public internet. For a loopback-only listener, remove the `MUSICDIMENSION_WEB_URL` line from the service file.

The LEGO Dimensions Toy Pad rule uses vendor/product `0e6f:0241` and grants access to the `plugdev` group. Replug the portal or reboot after installing it. This rule follows the identifier documented by the [LegoDimensions Linux driver instructions](https://github.com/Ellerbach/LegoDimensions/blob/main/linux-driver-rules/99-lego-dimensions.rules).

In **Playback controls → Upload limit**, set the maximum size for each local media or M3U/M3U8 upload. The default remains **512 MiB** for existing installs. Supported values are **1–4096 MiB**; the web UI skips larger files before upload, and the server independently rejects them. This is a per-file limit, not a total library quota, so consider the Pi's available storage before raising it. Existing media files are unaffected.

### Playback Control Tags

In **Playback controls**, scan or enter a different UID for each action, then click **Save controls**. Leave a field blank to remove its assignment. Forward/rewind, **Volume up**, **Volume down**, and **Toggle shuffle** work with both local and Spotify playback. Volume changes by 10 percentage points per presentation and is limited to 0–100. Spotify volume control requires the configured device to expose its current playback volume; another active device is never controlled instead.

Shuffle is a toggle. For local queues it randomizes the unplayed tracks without interrupting the current track or repeating already played entries; turning it off restores the remaining tracks' saved order. If local shuffle is still enabled when another tag starts, its queue starts shuffled. Spotify shuffle toggles the configured device's current shuffle state. Control tags can also operate on a paused music tag without losing its resume state. Shuffle state is a playback setting, not a rewrite of the tag's saved track list.

To pair a Bluetooth speaker or headset, open **Spotify settings → Pair a Bluetooth audio device**, scan, select the device, and choose **Pair / connect**. The first pair should be done while the device is discoverable. After it is paired, edit a tag and choose it under **Sound output for this tag**. Choose **Pi headphone jack** on any tag to switch back. Devices requiring a PIN or interactive passkey are not supported by the automatic pairing agent; pair those through the Raspberry Pi OS Bluetooth settings first.

For audiobook-style tags, enable **Resume playback where I left off** while creating or editing the tag. Removing the tag saves its current track and position; reading it again resumes there, including within local M3U playlists and Spotify track queues, albums, and playlists. Positions are stored per tag in `config/tag-playback-state.json` and survive application restarts. If a saved queue changes, its old position is discarded.

### Spotify Settings And Login

#### Managed Soloist Receiver

Librespot/Raspotify `audio_key` failures followed by decryption warnings and decoder end-of-stream errors indicate that audio key acquisition failed before decoding. The decoder errors are downstream symptoms, not evidence that the NFC tag, OAuth refresh token, or ALSA configuration is wrong. The log alone cannot prove which encryption/protocol change caused the key rejection. This integration replaces that third-party audio engine with Spotify's official [Soloist](https://developer.spotify.com/documentation/soloist). Spotify's repository is [spotify/soloist](https://github.com/spotify/soloist); the old `spotify/spotify-soloist` URL is not the documented project.

The installer disables the old `raspotify.service` and `librespot.service` without deleting their packages, configuration, or caches. Those sessions cannot be imported into Soloist. It installs `musicdimension-soloist.service`, backed by a private `musicdimension-audio.service` running PulseAudio as the unprivileged application user. Soloist supports PipeWire/PulseAudio on Linux, not a direct ALSA backend. The private audio server routes to your selected ALSA device and does not require a graphical login. Its Unix socket is in an owner-only runtime directory; Soloist's unauthenticated WebSocket API listens only on `127.0.0.1:9876`, never on the LAN.

Generate your own key from the [Soloist API key dashboard](https://developer.spotify.com/dashboard/soloist), accepting Spotify's terms. The account creating the key needs Premium. In **Spotify settings**, enable the Soloist receiver, save the **Spotify Soloist API key**, and choose the device name and ALSA output. A blank key field preserves the existing key; **Clear saved Soloist API key** removes it and stops the receiver. The website never returns the saved key. The key is stored in `config/soloist-key.json` with owner-only permissions and is excluded from general config serialization and Git. Do not paste it into chat, screenshots, command history, or public logs. Soloist requires passing the key at runtime, so process arguments and local service diagnostics must also be treated as sensitive.

Select the Soloist receiver once from the Spotify phone/desktop app on the same LAN. Soloist stores its identity and Connect session in `/var/lib/musicdimension/soloist`, separate from `config/tokens.json` and the API key. Keep that data directory across reboots and updates. The website reads Soloist's documented `auth_state` event to report whether it is logged in; it does not guess based on a credential filename. The Web API OAuth flow remains necessary for catalog search and the existing track/album/playlist playback controls. A Soloist key does not replace Web API authorization, and Web API OAuth cannot create a Soloist key or initial pairing.

**Device name** remains shared between the app and receiver. Save changes to apply within 30 seconds; changing name, key, or output restarts the receiver and can interrupt Spotify playback. Unchanged settings do not restart it. Legacy `receiver.bitrate` remains readable for old configurations but is not applied: Soloist does not document a bitrate CLI option. Local VLC output remains configured separately. `aplay -L` lists available ALSA outputs; `default`, `plughw:CARD=Headphones,DEV=0`, or HDMI may be appropriate. Some ALSA devices cannot serve VLC and the audio server simultaneously; the private server suspends its idle sink after five seconds.

#### Automatic Soloist Updates

Every start/restart of the installed `musicdimension.service` checks Spotify's official architecture-specific archive. Soloist's own service also checks before starting. HTTP ETags avoid downloading unchanged archives when supported; otherwise binary hashes avoid replacing identical builds. A changed artifact is staged, checked for a safe regular executable entry, matching ELF architecture, and successful `--version`, then atomically replaces `/opt/musicdimension-soloist/soloist`. A running receiver is restarted when an application-start check installs a changed build. Data/session directories are never replaced. Downloads use HTTPS from `soloist-builds.spotifycdn.com`; hashes compare local artifacts and are not an independent vendor signature.

Soloist builds [expire after 90 days](https://developer.spotify.com/documentation/soloist/reference/downloads-and-updates); expired daemons exit with code `10`. Update checks retain an installed build that passes its version check if the network is unavailable, rather than deleting it. If no runnable build can be downloaded, Soloist stays unavailable and the website/local player can still start. Check the update status on the website and service logs; an offline or incompatible OS cannot be fixed by retrying the same download. A continuously running installation must still be restarted/updated before its build expires; checking on app startup is not a background download timer.

Updates run through root-owned fixed-path helpers, not website-supplied commands. These automatic checks apply to the installed Pi systemd services; running the .NET DLL manually on a desktop does not install an OS service or request administrator privileges. No Soloist binaries or archives are included in MusicDimension release assets or committed here, in accordance with Spotify's distribution instructions. Use the official downloads page for standalone/manual installations.

If an installation on Raspberry Pi OS **Bookworm** fails before Soloist starts, check `getconf GNU_LIBC_VERSION`. Bookworm normally supplies glibc 2.36; the official ARM64 archive inspected on 2026-11-13 references `GLIBC_2.38`. The updater now reports missing runtime versions/shared libraries, download HTTP errors, and execution failures separately, and preserves the explanation in receiver update status. A runtime-version error is not repaired by entering another API key or reinstalling .NET. Back up `config/`, media, and any Soloist session, then use a supported newer OS (such as current Trixie-based Pi OS), or a compatible build provided by Spotify. Never manually replace the system glibc or mix Debian releases just to satisfy the executable.

The installer now runs the Soloist download/execution preflight **before** stopping existing services or replacing application files. A failed preflight leaves that existing installation in place. These changes do not modify an already published v3.0.0 archive: use a newly published matching build/installer or the documented local-publish installation path to receive the updated helper and installer behavior.

Receiver management is available only after running the updated Pi installer. Keep the web manager on a trusted LAN or SSH tunnel: anyone with access can change the receiver configuration. The helper is installed outside the app-writable directories and can change only its fixed receiver configuration and service. It never accepts arbitrary commands, shell snippets, file paths, or credentials from the browser.

Next to **Device name**, click the refresh icon (**Find Spotify devices**) to load the exact names visible to your Spotify account. Choose your Pi from **Available Spotify devices**, then click **Save Spotify settings**. Active devices are marked; restricted devices are shown but cannot be selected. Loading devices never transfers playback or changes the saved name. Manual name entry remains available. Lookup requires Web API authorization; the Soloist receiver must be running and paired before Spotify can list it.

The web manager's **Spotify settings** section edits `enable`, `spotifyDeviceName`, `clientId`, and `clientSecret` in `config/config.json`. Saved changes apply immediately without restarting, including enabling Spotify after a local-only startup. Disabling app Spotify support prevents playback/control requests; use the separate receiver switch to stop Soloist. The Web API client secret is never returned: leave it blank to keep it, or select **Clear saved client secret** to remove it. PKCE does not need a client secret. All receiver/Spotify settings are available to anyone who can reach the unauthenticated web manager; use only a trusted LAN or SSH tunnel, never a public listener.

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

Soloist requires its API key and a stored Spotify Connect session, while MusicDimension's Web API OAuth tokens remain separate. The application retries device discovery briefly, transfers playback to the configured receiver before play/resume, and targets it for all playback controls. It never falls back to your phone if the Pi is missing.

If the Pi still only works after selecting it on your phone, check the receiver immediately after reboot, before opening the phone app:

```sh
systemctl status musicdimension-soloist musicdimension-audio musicdimension-receiver.timer
journalctl -b -u musicdimension-soloist --no-pager
journalctl -b -u musicdimension-audio --no-pager
journalctl -b -u musicdimension-receiver.service --no-pager
/opt/musicdimension-soloist/soloist --version
```

Ensure Soloist can reach Spotify and the phone can discover the Pi on the same LAN. The installer does not change firewall rules. A receiver without its initial pairing may only appear after phone selection. A revoked Soloist key must be replaced, and a lost/revoked Connect session requires pairing again. Do not post unredacted process arguments, logs, or data directories publicly. Native Soloist startup, OS-library compatibility, audio output, and account pairing still require on-Pi verification.

Management checks: `python3 -m unittest discover -s tests -p 'test_*.py' -v`. Linux additionally checks symlink rejection. Pi acceptance checks: install a matching new MusicDimension build, save your Soloist key, pair, verify login status, restart the app and confirm an update check, reboot without opening the phone app, and scan single/multi-track tags. Verify pause/resume, skip, volume, shuffle, output changes, local VLC playback, and receiver disable/enable. Test update failure offline and confirm existing session data remains intact. No live Spotify audio or native Linux service behavior is verified by the Windows test suite.

Cross-publishing validates the managed application and package restore for both ARM RIDs. Actual portal USB access and audio output still require a test on the Pi 3 hardware and its installed Raspberry Pi OS/VLC packages.
  
