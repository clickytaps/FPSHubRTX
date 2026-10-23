<#
.SYNOPSIS
  Builds FPS Hub RTX on Windows: tests, FPSHubRTX.exe (self-contained, single file),
  the portable ZIP and FPSHubRTX-Setup.exe.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\build.ps1
  powershell -ExecutionPolicy Bypass -File build\build.ps1 -Version 1.0.1 -SkipTests

.NOTES
  Requirements: Windows 10/11 x64, .NET 10 SDK, Inno Setup 6 (for the installer), internet access
  for NuGet packages and the PresentMon download.
#>
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$PresentMonVersion = "",
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$SkipPresentMon
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"

function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }

Step "Checking tools"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw ".NET SDK not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download" }
$sdks = & dotnet --list-sdks
if (-not ($sdks -match '^10\.')) { throw ".NET 10 SDK is required (found: $($sdks -join ', '))." }
Write-Host "dotnet: $((& dotnet --version))"

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

if (-not $SkipPresentMon) {
    Step "Fetching PresentMon"
    if (Test-Path (Join-Path $root "third_party\PresentMon\PresentMon.exe")) {
        Write-Host "Using existing third_party\PresentMon\PresentMon.exe (delete it to re-download)."
    } else {
        try {
            & (Join-Path $PSScriptRoot "fetch-presentmon.ps1") -Version $PresentMonVersion
        } catch {
            Write-Warning "PresentMon download failed: $($_.Exception.Message). Continuing without it (FPS will show 'PresentMon not found')."
        }
    }
}

Step "Restoring packages"
dotnet restore FPSHubRTX.sln -r win-x64
if ($LASTEXITCODE -ne 0) { throw "restore failed" }
dotnet restore tests\FPSHubRTX.Core.Tests\FPSHubRTX.Core.Tests.csproj
if ($LASTEXITCODE -ne 0) { throw "test restore failed" }

if (-not $SkipTests) {
    Step "Running automated tests"
    dotnet test tests\FPSHubRTX.Core.Tests\FPSHubRTX.Core.Tests.csproj -c $Configuration --logger "trx;LogFileName=test-results.trx" --results-directory (Join-Path $artifacts "test-results")
    if ($LASTEXITCODE -ne 0) { throw "Tests failed — not packaging." }
}

Step "Publishing FPSHubRTX.exe (self-contained, single file, win-x64)"
dotnet publish src\FPSHubRTX.App\FPSHubRTX.App.csproj -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Copy-Item (Join-Path $publish "FPSHubRTX.exe") (Join-Path $artifacts "FPSHubRTX.exe")
if (-not (Test-Path (Join-Path $publish "tools\PresentMon\PresentMon.exe"))) {
    Write-Warning "PresentMon is not bundled — FPS measurement will report 'PresentMon not found' until it is installed. Run build\fetch-presentmon.ps1."
}

Step "Creating portable ZIP"
$portable = Join-Path $artifacts "portable\FPSHubRTX"
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Copy-Item (Join-Path $publish "*") $portable -Recurse
Set-Content -Path (Join-Path $portable "portable.txt") -Value "This file makes FPS Hub RTX keep its data in the Data folder next to the exe." -Encoding UTF8
Copy-Item README.md, THIRD-PARTY-NOTICES.md $portable
Compress-Archive -Path $portable -DestinationPath (Join-Path $artifacts "FPSHubRTX-Portable-$Version.zip") -Force

if (-not $SkipInstaller) {
    Step "Building FPSHubRTX-Setup.exe (Inno Setup)"
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
    if (-not $iscc) {
        throw "Inno Setup 6 not found. Install it (winget install JRSoftware.InnoSetup) or re-run with -SkipInstaller."
    }
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/O$artifacts" installer\FPSHubRTX.iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
}

Step "Done"
Get-ChildItem $artifacts -File | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    "{0,-36} {1,10:N0} KB  SHA256 {2}" -f $_.Name, ($_.Length / 1KB), $h
} | Tee-Object -FilePath (Join-Path $artifacts "SHA256SUMS.txt")
