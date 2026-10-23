# FPS telemetry: how FPS Hub measures frame rate

## The short version

FPS Hub never guesses FPS. It runs Intel's **PresentMon** console application, which listens to the
Windows *Event Tracing for Windows* (ETW) events emitted by the graphics kernel (DxgKrnl), DXGI, D3D9
and the Desktop Window Manager each time a game presents a frame. Nothing is injected into the game,
no hooks, no DLLs — it is the same data source used by Intel PresentMon, OCAT, CapFrameX and others.

```
Game ──Present()──► Windows graphics stack ──ETW events──► PresentMon.exe ──CSV──► FPS Hub
```

When no frames arrive, FPS Hub shows an explicit state (*Waiting for the first frame*, *No new frames*,
*FPS unavailable for this game*, *permission needed*, *PresentMon not found*) and every number is a dash.

## Requirements

| Requirement | Why | How |
|---|---|---|
| PresentMon console exe | the telemetry collector | bundled in `tools\PresentMon\` by the build; or set its path in Settings → FPS |
| Permission to read ETW presentation events | Windows restricts trace sessions | **Either** be in the built-in *Performance Log Users* group (FPS page → *Allow FPS measuring without admin*, one UAC prompt, then sign out/in) **or** click *Capture with admin rights* for a session |
| The game's process name | to attribute frames to the right game | detected from the install folder; override with *Choose executable…* |

FPS Hub itself always runs without administrator rights.

## Graphics API compatibility

PresentMon tracks presents at the Windows display-kernel level, so coverage depends on how a game reaches the screen, not only on its API.

| API / path | Status | Notes |
|---|---|---|
| DirectX 12 | Supported | Reported as `DXGI` runtime |
| DirectX 11 / 10 | Supported | `DXGI` |
| DirectX 9 / 9Ex | Supported | `D3D9` runtime |
| Vulkan | Supported in the common case | Modern Windows drivers present Vulkan swap chains through DXGI/DWM ("layered on DXGI"); runtime may show as `DXGI` or `Other` |
| OpenGL | Usually supported | Presents through the display kernel; runtime shows as `Other`. Very old drivers / some GL-on-GDI paths may not be attributable |
| UWP / Store games | Supported | Same as their underlying API |
| Frame generation (DLSS FG, FSR FG, AFMF) | Measured as presented by the swap chain | Generated frames presented by the driver may or may not be included depending on how they are presented; enable *Count displayed frames* in Settings → FPS to use `MsBetweenDisplayChange` |
| Cloud-streamed games (GeForce NOW, Xbox Cloud) | Not measurable | The game doesn't run on your PC; you'd measure the video player |
| Games under some anti-cheat/sandbox configurations | Usually fine | PresentMon is external and doesn't touch the game; if the process can't be named by Windows, choose the exe manually |

Exclusive-fullscreen vs borderless vs windowed: all are measured (the *Presentation* line shows e.g. `Hardware: Independent Flip` or `Composed: Flip`). This affects the **overlay**, not measurement.

## Metric definitions

All statistics use frame times (milliseconds) from PresentMon's `MsBetweenPresents` column (or
`MsBetweenDisplayChange` when *Count displayed frames* is enabled and the value is available).

| Metric | Definition |
|---|---|
| Current FPS | frames presented in the most recent ~1 s ÷ their total time |
| Average FPS | total frames ÷ total time (time-weighted — **not** the mean of per-frame FPS) |
| Minimum / Maximum FPS | 1000 ÷ slowest frame time, 1000 ÷ fastest frame time |
| 1% low | 1000 ÷ 99th-percentile frame time (nearest rank). Shown only with ≥ 100 frames |
| 0.1% low | 1000 ÷ 99.9th-percentile frame time. Shown only with ≥ 1000 frames |
| Frame time | the latest frame's interval |
| Stability | coefficient of variation of frame times: < 8 % Excellent, < 15 % Good, < 25 % Fair, else Unstable (≥ 100 frames) |
| Stutter frames | frames longer than 2 × the median frame time |
| Pauses | frames longer than the pause threshold (default 2000 ms: loading screens, alt-tab) are excluded and counted separately |

Long sessions use a 0.01 ms-resolution histogram (bounded memory, percentiles accurate to 0.01 ms). Benchmarks keep every frame and compute exact statistics. Unit tests in `tests/.../TelemetryTests.cs` pin these definitions.

When a process has several swap chains (e.g. a launcher overlay plus the game), only the most active one is used so their intervals aren't mixed.

## System metrics

| Metric | Source | When unavailable |
|---|---|---|
| CPU % | `GetSystemTimes` | first sample (needs a baseline) |
| GPU % | `\GPU Engine(*)\Utilization Percentage` (busiest engine per adapter, like Task Manager) | WDDM 1.x drivers / Windows < 10 1709 |
| GPU memory | `\GPU Adapter Memory(*)\Dedicated Usage`; NVML total for NVIDIA | counters missing |
| GPU temperature | NVIDIA NVML (`nvml.dll` from the driver) | AMD / Intel GPUs (no documented user-mode API) |
| CPU temperature | `\Thermal Zone Information(*)\Temperature` (ACPI) | most desktops don't expose a thermal zone; labelled as ACPI, not core temperature |
| RAM | `GlobalMemoryStatusEx` | — |
| Game memory | working set of the game's processes | protected processes deny access |
| Disk / network | `PhysicalDisk(_Total)`, `Network Interface(*)` counters | minimal-resource mode turns these off |

## Overlay compatibility

The overlay is a separate transparent, click-through, top-most window composed by Windows (DWM). Nothing is drawn inside the game.

| Game display mode | Overlay visible? |
|---|---|
| Windowed | Yes |
| Borderless / "fullscreen windowed" | Yes |
| Flip-model "fullscreen" (many DX12 / modern DX11 games on Windows 10/11) | Often yes — Windows composes the overlay (the game may leave *independent flip* while it's shown); verify per game |
| Legacy exclusive fullscreen (older DX9/DX11 games) | Often **no** — the game owns the display |

When the overlay can't appear, use borderless mode, keep FPS Hub's *Performance* page on a second monitor, or watch the tray tooltip/notifications. Toggle the overlay with **Ctrl+Shift+F10** and move it with **Ctrl+Shift+F11** (configurable).

## Overhead

PresentMon's ETW session is lightweight (it only consumes events Windows already emits); measure it on your own system with the Performance page if overhead matters. FPS Hub reads its CSV every 200 ms, refreshes the UI twice a second, and samples system counters at the configured interval (default 1 s; *minimal-resource mode* samples only CPU, GPU and memory). Capture stops automatically when the game exits (`--terminate_on_proc_exit`), and the per-session CSV is deleted unless the game's profile asks to keep it.
