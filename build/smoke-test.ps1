<#
.SYNOPSIS
  End-to-end smoke test of FPSHubRTX-Setup.exe on Windows (used by CI; safe to run locally).

  1. Silent per-user install into a temp folder (+ shortcuts)
  2. Creates a stand-in "game" (a renamed copy of PING.EXE that runs ~6 s) in a test games folder
  3. FPSHubRTX.exe --self-test: scan → library persisted → PLAY the test game → process detected →
     game exits → session saved → FPS availability recorded honestly
  4. Starts the real UI (minimised) and checks it stays up and logs a clean start
  5. Re-installs over itself (upgrade path) and checks data survived
  6. Silent uninstall and checks the test game files were not touched

  The app runs against an isolated data folder (FPSHUBRTX_DATA_DIR), so your real library is untouched.
#>
param(
    [Parameter(Mandatory = $true)][string]$Setup
)

$ErrorActionPreference = "Stop"
$Setup = (Resolve-Path $Setup).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("fpshub-smoke-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$install = Join-Path $work "install"
$data = Join-Path $work "data"
$games = Join-Path $work "Games"
New-Item -ItemType Directory -Force -Path $install, $data, $games | Out-Null
$env:FPSHUBRTX_DATA_DIR = $data
$failures = New-Object System.Collections.Generic.List[string]

function Check($condition, $message) {
    if ($condition) { Write-Host "  [PASS] $message" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $message" -ForegroundColor Red; $failures.Add($message) }
}

Write-Host "== 1. Install"
$p = Start-Process -FilePath $Setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/CURRENTUSER", "/NORESTART", "/DIR=`"$install`"", "/TASKS=startmenuicon,desktopicon", "/LOG=`"$work\install.log`"" -Wait -PassThru
Check ($p.ExitCode -eq 0) "Installer exit code 0 (was $($p.ExitCode))"
$exe = Join-Path $install "FPSHubRTX.exe"
Check (Test-Path $exe) "FPSHubRTX.exe installed"
Check (Test-Path (Join-Path $install "unins000.exe")) "Uninstaller registered"
$startMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "FPS Hub RTX.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath("Desktop")) "FPS Hub RTX.lnk"
Check (Test-Path $startMenu) "Start Menu shortcut created"
Check (Test-Path $desktop) "Desktop shortcut created"
$uninstallKey = Get-ChildItem "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall" | Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -eq "FPS Hub RTX" }
Check ($null -ne $uninstallKey) "Apps & features entry registered"

Write-Host "== 2. Test game"
$testGame = Join-Path $games "Test Game"
New-Item -ItemType Directory -Force -Path $testGame | Out-Null
Copy-Item "$env:WINDIR\System32\PING.EXE" (Join-Path $testGame "TestGame.exe")
Set-Content (Join-Path $testGame "UnityPlayer.dll") "marker" -Encoding ASCII
$settings = @{ Scan = @{ CustomFolders = @($games); ScanCommonGameFolders = $false } } | ConvertTo-Json -Depth 4
Set-Content (Join-Path $data "settings.json") $settings -Encoding UTF8
$gameHash = (Get-FileHash (Join-Path $testGame "TestGame.exe")).Hash

Write-Host "== 3. Self-test (scan, persist, launch, detect, session)"
$report = Join-Path $work "self-test.json"
$p = Start-Process -FilePath $exe -ArgumentList "--self-test", "--launch", "`"Test Game`"", "--launch-args", "`"-n 7 127.0.0.1`"", "--timeout", "60", "--report", "`"$report`"" -Wait -PassThru
Check ($p.ExitCode -eq 0) "Self-test exit code 0 (was $($p.ExitCode))"
if (Test-Path $report) {
    $r = Get-Content $report -Raw | ConvertFrom-Json
    Get-Content $report -Raw | Write-Host
    Check ($r.gamesFound -ge 1) "Scan found the test game ($($r.gamesFound) found)"
    Check ($r.libraryCountAfterReload -ge 1) "Library persisted"
    Check ($r.launch.Detected -eq $true) "Launched game process detected"
    Check ($r.launch.SessionSaved -eq $true) "Session saved after the game exited"
    Check ($r.launch.FpsAvailability -ne "Measured" -or $r.launch.AverageFps -gt 0) "FPS is either genuinely measured or reported unavailable ($($r.launch.FpsAvailability))"
    Check ($r.presentMon -ne "NOT FOUND") "PresentMon bundled"
} else {
    Check $false "Self-test report written"
}
Check (Test-Path (Join-Path $data "sessions.json")) "sessions.json written"

Write-Host "== 4. UI start"
$ui = Start-Process -FilePath $exe -ArgumentList "--minimized" -PassThru
Start-Sleep -Seconds 10
$ui.Refresh()
Check (-not $ui.HasExited) "App UI is still running 10 s after start"
$log = Join-Path $data "logs\fpshub.log"
Check ((Test-Path $log) -and ((Get-Content $log -Raw) -match "started")) "Log shows a clean start"
if (-not $ui.HasExited) { Stop-Process -Id $ui.Id -Force }
Start-Sleep -Seconds 2

Write-Host "== 5. Upgrade (reinstall over existing)"
$p = Start-Process -FilePath $Setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/CURRENTUSER", "/NORESTART", "/DIR=`"$install`"" -Wait -PassThru
Check ($p.ExitCode -eq 0) "Upgrade install exit code 0"
Check (Test-Path (Join-Path $data "library.json")) "Library kept across upgrade"
Check (Test-Path (Join-Path $data "sessions.json")) "Statistics kept across upgrade"

Write-Host "== 6. Uninstall"
$p = Start-Process -FilePath (Join-Path $install "unins000.exe") -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -Wait -PassThru
Start-Sleep -Seconds 3
Check ($p.ExitCode -eq 0) "Uninstaller exit code 0"
Check (-not (Test-Path $exe)) "FPSHubRTX.exe removed"
Check (-not (Test-Path $startMenu)) "Start Menu shortcut removed"
Check ((Test-Path (Join-Path $testGame "TestGame.exe")) -and ((Get-FileHash (Join-Path $testGame "TestGame.exe")).Hash -eq $gameHash)) "Test game files untouched"

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "Smoke test FAILED ($($failures.Count)):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}
Write-Host "Smoke test passed." -ForegroundColor Green
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
