namespace FPSHubRTX.Core.Models;

/// <summary>A recorded benchmark: raw frame samples plus system samples and context.</summary>
public sealed class BenchmarkRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string? ExecutableName { get; set; }

    /// <summary>User-entered context that matters for comparability.</summary>
    public string? Resolution { get; set; }
    public string? GraphicsPreset { get; set; }
    public string? Scene { get; set; }
    public string? Notes { get; set; }

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }

    public FrameStatsSummary? Summary { get; set; }

    public double? AverageCpuPercent { get; set; }
    public double? AverageGpuPercent { get; set; }
    public double? AverageRamUsedGb { get; set; }

    public SystemInfo System { get; set; } = new();

    /// <summary>Per-frame samples: time since capture start (ms) and frame time (ms).</summary>
    public List<FrameSample> Frames { get; set; } = new();

    /// <summary>System utilisation sampled during the run.</summary>
    public List<SystemSample> SystemSamples { get; set; } = new();

    public string? PresentRuntime { get; set; }
    public string? PresentMode { get; set; }
}

public readonly record struct FrameSample(double TimeMs, double FrameTimeMs);

public sealed class SystemSample
{
    public double OffsetSeconds { get; set; }
    public double? CpuPercent { get; set; }
    public double? GpuPercent { get; set; }
    public double? RamUsedGb { get; set; }
    public double? GpuTemperatureC { get; set; }
}

public sealed class SystemInfo
{
    public string OsDescription { get; set; } = "";
    public string? CpuName { get; set; }
    public int LogicalProcessors { get; set; }
    public List<string> GpuNames { get; set; } = new();
    public double? TotalRamGb { get; set; }
    public string? DisplayResolution { get; set; }
}
