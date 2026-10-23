# Troubleshooting

Start with **Settings → Library health & tools → Create diagnostic report**. It lists app/OS versions, PresentMon status, per-launcher counts, health issues and recent log lines, with user names, e-mail addresses and tokens removed (and game titles hidden unless you allow them). Logs: `%LOCALAPPDATA%\FPSHubRTX\logs\fpshub.log`.

## Scanning

| Symptom | What to do |
|---|---|
| A launcher shows *Not installed* | Its data wasn't found in the usual place. Make sure the launcher is installed for your Windows account and has been opened once. |
| A Steam library on another drive is missing | Steam lists libraries in `steamapps\libraryfolders.vdf`. Open Steam → Settings → Storage so it rewrites the file, then rescan. A disconnected drive is reported as a note in the scan summary. |
| An Xbox / Game Pass game is missing | FPS Hub reads `X:\.GamingRoot` and `XboxGames` folders. Games installed in the protected `WindowsApps` folder (older installs) aren't listed — change the install location in the Xbox app or add the game manually. |
| A standalone game isn't found | Add its parent folder (or the game's folder itself) under **Scan Games → Your game folders**. Whole drives aren't scanned on purpose. |
| A non-game program shows up | Remove it from the library (files aren't touched) and add its folder under *Excluded folders* so it isn't found again. |
| Duplicate entries | Settings → *Run health check* lists possible duplicates. Remove one entry; game files are never affected. |
| A game shows *Missing* | Its folder no longer exists (moved, uninstalled, or drive offline). Use **⋯ → Locate moved installation…**, reconnect the drive, or remove the entry. |

## Launching

| Symptom | What to do |
|---|---|
| *“The launcher for this game doesn't seem to be installed”* | The game's protocol (steam://, uplay://, …) isn't registered. Reinstall/repair that launcher. |
| Nothing happens / *“hasn't started yet”* | The launcher may be updating, asking you to sign in, or showing a dialog. Finish that; FPS Hub detects the game when its process starts. |
| Wrong program starts | Game settings → *Choose executable…*, and set *Launch method* to *Run executable directly* (only if the store allows it). |
| *“asks for administrator rights”* | Some games require elevation. FPS Hub only elevates when you confirm the prompt. |
| Launch arguments ignored | Most launchers (Epic, Ubisoft, Store) don't accept arguments through their links; set them in the launcher. Steam arguments are passed via `steam://run/<id>//args/` (Steam may ask to confirm). |
| Game crashed | The session is still saved with its exit code (*“possible crash”*). |

## FPS shows “unavailable”

1. **PresentMon not found** → reinstall FPS Hub, or Settings → FPS → *Browse…* to `PresentMon.exe` (download from github.com/GameTechDev/PresentMon/releases).
2. **Permission needed / access denied** → FPS page → *Allow FPS measuring without admin (one-time)*, approve the prompt, **sign out and back in**. Or use *Capture with admin rights* for this session.
3. **No frames** →
   - make sure the game is actually rendering (not minimised, not still on its launcher window);
   - if the game runs as a separate process (e.g. `…-Win64-Shipping.exe` under a different folder), set it with *Choose executable…*;
   - cloud-streamed games can't be measured locally.
4. Antivirus blocking PresentMon → allow `tools\PresentMon\PresentMon.exe` (it's signed by Intel).

See [FPS-TELEMETRY.md](FPS-TELEMETRY.md) for API compatibility.

## Overlay

| Symptom | What to do |
|---|---|
| Not visible over the game | Switch the game to borderless/windowed; exclusive fullscreen can cover top-most windows. |
| Can't move it | Press the edit hotkey (default **Ctrl+Shift+F11**), drag, press again to lock. |
| Hotkey does nothing | Another app owns that combination — Settings shows a message; choose another and press *Apply hotkeys*. |
| Shows N/A | Nothing is being measured yet (see FPS above). |

## Performance page

GPU temperature is read only for NVIDIA GPUs (NVML). CPU temperature appears only if the motherboard exposes an ACPI thermal zone. GPU usage requires WDDM 2.x drivers. Unavailable metrics are labelled — hover a tile for the reason.

## Data, upgrades, uninstall

- Data folder: `%LOCALAPPDATA%\FPSHubRTX` (portable build: `Data\` beside the exe). Upgrades never touch it.
- Corrupt `library.json`/`settings.json` is moved aside (`*.corrupt-<time>`) and the previous version (`*.bak`) is restored automatically.
- Settings → *Back up* / *Restore* zips library, settings, sessions, benchmarks and custom covers. Restore makes a safety backup first.
- Uninstall from **Windows Settings → Apps**. It asks whether to delete FPS Hub's own data (default: keep). Installed games are never affected.

## Startup problems

- *“could not create its data folder”* → check permissions on `%LOCALAPPDATA%`, or use the portable build.
- SmartScreen warning → the build isn't code-signed; see BUILD.md → Code signing.
- Run `FPSHubRTX.exe --self-test` from a terminal; it writes `%LOCALAPPDATA%\FPSHubRTX\reports\self-test.json` describing what works.
