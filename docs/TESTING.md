# Testing

## 1. Automated tests (`tests/FPSHubRTX.Core.Tests`, xUnit)

```powershell
dotnet test tests\FPSHubRTX.Core.Tests\FPSHubRTX.Core.Tests.csproj
```

115 tests, platform-neutral (fixture folders + in-memory registry), covering:

| Area | Examples |
|---|---|
| Scanner | Steam (multi-library, old/new `libraryfolders.vdf`, tools skipped, incomplete installs, cover art, path-traversal rejected), Epic (DLC/add-ons skipped, broken manifests), GOG (registry + play tasks, DLC skipped), Ubisoft, EA (`installerdata.xml`, DTD rejected), Battle.net product codes, Riot (YAML + client), Xbox (`.GamingRoot`, `MicrosoftGame.config`, package activation), Minecraft, Roblox, custom/standalone folders (containers, exclusions, removable drives, missing folders), executable selection, de-duplication, cancellation, a crashing scanner not stopping the scan |
| Library persistence | round-trip, rescans keep customisations, *missing* only after a completed launcher scan, nothing deleted, manual add/remove/relocate never touch files, corrupt file → backup restore, backup/restore zip, health checks |
| Launch-method resolution | preference order, custom exe, working directory and arguments, Steam argument URI, URI scheme allow-list, AUMID validation, newline injection, actionable errors, Win32 error mapping incl. elevation |
| Process detection | start/child-process/exit, launch timeout, unrelated processes and drive roots ignored, name hints only while launching |
| FPS statistics | exact averages (time-weighted), min/max, 1% / 0.1% lows and their frame thresholds, pauses excluded, histogram ≡ exact, PresentMon CSV v1/v2 parsing, swap-chain filtering, *no frames → unavailable*, stale stream, access denied, PresentMon missing |
| Statistics | midnight/week/month splitting, averages, FPS history, formatting |
| Export | CSV escaping + formula-injection guard, benchmark CSV/JSON/report, comparability warnings |
| Error handling / misc | settings validation & unknown fields, hotkey parsing, log sanitising, path validation, notification preferences and threshold cooldown, full session flow (launch → measure → exit → saved), app-close finalisation |

## 2. Headless end-to-end self-test

```powershell
FPSHubRTX.exe --self-test --launch "Some Game" --report C:\temp\report.json
```

Scans, verifies the library re-loads from disk, resolves every launch method, samples system metrics, launches the named game, waits for its process to start and exit, and checks the session was saved with an honest FPS status. Exit code 0 = success.

## 3. Installer smoke test

`build\smoke-test.ps1 -Setup artifacts\FPSHubRTX-Setup.exe` (also run by CI): silent install with shortcuts → stand-in game → self-test with a real launch → UI starts and stays up → reinstall (upgrade) keeps data → silent uninstall removes the app and leaves game files untouched.

## 4. Manual release checklist (needs a real gaming PC)

Record results in the release notes. Only tick what you actually did.

| # | Check | How |
|---|---|---|
| 1 | Installer runs | Run `FPSHubRTX-Setup.exe`, pick a custom folder, both shortcuts |
| 2 | App starts | Loading screen → Home page, tray icon present |
| 3 | SCAN GAMES detects games | Press SCAN GAMES with at least two launchers installed; compare with what's installed |
| 4 | Library | Games appear in My Games with covers/status/drive; search, filters, sort, collections work |
| 5 | PLAY | Launch one Steam, one Epic/other-launcher and one standalone game; card shows RUNNING |
| 6 | FPS telemetry | With a DX11/DX12 game running, FPS Monitor shows *Measuring* and plausible values; compare with the game's own FPS counter or PresentMon |
| 7 | Unsupported reported honestly | Measure a game streamed from the cloud or a minimised game → *No frames / unavailable*, no numbers |
| 8 | Statistics persist | Close and reopen FPS Hub; Game Sessions shows the sessions, playtime and FPS history |
| 9 | Overlay | Windowed, borderless and (if available) exclusive-fullscreen game; toggle with Ctrl+Shift+F10, move with Ctrl+Shift+F11 |
| 10 | Install / uninstall | Upgrade over an older build keeps data; uninstall from Windows Settings removes the app, games untouched |

## Verification status of this source drop

| What | Where it was verified |
|---|---|
| `FPSHubRTX.Core` + `FPSHubRTX.ViewModels` compile | Linux, .NET SDK 10.0.113 — 0 errors, 0 warnings |
| 115 automated tests | Executed on Linux with an offline xUnit-compatible runner (NuGet was unavailable there): 115 passed |
| Headless self-test incl. a real launch | Linux, with a stand-in "game" executable: scan found it, library persisted, launch → process detected → exit → session saved, FPS honestly reported unavailable (no PresentMon on Linux) |
| XAML bindings and resource keys | Static check of all 16 XAML files against the compiled view models (every binding path, two-way-to-read-only bindings, resource keys, event handlers) |
| WPF app compile, `FPSHubRTX.exe`, `FPSHubRTX-Setup.exe`, installer smoke test, PresentMon capture, overlay, checklist above | **Not yet run** — needs Windows: run `build\build.ps1` + `build\smoke-test.ps1` or the GitHub Actions workflow |
