using System.Globalization;
using System.Text;
using System.Text.Json;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Persistence;
using FPSHubRTX.Core.Services;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.Core.Export;

/// <summary>CSV / JSON / plain-text exports. All numbers use the invariant culture so files open anywhere.</summary>
public static class Exporters
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Csv(string? value)
    {
        if (value is null) return "";
        // Neutralise spreadsheet formula injection from untrusted text (game names, labels).
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static string N(double? v, string format = "0.###") => v is null ? "" : v.Value.ToString(format, Inv);

    /// <summary>Per-frame CSV for one benchmark run.</summary>
    public static string BenchmarkFramesCsv(BenchmarkRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TimeMs,FrameTimeMs,InstantFps");
        foreach (var f in run.Frames)
        {
            sb.Append(N(f.TimeMs, "0.###")).Append(',')
              .Append(N(f.FrameTimeMs, "0.####")).Append(',')
              .AppendLine(f.FrameTimeMs > 0 ? N(1000.0 / f.FrameTimeMs, "0.##") : "");
        }
        return sb.ToString();
    }

    public const string SummaryHeader = "Label,Game,StartUtc,DurationSeconds,Frames,AverageFps,MinFps,MaxFps,OnePercentLowFps,PointOnePercentLowFps,AvgFrameTimeMs,P99FrameTimeMs,StdDevMs,Stability,AvgCpuPercent,AvgGpuPercent,AvgRamGb,Resolution,Preset,Scene,Cpu,Gpu,PresentMode,MetricSource";

    /// <summary>One summary row per run — for comparing runs in a spreadsheet.</summary>
    public static string BenchmarkSummariesCsv(IEnumerable<BenchmarkRun> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SummaryHeader);
        foreach (var r in runs)
        {
            var s = r.Summary;
            sb.AppendJoin(',', new[]
            {
                Csv(r.Label), Csv(r.GameName), r.StartUtc.ToString("O", Inv), N((r.EndUtc - r.StartUtc).TotalSeconds, "0.0"),
                (s?.FrameCount ?? 0).ToString(Inv), N(s?.AverageFps, "0.0"), N(s?.MinimumFps, "0.0"), N(s?.MaximumFps, "0.0"),
                N(s?.OnePercentLowFps, "0.0"), N(s?.PointOnePercentLowFps, "0.0"), N(s?.AverageFrameTimeMs, "0.00"),
                N(s?.P99FrameTimeMs, "0.00"), N(s?.FrameTimeStdDevMs, "0.00"), s?.Stability.ToString() ?? "",
                N(r.AverageCpuPercent, "0.0"), N(r.AverageGpuPercent, "0.0"), N(r.AverageRamUsedGb, "0.00"),
                Csv(r.Resolution), Csv(r.GraphicsPreset), Csv(r.Scene), Csv(r.System.CpuName), Csv(string.Join(" / ", r.System.GpuNames)),
                Csv(r.PresentMode), Csv(s?.Source),
            });
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string BenchmarkJson(BenchmarkRun run) => JsonSerializer.Serialize(run, JsonDefaults.Options);

    public static string SessionsCsv(IEnumerable<GameSession> sessions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Game,Launcher,StartUtc,EndUtc,DurationMinutes,EndReason,ExitCode,LaunchedFromHub,FpsAvailability,AverageFps,OnePercentLowFps,AvgCpuPercent,AvgGpuPercent,PeakGameMemoryMb");
        foreach (var s in sessions.OrderBy(x => x.StartUtc))
        {
            sb.AppendJoin(',', new[]
            {
                Csv(s.GameName), s.Launcher.ToString(), s.StartUtc.ToString("O", Inv), s.EndUtc.ToString("O", Inv),
                N(s.Duration.TotalMinutes, "0.0"), s.EndReason.ToString(), s.ExitCode?.ToString(Inv) ?? "", s.LaunchedFromHub ? "true" : "false",
                s.FpsAvailability.ToString(), N(s.Fps?.AverageFps, "0.0"), N(s.Fps?.OnePercentLowFps, "0.0"),
                N(s.AverageCpuPercent, "0.0"), N(s.AverageGpuPercent, "0.0"), N(s.PeakGameMemoryBytes / 1024 / 1024, "0"),
            });
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string SessionsJson(IEnumerable<GameSession> sessions) => JsonSerializer.Serialize(sessions.ToList(), JsonDefaults.Options);

    /// <summary>Human-readable summary of a benchmark run.</summary>
    public static string BenchmarkReport(BenchmarkRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FPS Hub RTX — Benchmark report");
        sb.AppendLine(new string('=', 40));
        sb.AppendLine($"Label:        {run.Label}");
        sb.AppendLine($"Game:         {run.GameName}");
        sb.AppendLine($"Recorded:     {run.StartUtc.ToLocalTime():f} ({(run.EndUtc - run.StartUtc).TotalSeconds:0} s)");
        if (!string.IsNullOrWhiteSpace(run.Resolution)) sb.AppendLine($"Resolution:   {run.Resolution}");
        if (!string.IsNullOrWhiteSpace(run.GraphicsPreset)) sb.AppendLine($"Preset:       {run.GraphicsPreset}");
        if (!string.IsNullOrWhiteSpace(run.Scene)) sb.AppendLine($"Scene:        {run.Scene}");
        sb.AppendLine($"System:       {run.System.CpuName ?? "CPU unknown"} | {string.Join(", ", run.System.GpuNames.DefaultIfEmpty("GPU unknown"))} | {(run.System.TotalRamGb is { } ram ? $"{ram:0} GB RAM" : "RAM unknown")}");
        sb.AppendLine($"OS:           {run.System.OsDescription}");
        if (run.PresentMode is not null) sb.AppendLine($"Presentation: {run.PresentRuntime} / {run.PresentMode}");
        sb.AppendLine();

        var s = run.Summary;
        if (s is null || s.FrameCount == 0)
        {
            sb.AppendLine("No frames were measured during this run.");
            return sb.ToString();
        }
        sb.AppendLine($"Frames measured:  {s.FrameCount:N0} (ignored as pauses: {s.IgnoredFrameCount:N0})");
        sb.AppendLine($"Average FPS:      {s.AverageFps:0.0}");
        sb.AppendLine($"1% low FPS:       {(s.OnePercentLowFps is { } a ? a.ToString("0.0") : $"n/a (needs {FrameStatistics.MinFramesForOnePercentLow}+ frames)")}");
        sb.AppendLine($"0.1% low FPS:     {(s.PointOnePercentLowFps is { } b ? b.ToString("0.0") : $"n/a (needs {FrameStatistics.MinFramesForPointOnePercentLow}+ frames)")}");
        sb.AppendLine($"Min / Max FPS:    {s.MinimumFps:0.0} / {s.MaximumFps:0.0}");
        sb.AppendLine($"Frame time:       avg {s.AverageFrameTimeMs:0.00} ms, median {s.MedianFrameTimeMs:0.00} ms, 99th pct {s.P99FrameTimeMs:0.00} ms, σ {s.FrameTimeStdDevMs:0.00} ms");
        sb.AppendLine($"Stutter frames:   {s.StutterFrameCount:N0} (> 2× median frame time)");
        sb.AppendLine($"Stability:        {FrameStatistics.Describe(s.Stability)}");
        sb.AppendLine($"CPU / GPU avg:    {(run.AverageCpuPercent is { } c ? $"{c:0}%" : "n/a")} / {(run.AverageGpuPercent is { } g ? $"{g:0}%" : "n/a")}");
        sb.AppendLine();
        sb.AppendLine($"Source: PresentMon {s.Source}. Low-percentile FPS = 1000 / percentile frame time.");
        return sb.ToString();
    }

    public static string ComparisonReport(BenchmarkComparison c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Comparing '{c.B.Label}' against '{c.A.Label}'");
        string D(double? d) => d is null ? "n/a" : $"{(d > 0 ? "+" : "")}{d:0.0}%";
        sb.AppendLine($"Average FPS: {c.A.Summary?.AverageFps:0.0} → {c.B.Summary?.AverageFps:0.0} ({D(c.AverageFpsDeltaPercent)})");
        sb.AppendLine($"1% low:      {c.A.Summary?.OnePercentLowFps:0.0} → {c.B.Summary?.OnePercentLowFps:0.0} ({D(c.OnePercentLowDeltaPercent)})");
        sb.AppendLine($"0.1% low:    {c.A.Summary?.PointOnePercentLowFps:0.0} → {c.B.Summary?.PointOnePercentLowFps:0.0} ({D(c.PointOnePercentLowDeltaPercent)})");
        if (c.ComparabilityWarnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("These runs may NOT be directly comparable:");
            foreach (var w in c.ComparabilityWarnings) sb.AppendLine(" • " + w);
        }
        return sb.ToString();
    }
}
