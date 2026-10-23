<#
.SYNOPSIS
  Downloads Intel's PresentMon console application (MIT licence) into third_party\PresentMon.

.DESCRIPTION
  FPS Hub RTX measures FPS with PresentMon's frame-presentation (ETW) telemetry. The console exe is
  bundled next to FPSHubRTX.exe under tools\PresentMon. This script fetches it from the official
  GitHub releases, checks the Authenticode signature, records the SHA-256 and copies the licence.

.PARAMETER Version
  Release version without the "v" (e.g. 2.4.1). Default: the latest published release.

.PARAMETER ExpectedSha256
  Optional pinned hash; the download is rejected if it doesn't match.
#>
param(
    [string]$Version = "",
    [string]$ExpectedSha256 = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "third_party\PresentMon"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$headers = @{ "User-Agent" = "FPSHubRTX-build"; "Accept" = "application/vnd.github+json" }
if ($env:GITHUB_TOKEN) { $headers["Authorization"] = "Bearer $env:GITHUB_TOKEN" }  # avoids API rate limits on CI
$api = if ($Version) { "https://api.github.com/repos/GameTechDev/PresentMon/releases/tags/v$Version" } else { "https://api.github.com/repos/GameTechDev/PresentMon/releases/latest" }

Write-Host "Querying $api"
$release = Invoke-RestMethod -Uri $api -Headers $headers
$asset = $release.assets | Where-Object { $_.name -match '^PresentMon-[\d\.]+-x64\.exe$' } | Select-Object -First 1
if (-not $asset) {
    throw "No 'PresentMon-<version>-x64.exe' console asset found in release $($release.tag_name). Download it manually from https://github.com/GameTechDev/PresentMon/releases and save it as $dest\PresentMon.exe"
}

$exe = Join-Path $dest "PresentMon.exe"
Write-Host "Downloading $($asset.name) from release $($release.tag_name)"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $exe -Headers @{ "User-Agent" = "FPSHubRTX-build" }

$hash = (Get-FileHash -Algorithm SHA256 -Path $exe).Hash
if ($ExpectedSha256 -and ($hash -ne $ExpectedSha256.ToUpperInvariant())) {
    Remove-Item $exe -Force
    throw "SHA-256 mismatch for PresentMon: expected $ExpectedSha256, got $hash"
}

$sig = Get-AuthenticodeSignature -FilePath $exe
if ($sig.Status -ne "Valid") {
    Write-Warning "PresentMon signature status: $($sig.Status). Verify the download before shipping."
} elseif ($sig.SignerCertificate.Subject -notmatch "Intel") {
    Write-Warning "PresentMon is signed by an unexpected publisher: $($sig.SignerCertificate.Subject)"
} else {
    Write-Host "Signature OK: $($sig.SignerCertificate.Subject)"
}

$tag = $release.tag_name
try {
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/GameTechDev/PresentMon/$tag/LICENSE.txt" -OutFile (Join-Path $dest "LICENSE.txt") -Headers @{ "User-Agent" = "FPSHubRTX-build" }
} catch {
    Write-Warning "Could not download LICENSE.txt ($_). Add PresentMon's MIT licence text manually."
}

"$($asset.name)  $tag  SHA256=$hash" | Set-Content -Path (Join-Path $dest "VERSION.txt") -Encoding UTF8
Write-Host "PresentMon $tag saved to $exe"
Write-Host "SHA256: $hash"
