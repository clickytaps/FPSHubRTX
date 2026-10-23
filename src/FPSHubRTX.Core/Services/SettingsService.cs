using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Persistence;

namespace FPSHubRTX.Core.Services;

/// <summary>Loads, validates and saves <see cref="AppSettings"/>. Saving is debounced so sliders don't hammer the disk.</summary>
public sealed class SettingsService : IDisposable
{
    private readonly JsonFileStore<AppSettings> _store;
    private readonly ILog _log;
    private readonly Timer _debounce;
    private readonly object _gate = new();

    public SettingsService(string path, ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
        _store = new JsonFileStore<AppSettings>(path, () => new AppSettings(), _log);
        Current = Validate(_store.Load());
        _debounce = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Call after changing <see cref="Current"/>; saves after 500 ms of quiet.</summary>
    public void NotifyChanged()
    {
        _debounce.Change(500, Timeout.Infinite);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveNow()
    {
        lock (_gate)
        {
            try
            {
                _store.Save(Current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error("Could not save settings", ex);
            }
        }
    }

    public void Reset()
    {
        Current = new AppSettings();
        SaveNow();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clamps out-of-range values from a hand-edited or older settings file.</summary>
    public static AppSettings Validate(AppSettings s)
    {
        s.Scan ??= new ScanSettings();
        s.Fps ??= new FpsSettings();
        s.Monitoring ??= new MonitoringSettings();
        s.Overlay ??= new OverlaySettings();
        s.Notifications ??= new NotificationSettings();
        s.Privacy ??= new PrivacySettings();
        s.Updates ??= new UpdateSettings();
        s.Scan.CustomFolders ??= new List<string>();
        s.Scan.ExcludedDirectories ??= new List<string>();
        s.Scan.EnabledLaunchers ??= new HashSet<LauncherKind>();
        s.Scan.ExecutableSearchDepth = Math.Clamp(s.Scan.ExecutableSearchDepth, 1, 8);
        s.Monitoring.SampleIntervalMs = Math.Clamp(s.Monitoring.SampleIntervalMs, 250, 10_000);
        s.Monitoring.HistoryPoints = Math.Clamp(s.Monitoring.HistoryPoints, 30, 600);
        s.Fps.MaxValidFrameTimeMs = Math.Clamp(s.Fps.MaxValidFrameTimeMs, 100, 10_000);
        s.Fps.GraphWindowSeconds = Math.Clamp(s.Fps.GraphWindowSeconds, 5, 300);
        s.Fps.NoFramesTimeoutSeconds = Math.Clamp(s.Fps.NoFramesTimeoutSeconds, 5, 120);
        s.Overlay.FontSize = Math.Clamp(s.Overlay.FontSize, 8, 48);
        s.Overlay.BackgroundOpacity = Math.Clamp(s.Overlay.BackgroundOpacity, 0, 1);
        s.Overlay.FullMetrics ??= new List<OverlayMetric>();
        s.Notifications.ThresholdSustainSeconds = Math.Clamp(s.Notifications.ThresholdSustainSeconds, 2, 300);
        if (!IsHexColor(s.AccentColor)) s.AccentColor = "#8B5CF6";
        if (!IsHexColor(s.Overlay.TextColor)) s.Overlay.TextColor = "#E5E7EB";
        if (!IsHexColor(s.Overlay.AccentColor)) s.Overlay.AccentColor = "#22D3EE";
        if (!IsHexColor(s.Overlay.BackgroundColor)) s.Overlay.BackgroundColor = "#000000";
        if (s.Updates.FeedUrl is { Length: > 0 } url && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s.Updates.FeedUrl = null;
        return s;
    }

    public static bool IsHexColor(string? c)
        => c is { Length: 7 or 9 } && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit);

    public void Dispose()
    {
        _debounce.Dispose();
        SaveNow();
    }
}
