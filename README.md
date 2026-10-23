# FPS Hub RTX

**Clickys Gaming Optimizer · Project FPS Hub RTX** — a Windows 10/11 desktop app that finds the games installed on your PC, launches them from one library, and measures **real** FPS and system performance while you play.

- **SCAN GAMES** — reads Steam, Epic Games, Xbox app / Microsoft Store, GOG, Ubisoft Connect, EA app, Battle.net, Riot Client, Minecraft Launcher and Roblox installs, plus folders you choose on any SSD, HDD or external drive. Never scans whole drives, never launches or modifies anything while scanning.
- **Library & launcher** — cover art, search, filters (launcher, drive, collection, favourites), sorting, collections, custom names/arguments/executables/art, locate moved installs, open install folder or launcher page, verify files through the official launcher. Games are launched the way their store requires (steam://, Epic, Riot Client, Battle.net, Xbox package activation…) — DRM, sign-in and anti-cheat are never bypassed.
- **Real FPS monitoring** — Intel **PresentMon** frame-presentation telemetry (external, no injection): current / average / min / max FPS, 1% and 0.1% lows (when enough frames exist), frame time, frame-time graph, stability and stutter count. If a game can't be measured it says **“FPS unavailable for this game”** with the reason — it never shows an estimated or fixed number.
- **In-game overlay** — transparent, click-through, always-on-top window (FPS-only, compact, full, graph layouts), movable, hotkey toggle, custom colours and size.
- **Performance dashboard** — CPU, GPU (Task-Manager-equivalent counters), GPU memory, RAM, game process memory, disk, network, drive space, foreground game, CPU temperature (ACPI thermal zone, when exposed) and NVIDIA GPU temperature (NVML). Unavailable metrics are labelled as such.
- **Benchmarks** — record runs, compare two runs with explicit comparability warnings, export CSV / JSON / text summaries.
- **Game sessions** — playtime today / week / month / all time, sessions, most played, per-game FPS history; export or delete your history.
- **Settings** — dark/light theme, accent colour, overlay, hotkeys, startup and tray behaviour, scan scope, FPS & sampling options, notifications (per type + thresholds), privacy, backup/restore, health check, diagnostics report, update feed, reset.

Everything is stored locally in `%LOCALAPPDATA%\FPSHubRTX` (or `Data\` next to the exe in the portable build).

## Deliverables

| File | What it is |
|---|---|
| `FPSHubRTX.exe` | The application — one self-contained file, no .NET install needed |
| `FPSHubRTX-Setup.exe` | Inno Setup installer: choose folder, optional Start Menu / desktop shortcuts, per-user (no admin) or all-users install, upgrades in place, uninstall from Windows Settings |
| `FPSHubRTX-Portable-<ver>.zip` | Portable build (keeps data beside the exe) |

These are produced by `build\build.ps1` on Windows or by the included GitHub Actions workflow — see **[docs/BUILD.md](docs/BUILD.md)**.

## Quick start (building)

```powershell
# Windows 10/11 x64, .NET 10 SDK, Inno Setup 6
git clone <this repo> FPSHubRTX
cd FPSHubRTX
powershell -ExecutionPolicy Bypass -File build\build.ps1 -Version 1.0.0
# → artifacts\FPSHubRTX.exe, artifacts\FPSHubRTX-Setup.exe, artifacts\FPSHubRTX-Portable-1.0.0.zip
```

Or push to GitHub: `.github/workflows/build.yml` builds, tests, smoke-tests the installer on `windows-latest` and uploads the three files as an artifact.

## Project layout

```
src/FPSHubRTX.Core         Models, persistence, launcher scanners, launch resolution, process detection,
                           PresentMon capture + frame statistics, system monitor (PDH/NVML), sessions,
                           benchmarks, statistics, export, diagnostics. No NuGet dependencies.
src/FPSHubRTX.ViewModels   View models for every page + AppHost (composition root) + headless SelfTest.
src/FPSHubRTX.App          WPF app (FPSHubRTX.exe): views, theme, overlay window, tray, hotkeys, dialogs.
tests/FPSHubRTX.Core.Tests xUnit tests: scanners, persistence, launch resolution, statistics, errors…
installer/FPSHubRTX.iss    Inno Setup script → FPSHubRTX-Setup.exe
build/                     build.ps1, fetch-presentmon.ps1, smoke-test.ps1
docs/                      BUILD, TROUBLESHOOTING, FPS-TELEMETRY, DEPENDENCIES, TESTING
```

## Honest notes

- **FPS needs permission.** PresentMon reads Windows ETW presentation events, which requires membership of the built-in *Performance Log Users* group or administrator rights. FPS Hub runs as a normal user; the FPS page has a one-time *“Allow FPS measuring without admin”* button (one UAC prompt, then sign out/in) and an explicit *“Capture with admin rights”* option. See [docs/FPS-TELEMETRY.md](docs/FPS-TELEMETRY.md).
- **Overlay limits.** The overlay is a normal top-most window, so it appears over windowed and borderless games; exclusive-fullscreen games can cover it. Use borderless mode, a second monitor, or the tray's Performance panel.
- **Launcher coverage.** Each launcher's on-disk format is documented in the scanner source. Battle.net product codes are known for major Blizzard titles only; other Blizzard games launch through their executable (which opens Battle.net). Microsoft Store detection covers Xbox/GDK games (XboxGames folders), Minecraft and Roblox — not every Store app.
- **Temperatures.** Windows has no documented user-mode CPU temperature API; FPS Hub shows the ACPI thermal zone where the PC exposes one, otherwise “Unavailable”. GPU temperature is read for NVIDIA GPUs via NVML; AMD/Intel GPU temperatures show “Unavailable”.
- “RTX” is NVIDIA's trademark; the app works with any GPU and isn't affiliated with NVIDIA or any launcher vendor.

## Further reading

- [docs/BUILD.md](docs/BUILD.md) — exact build and packaging commands
- [docs/TESTING.md](docs/TESTING.md) — automated tests, smoke test and the manual release checklist
- [docs/FPS-TELEMETRY.md](docs/FPS-TELEMETRY.md) — how FPS is measured, API compatibility, metric definitions
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)
- [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
