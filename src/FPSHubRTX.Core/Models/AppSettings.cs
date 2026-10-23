using System.Text.Json.Serialization;

namespace FPSHubRTX.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    Dark,
    Light,
}

[JsonConverter(typeof(JsonStringEnumConverter<OverlayCorner>))]
public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Custom,
}

/// <summary>All user settings. Persisted to settings.json in the data directory.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // ---- Appearance ----
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string AccentColor { get; set; } = "#8B5CF6";
    public bool EnableAnimations { get; set; } = true;

    // ---- Startup / window ----
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool ShowSplashScreen { get; set; } = true;

    // ---- Scanning ----
    public ScanSettings Scan { get; set; } = new();

    // ---- FPS / monitoring ----
    public FpsSettings Fps { get; set; } = new();
    public MonitoringSettings Monitoring { get; set; } = new();

    // ---- Overlay ----
    public OverlaySettings Overlay { get; set; } = new();

    // ---- Tracking ----
    public bool TrackPlaytime { get; set; } = true;
    /// <summary>Also create sessions for library games started outside FPS Hub (detected via running processes).</summary>
    public bool TrackExternallyLaunchedGames { get; set; } = true;

    // ---- Notifications ----
    public NotificationSettings Notifications { get; set; } = new();

    // ---- Privacy ----
    public PrivacySettings Privacy { get; set; } = new();

    // ---- Updates ----
    public UpdateSettings Updates { get; set; } = new();

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, Persistence.JsonDefaults.Options);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, Persistence.JsonDefaults.Options)!;
    }
}

public sealed class ScanSettings
{
    /// <summary>Launchers the scanner should check. Empty set is treated as "all".</summary>
    public HashSet<LauncherKind> EnabledLaunchers { get; set; } = new(Enum.GetValues<LauncherKind>().Where(k => k != LauncherKind.Manual));

    /// <summary>Extra folders chosen by the user that contain games (one game per subfolder, or a game folder itself).</summary>
    public List<string> CustomFolders { get; set; } = new();

    /// <summary>Folders (and everything below them) the scanner must never look at.</summary>
    public List<string> ExcludedDirectories { get; set; } = new();

    /// <summary>Check "X:\Games" and "X:\XboxGames" on every fixed drive.</summary>
    public bool ScanCommonGameFolders { get; set; } = true;

    /// <summary>Include removable/external drives when checking common game folders.</summary>
    public bool IncludeRemovableDrives { get; set; }

    /// <summary>How deep the scanner looks for an executable inside a standalone game folder.</summary>
    public int ExecutableSearchDepth { get; set; } = 4;

    /// <summary>Mark games that disappeared from a launcher as Missing (they are never removed automatically).</summary>
    public bool MarkMissingGames { get; set; } = true;
}

public sealed class FpsSettings
{
    /// <summary>Optional explicit path to PresentMon console exe; otherwise the bundled copy is used.</summary>
    public string? PresentMonPath { get; set; }

    /// <summary>Start frame capture automatically when a library game starts.</summary>
    public bool AutoCaptureOnGameStart { get; set; } = true;

    /// <summary>Frame times above this (ms) are treated as pauses (loading, alt-tab) and excluded from statistics.</summary>
    public double MaxValidFrameTimeMs { get; set; } = 2000;

    /// <summary>Seconds of frame-time history shown in live graphs.</summary>
    public int GraphWindowSeconds { get; set; } = 30;

    /// <summary>Use "displayed" frame timing (MsBetweenDisplayChange) instead of presented frames when available.</summary>
    public bool PreferDisplayedFrames { get; set; }

    /// <summary>Seconds to wait for the first frame before reporting "no frames detected".</summary>
    public int NoFramesTimeoutSeconds { get; set; } = 15;
}

public sealed class MonitoringSettings
{
    public int SampleIntervalMs { get; set; } = 1000;
    public bool MinimalResourceMode { get; set; }
    public bool MonitorOnStartup { get; set; } = true;
    public int HistoryPoints { get; set; } = 120;
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public bool ShowAutomaticallyWhenGameRuns { get; set; } = true;
    public OverlayLayout Layout { get; set; } = OverlayLayout.Compact;
    public double FontSize { get; set; } = 14;
    public string TextColor { get; set; } = "#E5E7EB";
    public string AccentColor { get; set; } = "#22D3EE";
    public string BackgroundColor { get; set; } = "#000000";
    public double BackgroundOpacity { get; set; } = 0.45;
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;
    public double CustomLeft { get; set; } = 24;
    public double CustomTop { get; set; } = 24;
    public int ScreenIndex { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+F10";
    public string EditModeHotkey { get; set; } = "Ctrl+Shift+F11";
    public List<OverlayMetric> FullMetrics { get; set; } = new()
    {
        OverlayMetric.Fps, OverlayMetric.AverageFps, OverlayMetric.OnePercentLow, OverlayMetric.FrameTime,
        OverlayMetric.Cpu, OverlayMetric.Gpu, OverlayMetric.Ram,
    };
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool ScanCompleted { get; set; } = true;
    public bool NewGamesDiscovered { get; set; } = true;
    public bool GameLaunched { get; set; } = true;
    public bool GameLaunchFailed { get; set; } = true;
    public bool SessionEnded { get; set; } = true;
    public bool BenchmarkCompleted { get; set; } = true;
    public bool PerformanceThreshold { get; set; }

    /// <summary>Notify when measured FPS stays below this value for <see cref="ThresholdSustainSeconds"/>. 0 = off.</summary>
    public double FpsBelowThreshold { get; set; }
    public double CpuAboveThresholdPercent { get; set; } = 95;
    public double GpuTemperatureAboveC { get; set; } = 85;
    public int ThresholdSustainSeconds { get; set; } = 10;
}

public sealed class PrivacySettings
{
    /// <summary>
    /// Download missing cover art from Steam's public CDN. Off by default because it reveals
    /// which Steam app ids are installed to that CDN.
    /// </summary>
    public bool DownloadCoverArt { get; set; }

    /// <summary>Include game titles in diagnostic reports (off by default).</summary>
    public bool IncludeGameTitlesInDiagnostics { get; set; }
}

public sealed class UpdateSettings
{
    public bool CheckOnStartup { get; set; }

    /// <summary>HTTPS URL of a JSON update manifest: {"version":"1.2.0","url":"https://…","notes":"…"}.</summary>
    public string? FeedUrl { get; set; }
}
