# Dependencies

## Runtime (shipped)

| Component | Version | Licence | Purpose |
|---|---|---|---|
| .NET runtime + WPF + Windows Forms (self-contained in `FPSHubRTX.exe`) | 10.0 (LTS) | MIT | Application runtime; WinForms is used only for the tray icon |
| Microsoft.Windows.SDK.NET (CsWinRT projection, part of the `net10.0-windows10.0.19041.0` target) | 10.0.19041 | MIT | `Windows.Management.Deployment.PackageManager` to list Store/Xbox packages |
| Intel PresentMon console (`tools\PresentMon\PresentMon.exe`) | latest 2.x release at build time | MIT | Frame-presentation telemetry for FPS |

Windows components used through documented APIs (no redistribution): kernel32 (`GetSystemTimes`, `GlobalMemoryStatusEx`, `QueryFullProcessImageName`), user32 (`RegisterHotKey`, `GetForegroundWindow`), pdh.dll (performance counters), NVIDIA `nvml.dll` (installed by the NVIDIA driver, loaded only if present).

`FPSHubRTX.Core` and `FPSHubRTX.ViewModels` have **no NuGet package dependencies**.

## Build / test only

| Package | Version | Licence |
|---|---|---|
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Inno Setup | 6.x | Inno Setup licence (free, incl. commercial use) |

## Supported systems

Windows 10 version 1809 (build 17763) or later and Windows 11, x64. Store/Xbox app-id resolution uses an API from Windows 10 2004 (19041); on 1809–1909 those games fall back to their executable or appear without a launch method.
