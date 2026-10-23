using System.Text.Json.Serialization;

namespace FPSHubRTX.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<SessionEndReason>))]
public enum SessionEndReason
{
    /// <summary>Game process exited with exit code 0 (or exit code unavailable).</summary>
    Exited,
    /// <summary>Game process exited with a non-zero exit code. Not always a crash, but often is.</summary>
    NonZeroExitCode,
    /// <summary>FPS Hub was closed while the game was still running; the session was closed at that point.</summary>
    AppClosed,
}

[JsonConverter(typeof(JsonStringEnumConverter<FpsAvailability>))]
public enum FpsAvailability
{
    /// <summary>Frame capture was not attempted (disabled by the user or profile).</summary>
    NotCaptured,
    /// <summary>Real frame-presentation data was measured.</summary>
    Measured,
    /// <summary>Capture was attempted but no frames could be measured.</summary>
    Unavailable,
}

/// <summary>One play session of one game.</summary>
public sealed class GameSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public LauncherKind Launcher { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public SessionEndReason EndReason { get; set; }
    public int? ExitCode { get; set; }
    public bool LaunchedFromHub { get; set; }
    public FpsAvailability FpsAvailability { get; set; }
    public string? FpsUnavailableReason { get; set; }
    public FrameStatsSummary? Fps { get; set; }
    public double? AverageCpuPercent { get; set; }
    public double? AverageGpuPercent { get; set; }
    public double? PeakGameMemoryBytes { get; set; }

    [JsonIgnore]
    public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
}

/// <summary>Frame statistics computed from real PresentMon frame-time samples.</summary>
public sealed class FrameStatsSummary
{
    /// <summary>Number of valid frames used for the statistics.</summary>
    public long FrameCount { get; set; }

    /// <summary>Frames ignored because their frame time was outside the valid range (pauses, alt-tab).</summary>
    public long IgnoredFrameCount { get; set; }

    /// <summary>Sum of valid frame times, in seconds.</summary>
    public double MeasuredSeconds { get; set; }

    public double AverageFps { get; set; }
    public double MinimumFps { get; set; }
    public double MaximumFps { get; set; }

    /// <summary>1000 / 99th-percentile frame time. Null when fewer than <see cref="FrameStatistics.MinFramesForOnePercentLow"/> frames.</summary>
    public double? OnePercentLowFps { get; set; }

    /// <summary>1000 / 99.9th-percentile frame time. Null when fewer than <see cref="FrameStatistics.MinFramesForPointOnePercentLow"/> frames.</summary>
    public double? PointOnePercentLowFps { get; set; }

    public double AverageFrameTimeMs { get; set; }
    public double MedianFrameTimeMs { get; set; }
    public double P99FrameTimeMs { get; set; }
    public double FrameTimeStdDevMs { get; set; }

    /// <summary>Frames that took more than twice the median frame time.</summary>
    public long StutterFrameCount { get; set; }

    public StabilityRating Stability { get; set; }

    /// <summary>Which PresentMon metric the frame times came from (e.g. "MsBetweenPresents").</summary>
    public string Source { get; set; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter<StabilityRating>))]
public enum StabilityRating
{
    NotEnoughData,
    Excellent,
    Good,
    Fair,
    Unstable,
}
