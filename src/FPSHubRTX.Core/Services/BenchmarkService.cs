using System.Text.Json;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Monitoring;
using FPSHubRTX.Core.Persistence;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.Core.Services;

/// <summary>Benchmark history: one JSON file per run in the benchmarks folder.</summary>
public sealed class BenchmarkStore
{
    private readonly string _directory;
    private readonly ILog _log;
    private readonly object _gate = new();
    private List<BenchmarkRun>? _cache;

    public BenchmarkStore(string directory, ILog? log = null)
    {
        _directory = directory;
        _log = log ?? NullLog.Instance;
        Directory.CreateDirectory(directory);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<BenchmarkRun> GetAll()
    {
        lock (_gate)
        {
            _cache ??= LoadAll();
            return _cache.OrderByDescending(r => r.StartUtc).ToList();
        }
    }

    private List<BenchmarkRun> LoadAll()
    {
        var list = new List<BenchmarkRun>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var run = JsonSerializer.Deserialize<BenchmarkRun>(stream, JsonDefaults.Options);
                if (run is not null) list.Add(run);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Skipping unreadable benchmark {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return list;
    }

    public void Save(BenchmarkRun run)
    {
        lock (_gate)
        {
            var path = Path.Combine(_directory, $"{run.Id:N}.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(run, JsonDefaults.Compact));
            File.Move(tmp, path, overwrite: true);
            _cache ??= LoadAll();
            _cache.RemoveAll(r => r.Id == run.Id);
            _cache.Add(run);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            var path = Path.Combine(_directory, $"{id:N}.json");
            if (File.Exists(path)) File.Delete(path);
            _cache?.RemoveAll(r => r.Id == id);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteAll()
    {
        lock (_gate)
        {
            foreach (var f in Directory.EnumerateFiles(_directory, "*.json")) File.Delete(f);
            _cache = new List<BenchmarkRun>();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class BenchmarkContext
{
    public required string Label { get; init; }
    public string? Resolution { get; init; }
    public string? GraphicsPreset { get; init; }
    public string? Scene { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// Records a benchmark from the live PresentMon frame stream plus system samples. A benchmark can only
/// start while FPS is actually being measured for a game — it never records synthetic data.
/// </summary>
public sealed class BenchmarkService
{
    private readonly FpsMonitorService _fps;
    private readonly SystemMonitor _system;
    private readonly BenchmarkStore _store;
    private readonly Func<FpsSettings> _settings;
    private readonly ILog _log;
    private readonly object _gate = new();
    private BenchmarkRun? _current;
    private double? _firstFrameTime;

    public BenchmarkService(FpsMonitorService fps, SystemMonitor system, BenchmarkStore store, Func<FpsSettings> settings, ILog log)
    {
        _fps = fps;
        _system = system;
        _store = store;
        _settings = settings;
        _log = log;
        _fps.FramesCaptured += OnFrames;
        _system.SampleTaken += OnSample;
    }

    public bool IsRecording { get { lock (_gate) return _current is not null; } }

    public BenchmarkRun? Current { get { lock (_gate) return _current; } }

    public int RecordedFrames { get { lock (_gate) return _current?.Frames.Count ?? 0; } }

    public event Action<BenchmarkRun>? Completed;

    /// <summary>Starts recording. Returns an error message when it can't (nothing is being measured).</summary>
    public string? Start(BenchmarkContext context, string? gameName, string? exeName)
    {
        var snapshot = _fps.GetSnapshot();
        if (snapshot.State != FpsCaptureState.Measuring || snapshot.GameId is null)
        {
            return "Start the game and wait until the FPS Monitor shows 'Measuring' before starting a benchmark.";
        }

        lock (_gate)
        {
            if (_current is not null) return "A benchmark is already recording.";
            _current = new BenchmarkRun
            {
                Label = string.IsNullOrWhiteSpace(context.Label) ? $"Benchmark {DateTime.Now:g}" : context.Label.Trim(),
                GameId = snapshot.GameId,
                GameName = gameName ?? snapshot.GameName ?? "",
                ExecutableName = exeName,
                Resolution = context.Resolution,
                GraphicsPreset = context.GraphicsPreset,
                Scene = context.Scene,
                Notes = context.Notes,
                StartUtc = DateTime.UtcNow,
                System = _system.DescribeSystem(),
                PresentRuntime = snapshot.PresentRuntime,
                PresentMode = snapshot.PresentMode,
            };
            _firstFrameTime = null;
        }
        _log.Info($"Benchmark started: {context.Label}");
        return null;
    }

    public BenchmarkRun? Stop()
    {
        BenchmarkRun? run;
        lock (_gate)
        {
            run = _current;
            _current = null;
        }
        if (run is null) return null;

        run.EndUtc = DateTime.UtcNow;
        run.Summary = run.Frames.Count > 0
            ? FrameStatistics.Compute(run.Frames.Select(f => f.FrameTimeMs), _settings().MaxValidFrameTimeMs, _fps.GetSnapshot().MetricSource)
            : null;
        run.AverageCpuPercent = Avg(run.SystemSamples.Select(s => s.CpuPercent));
        run.AverageGpuPercent = Avg(run.SystemSamples.Select(s => s.GpuPercent));
        run.AverageRamUsedGb = Avg(run.SystemSamples.Select(s => s.RamUsedGb));
        _store.Save(run);
        _log.Info($"Benchmark saved: {run.Label}, {run.Frames.Count} frames");
        Completed?.Invoke(run);
        return run;
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list.Average();
    }

    private void OnFrames(string gameId, IReadOnlyList<FrameSample> frames)
    {
        lock (_gate)
        {
            if (_current is null || !_current.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var f in frames)
            {
                _firstFrameTime ??= f.TimeMs;
                _current.Frames.Add(new FrameSample(f.TimeMs - _firstFrameTime.Value, f.FrameTimeMs));
            }
        }
    }

    private void OnSample(SystemSnapshot s)
    {
        lock (_gate)
        {
            if (_current is null) return;
            _current.SystemSamples.Add(new SystemSample
            {
                OffsetSeconds = (s.TimestampUtc - _current.StartUtc).TotalSeconds,
                CpuPercent = s.CpuPercent,
                GpuPercent = s.GpuPercent,
                RamUsedGb = s.RamUsedBytes is { } r ? r / 1024 / 1024 / 1024 : null,
                GpuTemperatureC = s.GpuTemperatureC,
            });
        }
    }
}

public sealed record BenchmarkComparison(BenchmarkRun A, BenchmarkRun B, double? AverageFpsDeltaPercent, double? OnePercentLowDeltaPercent,
    double? PointOnePercentLowDeltaPercent, IReadOnlyList<string> ComparabilityWarnings)
{
    public bool DirectlyComparable => ComparabilityWarnings.Count == 0;
}

public static class BenchmarkComparer
{
    /// <summary>Compares B against A and lists every reason the two runs may not be directly comparable.</summary>
    public static BenchmarkComparison Compare(BenchmarkRun a, BenchmarkRun b)
    {
        var warnings = new List<string>();
        if (!a.GameId.Equals(b.GameId, StringComparison.OrdinalIgnoreCase)) warnings.Add("Different games.");
        void Field(string label, string? x, string? y)
        {
            if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y))
                warnings.Add($"{label} not recorded for both runs — can't confirm they match.");
            else if (!x.Trim().Equals(y.Trim(), StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Different {label.ToLowerInvariant()}: '{x}' vs '{y}'.");
        }
        Field("Resolution", a.Resolution, b.Resolution);
        Field("Graphics preset", a.GraphicsPreset, b.GraphicsPreset);
        Field("Scene", a.Scene, b.Scene);
        if (!string.Equals(a.System.CpuName, b.System.CpuName, StringComparison.OrdinalIgnoreCase)) warnings.Add("Recorded on a different CPU.");
        if (!a.System.GpuNames.SequenceEqual(b.System.GpuNames, StringComparer.OrdinalIgnoreCase)) warnings.Add("Recorded with different GPU(s) or GPU driver names.");
        if (!string.Equals(a.PresentMode, b.PresentMode, StringComparison.OrdinalIgnoreCase) && a.PresentMode is not null && b.PresentMode is not null)
            warnings.Add($"Different presentation mode ('{a.PresentMode}' vs '{b.PresentMode}') — e.g. fullscreen vs windowed.");
        if (a.Summary is null || b.Summary is null) warnings.Add("One of the runs has no measured frames.");
        else
        {
            var da = a.Summary.MeasuredSeconds;
            var db = b.Summary.MeasuredSeconds;
            if (da > 0 && db > 0 && Math.Max(da, db) / Math.Min(da, db) > 1.5) warnings.Add($"Run lengths differ a lot ({da:0}s vs {db:0}s).");
            if (a.Summary.FrameCount < FrameStatistics.MinFramesForOnePercentLow || b.Summary.FrameCount < FrameStatistics.MinFramesForOnePercentLow)
                warnings.Add("Too few frames in one run for reliable low-percentile figures.");
        }

        static double? Delta(double? x, double? y) => x is > 0 && y is not null ? (y.Value - x.Value) / x.Value * 100.0 : null;
        return new BenchmarkComparison(a, b,
            Delta(a.Summary?.AverageFps, b.Summary?.AverageFps),
            Delta(a.Summary?.OnePercentLowFps, b.Summary?.OnePercentLowFps),
            Delta(a.Summary?.PointOnePercentLowFps, b.Summary?.PointOnePercentLowFps),
            warnings);
    }
}
