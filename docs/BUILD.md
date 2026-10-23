# Building FPS Hub RTX

## Requirements (Windows build machine)

| Tool | Version | Get it |
|---|---|---|
| Windows | 10 (1809+) or 11, x64 | — |
| .NET SDK | 10.0.x | `winget install Microsoft.DotNet.SDK.10` |
| Inno Setup | 6.x | `winget install JRSoftware.InnoSetup` |
| PowerShell | 5.1+ (built in) or 7 | — |
| Internet | NuGet restore + PresentMon download | — |

The end-user PC needs **nothing extra**: `FPSHubRTX.exe` is published self-contained (the .NET runtime is inside the exe).

## One command

```powershell
cd FPSHubRTX
powershell -ExecutionPolicy Bypass -File build\build.ps1 -Version 1.0.0
```

It will:

1. Download PresentMon's console exe from `github.com/GameTechDev/PresentMon/releases` into `third_party\PresentMon\` (signature checked, SHA-256 recorded). Pin a version with `-PresentMonVersion 2.4.1`.
2. Restore NuGet packages.
3. Run the automated tests (`dotnet test`) — packaging stops if any fail.
4. Publish `FPSHubRTX.exe` (win-x64, self-contained, single file, compressed) with PresentMon in `tools\PresentMon\`.
5. Create `FPSHubRTX-Portable-<version>.zip` (contains `portable.txt`, so data stays beside the exe).
6. Compile `installer\FPSHubRTX.iss` with Inno Setup → `FPSHubRTX-Setup.exe`.
7. Write `artifacts\SHA256SUMS.txt`.

Output:

```
artifacts\FPSHubRTX.exe
artifacts\FPSHubRTX-Setup.exe
artifacts\FPSHubRTX-Portable-1.0.0.zip
artifacts\SHA256SUMS.txt
artifacts\test-results\test-results.trx
```

Switches: `-SkipTests`, `-SkipInstaller`, `-SkipPresentMon`, `-Configuration Debug`.

Then run the end-to-end smoke test against the installer (installs into a temp folder with an isolated data folder, scans/launches a stand-in game, upgrades, uninstalls):

```powershell
powershell -ExecutionPolicy Bypass -File build\smoke-test.ps1 -Setup artifacts\FPSHubRTX-Setup.exe
```

## Manual commands (equivalent)

```powershell
powershell -File build\fetch-presentmon.ps1
dotnet test tests\FPSHubRTX.Core.Tests\FPSHubRTX.Core.Tests.csproj -c Release
dotnet publish src\FPSHubRTX.App\FPSHubRTX.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:Version=1.0.0 -o artifacts\publish
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0 /DSourceDir=..\artifacts\publish /O..\artifacts installer\FPSHubRTX.iss
```

## Developing

- Open `FPSHubRTX.sln` in Visual Studio 2022 17.14+ / 2026 (with the .NET desktop workload) or Rider, set **FPSHubRTX.App** as the start-up project, F5.
- `FPSHubRTX.Core` and `FPSHubRTX.ViewModels` have no Windows-only build dependencies; `dotnet build` / `dotnet test` work on Linux and macOS too (the scanners are tested with fixture folders and an in-memory registry). The WPF project needs Windows to run (it can be *compiled* elsewhere with `EnableWindowsTargeting`, which is set).
- Headless check on any Windows PC: `FPSHubRTX.exe --self-test [--launch "Game Name"] [--report C:\path\report.json]`.
- Set `FPSHUBRTX_DATA_DIR=C:\some\folder` to run against a throw-away data folder.

## GitHub Actions

`.github/workflows/build.yml` runs on `windows-latest`: installs Inno Setup, runs `build.ps1`, runs `smoke-test.ps1`, and uploads `FPSHubRTX.exe`, `FPSHubRTX-Setup.exe`, the portable ZIP, checksums and test results. Trigger it by pushing, tagging `v1.2.3` (sets the version), or *Actions → build → Run workflow*.

## Code signing (recommended for distribution)

Unsigned executables trigger SmartScreen warnings. If you have a code-signing certificate:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a artifacts\FPSHubRTX.exe
# sign the setup after building it (or add a SignTool= line to the [Setup] section of FPSHubRTX.iss)
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a artifacts\FPSHubRTX-Setup.exe
```
