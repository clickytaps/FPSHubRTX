using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Monitoring;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.Core.Services;

public enum NotificationKind
{
    ScanCompleted,
    NewGamesDiscovered,
    GameLaunched,
    GameLaunchFailed,
    SessionEnded,
    BenchmarkCompleted,
    PerformanceThreshold,
}

public enum NotificationLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>Displays a notification (implemented by the WPF app as a Windows notification via the tray icon).</summary>
public interface INotificationSink
{
    void Show(string title, string message, NotificationLevel level);
}

/// <summary>Routes notifications through the user's per-type preferences.</summary>
public sealed class NotificationCenter
{
    private readonly Func<NotificationSettings> _settings;
    private INotificationSink? _sink;

    public NotificationCenter(Func<NotificationSettings> settings) => _settings = settings;

    public void AttachSink(INotificationSink sink) => _sink = sink;

    public bool IsEnabled(NotificationKind kind)
    {
        var s = _settings();
        if (!s.Enabled) return false;
        return kind switch
        {
            NotificationKind.ScanCompleted => s.ScanCompleted,
            NotificationKind.NewGamesDiscovered => s.NewGamesDiscovered,
            NotificationKind.GameLaunched => s.GameLaunched,
            NotificationKind.GameLaunchFailed => s.GameLaunchFailed,
            NotificationKind.SessionEnded => s.SessionEnded,
            NotificationKind.BenchmarkCompleted => s.BenchmarkCompleted,
            NotificationKind.PerformanceThreshold => s.PerformanceThreshold,
            _ => false,
        };
    }

    public bool Notify(NotificationKind kind, string title, string message, NotificationLevel level = NotificationLevel.Info)
    {
        if (!IsEnabled(kind) || _sink is null) return false;
        _sink.Show(title, message, level);
        return true;
    }
}

/// <summary>
/// Raises a notification when a measured value crosses a user threshold for a sustained period,
/// with a cooldown so the user isn't spammed. Only real measurements are evaluated.
/// </summary>
public sealed class ThresholdMonitor
{
    private readonly NotificationCenter _center;
    private readonly Func<NotificationSettings> _settings;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, DateTime> _since = new();
    private readonly Dictionary<string, DateTime> _lastFired = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    public ThresholdMonitor(NotificationCenter center, Func<NotificationSettings> settings, Func<DateTime>? clock = null)
    {
        _center = center;
        _settings = settings;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public void Evaluate(FpsSnapshot? fps, SystemSnapshot? system)
    {
        var s = _settings();
        if (!s.Enabled || !s.PerformanceThreshold) return;
        var sustain = TimeSpan.FromSeconds(s.ThresholdSustainSeconds);

        if (s.FpsBelowThreshold > 0)
            Check("fps", fps?.HasLiveData == true && fps.CurrentFps < s.FpsBelowThreshold, sustain,
                $"FPS below {s.FpsBelowThreshold:0}", $"{fps?.GameName} has been under {s.FpsBelowThreshold:0} FPS for {s.ThresholdSustainSeconds}s (now {fps?.CurrentFps:0}).");
        if (s.CpuAboveThresholdPercent > 0)
            Check("cpu", system?.CpuPercent > s.CpuAboveThresholdPercent, sustain,
                "High CPU usage", $"CPU usage has been above {s.CpuAboveThresholdPercent:0}% for {s.ThresholdSustainSeconds}s.");
        if (s.GpuTemperatureAboveC > 0)
            Check("gputemp", system?.GpuTemperatureC > s.GpuTemperatureAboveC, sustain,
                "GPU temperature high", $"GPU temperature has been above {s.GpuTemperatureAboveC:0} °C for {s.ThresholdSustainSeconds}s (now {system?.GpuTemperatureC:0} °C).");
    }

    /// <summary>Returns true when a notification was fired (for tests).</summary>
    internal bool Check(string key, bool breached, TimeSpan sustain, string title, string message)
    {
        var now = _clock();
        if (!breached)
        {
            _since.Remove(key);
            return false;
        }
        if (!_since.TryGetValue(key, out var since))
        {
            _since[key] = now;
            return false;
        }
        if (now - since < sustain) return false;
        if (_lastFired.TryGetValue(key, out var last) && now - last < Cooldown) return false;
        _lastFired[key] = now;
        return _center.Notify(NotificationKind.PerformanceThreshold, title, message, NotificationLevel.Warning);
    }
}
