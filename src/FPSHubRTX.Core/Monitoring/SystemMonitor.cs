using System.Diagnostics;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Monitoring;

public sealed record DriveSpace(string Root, string? Label, long FreeBytes, long TotalBytes, DriveKind Kind);

/// <summary>
/// One sample of system telemetry. Every value is nullable: null means "not available on this PC",
/// and the matching *Note explains why. Values are never estimated or invented.
/// </summary>
public sealed class SystemSnapshot
{
    public DateTime TimestampUtc { get; init; }

    public double? CpuPercent { get; init; }
    public double? CpuTemperatureC { get; init; }
    public string CpuTemperatureNote { get; init; } = "";

    public double? GpuPercent { get; init; }
    public string? GpuNote { get; init; }
    public double? GpuTemperatureC { get; init; }
    public string GpuTemperatureNote { get; init; } = "";
    public double? GpuMemoryUsedBytes { get; init; }
    public double? GpuMemoryTotalBytes { get; init; }

    public double? RamTotalBytes { get; init; }
    public double? RamAvailableBytes { get; init; }
    public double? RamUsedBytes => RamTotalBytes is { } t && RamAvailableBytes is { } a ? t - a : null;
    public double? RamPercent => RamTotalBytes is > 0 && RamUsedBytes is { } u ? u / RamTotalBytes * 100 : null;

    public double? GameProcessMemoryBytes { get; init; }
    public double? GameGpuPercent { get; init; }

    public double? DiskActivePercent { get; init; }
    public double? DiskReadBytesPerSec { get; init; }
    public double? DiskWriteBytesPerSec { get; init; }

    public double? NetworkReceiveBytesPerSec { get; init; }
    public double? NetworkSendBytesPerSec { get; init; }

    public IReadOnlyList<DriveSpace> Drives { get; init; } = Array.Empty<DriveSpace>();

    public int? ForegroundProcessId { get; init; }
    public string? ForegroundProcessName { get; init; }
}

/// <summary>
/// Samples CPU, GPU, memory, disk and network through documented Windows interfaces:
/// GetSystemTimes / GlobalMemoryStatusEx (kernel32), PDH performance counters (GPU Engine, GPU Adapter
/// Memory, PhysicalDisk, Network Interface, Thermal Zone Information) and NVML for NVIDIA temperatures.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly ILog _log;
    private readonly IDriveProvider _drives;
    private readonly object _gate = new();
    private readonly LinkedList<SystemSnapshot> _history = new();
    private Timer? _timer;
    private int _sampling;
    private PdhQuery? _pdh;
    private Nvml? _nvml;
    private bool _nvmlTried;
    private ulong _lastIdle, _lastKernel, _lastUser;
    private IReadOnlySet<int>? _gamePids;
    private IReadOnlyList<DriveSpace> _driveCache = Array.Empty<DriveSpace>();
    private int _sampleCount;
    private bool _minimal;

    public SystemMonitor(ILog log, IDriveProvider drives)
    {
        _log = log;
        _drives = drives;
    }

    public event Action<SystemSnapshot>? SampleTaken;

    public SystemSnapshot? Latest { get; private set; }

    public bool IsRunning => _timer is not null;

    public int IntervalMs { get; private set; } = 1000;

    public int HistoryCapacity { get; set; } = 300;

    public IReadOnlyList<SystemSnapshot> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    public bool MinimalMode
    {
        get => _minimal;
        set
        {
            _minimal = value;
            lock (_gate) ResetPdh();
        }
    }

    /// <summary>Processes of the game being monitored (for per-game memory and GPU usage).</summary>
    public void SetGameProcesses(IReadOnlySet<int>? pids) => _gamePids = pids;

    public void Start(int intervalMs)
    {
        IntervalMs = Math.Clamp(intervalMs, 250, 10_000);
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => Sample(), null, 0, IntervalMs);
        }
        _log.Info($"System monitoring started ({IntervalMs} ms, minimal={_minimal})");
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            ResetPdh();
            _nvml?.Dispose();
            _nvml = null;
            _nvmlTried = false;
        }
        _log.Info("System monitoring stopped");
    }

    private void ResetPdh()
    {
        _pdh?.Dispose();
        _pdh = null;
    }

    private void EnsureCounters()
    {
        if (_pdh is not null || !OperatingSystem.IsWindows()) return;
        _pdh = new PdhQuery();
        _pdh.Add("gpu", @"\GPU Engine(*)\Utilization Percentage");
        _pdh.Add("vram", @"\GPU Adapter Memory(*)\Dedicated Usage");
        if (!_minimal)
        {
            _pdh.Add("diskIdle", @"\PhysicalDisk(_Total)\% Idle Time");
            _pdh.Add("diskRead", @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            _pdh.Add("diskWrite", @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            _pdh.Add("netRx", @"\Network Interface(*)\Bytes Received/sec");
            _pdh.Add("netTx", @"\Network Interface(*)\Bytes Sent/sec");
            _pdh.Add("thermal", @"\Thermal Zone Information(*)\Temperature");
        }
        _pdh.Collect(); // rate counters need a baseline sample
    }

    /// <summary>Takes one sample immediately (also used by tests on non-Windows to verify graceful nulls).</summary>
    public SystemSnapshot Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return Latest ?? new SystemSnapshot { TimestampUtc = DateTime.UtcNow };
        try
        {
            SystemSnapshot snapshot;
            lock (_gate) snapshot = SampleCore();
            Latest = snapshot;
            lock (_gate)
            {
                _history.AddLast(snapshot);
                while (_history.Count > HistoryCapacity) _history.RemoveFirst();
            }
            SampleTaken?.Invoke(snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            _log.Error("System sample failed", ex);
            return Latest ?? new SystemSnapshot { TimestampUtc = DateTime.UtcNow };
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private SystemSnapshot SampleCore()
    {
        var now = DateTime.UtcNow;
        _sampleCount++;
        if (!OperatingSystem.IsWindows())
        {
            return new SystemSnapshot
            {
                TimestampUtc = now,
                GpuNote = "Only available on Windows",
                CpuTemperatureNote = "Only available on Windows",
                GpuTemperatureNote = "Only available on Windows",
                Drives = SampleDrives(),
            };
        }

        EnsureCounters();
        if (!_nvmlTried)
        {
            _nvmlTried = true;
            _nvml = Nvml.TryLoad();
        }

        var cpu = SampleCpu();
        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        var memOk = NativeMethods.GlobalMemoryStatusEx(ref mem);

        var pdhOk = _pdh?.Collect() ?? false;
        double? gpu = null, gameGpu = null;
        string? gpuNote = null;
        if (pdhOk && _pdh!.Has("gpu"))
        {
            (gpu, gameGpu) = GpuEngineMath.Aggregate(_pdh.GetArray("gpu"), _minimal ? null : _gamePids);
            if (gpu is null) gpuNote = "GPU counters returned no data yet";
        }
        else
        {
            gpuNote = "Windows GPU performance counters are unavailable (requires WDDM 2.x drivers, Windows 10 1709+)";
        }

        double? vramUsed = null;
        if (pdhOk && _pdh!.Has("vram"))
        {
            var items = _pdh.GetArray("vram");
            if (items.Count > 0) vramUsed = items.Max(i => i.Value);
        }

        var nvMem = _nvml?.GetMemory();
        double? gpuTemp = _nvml?.GetMaxTemperature();
        var gpuTempNote = gpuTemp is not null
            ? "NVIDIA NVML"
            : "Not available: Windows has no documented API for this GPU's temperature (NVIDIA GPUs are read through NVML).";

        double? cpuTemp = null;
        var cpuTempNote = "Not available: Windows exposes no documented user-mode CPU temperature sensor on this PC.";
        if (!_minimal && pdhOk && _pdh!.Has("thermal"))
        {
            var zones = _pdh.GetArray("thermal").Where(z => z.Value > 200 && z.Value < 400).ToList(); // Kelvin sanity range
            if (zones.Count > 0)
            {
                cpuTemp = zones.Max(z => z.Value) - 273.15;
                cpuTempNote = "ACPI thermal zone (motherboard-reported; may differ from CPU core temperature)";
            }
        }

        double? gameMem = null;
        var pids = _gamePids;
        if (pids is not null && pids.Count > 0)
        {
            double total = 0;
            var any = false;
            foreach (var pid in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    total += p.WorkingSet64;
                    any = true;
                }
                catch (Exception)
                {
                    // exited or protected
                }
            }
            gameMem = any ? total : null;
        }

        var (fgPid, fgName) = Foreground();

        if (_sampleCount % 10 == 1) _driveCache = SampleDrives();

        return new SystemSnapshot
        {
            TimestampUtc = now,
            CpuPercent = cpu,
            CpuTemperatureC = cpuTemp,
            CpuTemperatureNote = cpuTempNote,
            GpuPercent = gpu,
            GpuNote = gpuNote,
            GpuTemperatureC = gpuTemp,
            GpuTemperatureNote = gpuTempNote,
            GpuMemoryUsedBytes = vramUsed ?? (nvMem is { } m1 ? m1.Used : null),
            GpuMemoryTotalBytes = nvMem is { } m2 ? m2.Total : null,
            RamTotalBytes = memOk ? mem.ullTotalPhys : null,
            RamAvailableBytes = memOk ? mem.ullAvailPhys : null,
            GameProcessMemoryBytes = gameMem,
            GameGpuPercent = gameGpu,
            DiskActivePercent = _minimal ? null : Clamp100(100 - _pdh?.GetValue("diskIdle")),
            DiskReadBytesPerSec = _minimal ? null : _pdh?.GetValue("diskRead"),
            DiskWriteBytesPerSec = _minimal ? null : _pdh?.GetValue("diskWrite"),
            NetworkReceiveBytesPerSec = _minimal ? null : SumArray("netRx"),
            NetworkSendBytesPerSec = _minimal ? null : SumArray("netTx"),
            Drives = _driveCache,
            ForegroundProcessId = fgPid,
            ForegroundProcessName = fgName,
        };
    }

    private double? SumArray(string key)
    {
        if (_pdh is null || !_pdh.Has(key)) return null;
        var items = _pdh.GetArray(key);
        return items.Count == 0 ? null : items.Sum(i => i.Value);
    }

    private static double? Clamp100(double? v) => v is null ? null : Math.Clamp(v.Value, 0, 100);

    private double? SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return null;
        ulong idle = idleFt.Value, kernel = kernelFt.Value, user = userFt.Value;
        double? result = null;
        if (_lastKernel != 0)
        {
            var idleDelta = idle - _lastIdle;
            var totalDelta = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle time
            if (totalDelta > 0) result = Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
        }
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        return result;
    }

    private IReadOnlyList<DriveSpace> SampleDrives()
        => _drives.GetDrives()
            .Where(d => d.IsReady && d.FreeBytes is not null && d.TotalBytes is > 0 && d.Kind is DriveKind.Fixed or DriveKind.Removable)
            .Select(d => new DriveSpace(d.RootPath, d.Label, d.FreeBytes!.Value, d.TotalBytes!.Value, d.Kind))
            .ToList();

    private static (int?, string?) Foreground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (null, null);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return (null, null);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return ((int)pid, p.ProcessName);
        }
        catch (Exception)
        {
            return ((int)pid, null);
        }
    }

    /// <summary>Static hardware description for benchmark reports.</summary>
    public Models.SystemInfo DescribeSystem()
    {
        var info = new Models.SystemInfo
        {
            OsDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            LogicalProcessors = Environment.ProcessorCount,
        };
        if (OperatingSystem.IsWindows())
        {
            var reg = new WindowsRegistryReader();
            info.CpuName = reg.GetString(RegHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim();
            const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            foreach (var sub in reg.GetSubKeyNames(RegHive.LocalMachine, displayClass).Where(s => s.All(char.IsDigit)))
            {
                var name = reg.GetString(RegHive.LocalMachine, $@"{displayClass}\{sub}", "DriverDesc");
                if (!string.IsNullOrWhiteSpace(name) && !info.GpuNames.Contains(name)) info.GpuNames.Add(name);
            }
            var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
            if (NativeMethods.GlobalMemoryStatusEx(ref mem)) info.TotalRamGb = Math.Round(mem.ullTotalPhys / 1024d / 1024 / 1024, 1);
        }
        return info;
    }

    public void Dispose()
    {
        Stop();
    }
}
