using FPSHubRTX.Core.Models;

namespace FPSHubRTX.Core.Telemetry;

/// <summary>
/// Frame-rate statistics from measured frame times (milliseconds).
///
/// Definitions (documented in docs/FPS-TELEMETRY.md):
///   Average FPS   = frames / total time      (time-weighted, NOT the mean of per-frame FPS values)
///   Min / Max FPS = 1000 / slowest frame time, 1000 / fastest frame time
///   1% low FPS    = 1000 / 99th-percentile frame time   (needs ≥ 100 frames)
///   0.1% low FPS  = 1000 / 99.9th-percentile frame time (needs ≥ 1000 frames)
/// Frame times above the configured pause threshold (loading screens, alt-tab) are excluded and counted separately.
/// </summary>
public static class FrameStatistics
{
    public const int MinFramesForOnePercentLow = 100;
    public const int MinFramesForPointOnePercentLow = 1000;
    public const int MinFramesForStability = 100;

    public static FrameStatsSummary Compute(IEnumerable<double> frameTimesMs, double maxValidFrameTimeMs = 2000, string source = "MsBetweenPresents")
    {
        var valid = new List<double>();
        long ignored = 0;
        foreach (var ft in frameTimesMs)
        {
            if (IsValid(ft, maxValidFrameTimeMs)) valid.Add(ft);
            else ignored++;
        }
        return FromValidSorted(SortInPlace(valid), ignored, source);
    }

    public static bool IsValid(double frameTimeMs, double maxValidFrameTimeMs)
        => double.IsFinite(frameTimeMs) && frameTimeMs > 0.01 && frameTimeMs <= maxValidFrameTimeMs;

    private static List<double> SortInPlace(List<double> list)
    {
        list.Sort();
        return list;
    }

    /// <summary>Nearest-rank percentile on an ascending sorted list (p in [0,1]).</summary>
    public static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0) return double.NaN;
        var rank = (int)Math.Ceiling(p * sortedAscending.Count) - 1;
        rank = Math.Clamp(rank, 0, sortedAscending.Count - 1);
        return sortedAscending[rank];
    }

    internal static FrameStatsSummary FromValidSorted(IReadOnlyList<double> sorted, long ignored, string source)
    {
        var n = sorted.Count;
        var summary = new FrameStatsSummary { FrameCount = n, IgnoredFrameCount = ignored, Source = source };
        if (n == 0)
        {
            summary.Stability = StabilityRating.NotEnoughData;
            return summary;
        }

        double sum = 0, sumSq = 0;
        foreach (var ft in sorted)
        {
            sum += ft;
            sumSq += ft * ft;
        }
        var mean = sum / n;
        var variance = Math.Max(0, sumSq / n - mean * mean);
        var median = Percentile(sorted, 0.5);
        var p99 = Percentile(sorted, 0.99);
        var p999 = Percentile(sorted, 0.999);

        long stutters = 0;
        for (var i = n - 1; i >= 0 && sorted[i] > 2 * median; i--) stutters++;

        summary.MeasuredSeconds = sum / 1000.0;
        summary.AverageFps = n * 1000.0 / sum;
        summary.MinimumFps = 1000.0 / sorted[n - 1];
        summary.MaximumFps = 1000.0 / sorted[0];
        summary.AverageFrameTimeMs = mean;
        summary.MedianFrameTimeMs = median;
        summary.P99FrameTimeMs = p99;
        summary.FrameTimeStdDevMs = Math.Sqrt(variance);
        summary.OnePercentLowFps = n >= MinFramesForOnePercentLow ? 1000.0 / p99 : null;
        summary.PointOnePercentLowFps = n >= MinFramesForPointOnePercentLow ? 1000.0 / p999 : null;
        summary.StutterFrameCount = stutters;
        summary.Stability = Rate(n, mean, summary.FrameTimeStdDevMs);
        return summary;
    }

    /// <summary>Stability from the coefficient of variation of frame times.</summary>
    public static StabilityRating Rate(long frames, double meanMs, double stdDevMs)
    {
        if (frames < MinFramesForStability || meanMs <= 0) return StabilityRating.NotEnoughData;
        var cov = stdDevMs / meanMs;
        return cov switch
        {
            < 0.08 => StabilityRating.Excellent,
            < 0.15 => StabilityRating.Good,
            < 0.25 => StabilityRating.Fair,
            _ => StabilityRating.Unstable,
        };
    }

    public static string Describe(StabilityRating rating) => rating switch
    {
        StabilityRating.Excellent => "Excellent — frame pacing is very consistent",
        StabilityRating.Good => "Good — minor frame-time variation",
        StabilityRating.Fair => "Fair — noticeable frame-time variation",
        StabilityRating.Unstable => "Unstable — large frame-time spikes (stutter)",
        _ => "Not enough frames yet",
    };
}

/// <summary>
/// Bounded-memory accumulator for long sessions: a 0.01 ms-resolution histogram up to 1 s plus an exact
/// overflow list. Percentiles are therefore accurate to 0.01 ms regardless of session length.
/// </summary>
public sealed class FrameTimeHistogram
{
    private const double BucketMs = 0.01;
    private const int BucketCount = 100_000; // covers 0 – 1000 ms
    private readonly int[] _buckets = new int[BucketCount];
    private readonly List<double> _overflow = new();
    private readonly double _maxValid;
    private double _sum, _sumSq, _min = double.MaxValue, _max;
    private long _count, _ignored;

    public FrameTimeHistogram(double maxValidFrameTimeMs = 2000) => _maxValid = maxValidFrameTimeMs;

    public long Count => _count;

    public void Add(double frameTimeMs)
    {
        if (!FrameStatistics.IsValid(frameTimeMs, _maxValid))
        {
            _ignored++;
            return;
        }
        _count++;
        _sum += frameTimeMs;
        _sumSq += frameTimeMs * frameTimeMs;
        if (frameTimeMs < _min) _min = frameTimeMs;
        if (frameTimeMs > _max) _max = frameTimeMs;
        var bucket = (int)(frameTimeMs / BucketMs);
        if (bucket < BucketCount) _buckets[bucket]++;
        else _overflow.Add(frameTimeMs);
    }

    public void Reset()
    {
        Array.Clear(_buckets);
        _overflow.Clear();
        _sum = _sumSq = _max = 0;
        _min = double.MaxValue;
        _count = _ignored = 0;
    }

    /// <summary>Nearest-rank percentile (bucket midpoint, clamped to the true min/max).</summary>
    public double Percentile(double p)
    {
        if (_count == 0) return double.NaN;
        var target = Math.Clamp((long)Math.Ceiling(p * _count), 1, _count);
        long cumulative = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            cumulative += _buckets[i];
            if (cumulative >= target) return Math.Clamp((i + 0.5) * BucketMs, _min, _max);
        }
        var sortedOverflow = _overflow.OrderBy(x => x).ToList();
        var idx = (int)(target - cumulative - 1);
        return sortedOverflow[Math.Clamp(idx, 0, sortedOverflow.Count - 1)];
    }

    public FrameStatsSummary ToSummary(string source)
    {
        var summary = new FrameStatsSummary { FrameCount = _count, IgnoredFrameCount = _ignored, Source = source };
        if (_count == 0)
        {
            summary.Stability = StabilityRating.NotEnoughData;
            return summary;
        }
        var mean = _sum / _count;
        var std = Math.Sqrt(Math.Max(0, _sumSq / _count - mean * mean));
        var median = Percentile(0.5);
        var p99 = Percentile(0.99);
        var p999 = Percentile(0.999);

        long stutters = _overflow.Count(x => x > 2 * median);
        var startBucket = (int)Math.Floor(2 * median / BucketMs) + 1;
        for (var i = Math.Max(0, startBucket); i < BucketCount; i++) stutters += _buckets[i];

        summary.MeasuredSeconds = _sum / 1000.0;
        summary.AverageFps = _count * 1000.0 / _sum;
        summary.MinimumFps = 1000.0 / _max;
        summary.MaximumFps = 1000.0 / _min;
        summary.AverageFrameTimeMs = mean;
        summary.MedianFrameTimeMs = median;
        summary.P99FrameTimeMs = p99;
        summary.FrameTimeStdDevMs = std;
        summary.OnePercentLowFps = _count >= FrameStatistics.MinFramesForOnePercentLow ? 1000.0 / p99 : null;
        summary.PointOnePercentLowFps = _count >= FrameStatistics.MinFramesForPointOnePercentLow ? 1000.0 / p999 : null;
        summary.StutterFrameCount = stutters;
        summary.Stability = FrameStatistics.Rate(_count, mean, std);
        return summary;
    }
}
