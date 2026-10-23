using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Mvvm;
using FPSHubRTX.Core.Services;
using OL = FPSHubRTX.Core.Models.OverlayLayout;
using OC = FPSHubRTX.Core.Models.OverlayCorner;

namespace FPSHubRTX.ViewModels;

public sealed class OverlayMetricToggle : ObservableObject
{
    private readonly Action _changed;
    private bool _enabled;

    public OverlayMetricToggle(OverlayMetric metric, string label, bool enabled, Action changed)
    {
        Metric = metric;
        Label = label;
        _enabled = enabled;
        _changed = changed;
    }

    public OverlayMetric Metric { get; }
    public string Label { get; }
    public bool IsEnabled { get => _enabled; set { if (Set(ref _enabled, value)) _changed(); } }
}

/// <summary>All settings. Changes apply immediately and are saved automatically.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IPlatformUi _ui;
    private readonly IShell _shell;
    private readonly Action _settingsApplied;
    private string _hotkeyStatus = "", _updateStatus = "", _presentMonStatus = "", _healthSummary = "";
    private UpdateInfo? _update;

    public SettingsViewModel(AppHost host, IPlatformUi ui, IShell shell, Action settingsApplied, Action toggleOverlayEditMode)
    {
        _host = host;
        _ui = ui;
        _shell = shell;
        _settingsApplied = settingsApplied;

        SetAccentCommand = new RelayCommand(p => { if (p is string hex) AccentColor = hex; });
        ApplyHotkeysCommand = new RelayCommand(() => settingsApplied());
        EditOverlayPositionCommand = new RelayCommand(toggleOverlayEditMode);
        BrowsePresentMonCommand = new RelayCommand(BrowsePresentMon);
        ClearPresentMonCommand = new RelayCommand(() => PresentMonPath = "");
        FixPermissionsCommand = new RelayCommand(() => shell.Navigate(Pages.Fps));
        BackupCommand = new RelayCommand(Backup);
        RestoreCommand = new RelayCommand(Restore);
        OpenDataFolderCommand = new RelayCommand(() => { if (!ShellService.OpenFolder(_host.Paths.DataDirectory, out var e)) _shell.ShowBanner(e ?? "", true); });
        OpenLogsCommand = new RelayCommand(() => { if (!ShellService.OpenFolder(_host.Paths.LogsDirectory, out var e)) _shell.ShowBanner(e ?? "", true); });
        RunHealthCheckCommand = new RelayCommand(RunHealthCheck);
        RefreshStatusesCommand = new RelayCommand(() =>
        {
            var changed = _host.Library.RefreshStatuses();
            _shell.ShowBanner($"Install folders re-checked ({changed} change{(changed == 1 ? "" : "s")}).");
            RunHealthCheck();
        });
        DiagnosticReportCommand = new RelayCommand(CreateDiagnosticReport);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, null, ex => UpdateStatus = ex.Message);
        OpenUpdateCommand = new RelayCommand(() => { if (_update is not null) ShellService.OpenUri(_update.Url, out _); }, () => _update is not null);
        ResetSettingsCommand = new RelayCommand(ResetSettings);
        DownloadCoversNowCommand = new AsyncRelayCommand(DownloadCoversAsync, () => DownloadCoverArt, ex => _shell.ShowBanner(ex.Message, true));
        ExportActivityCommand = new RelayCommand(() => shell.Navigate(Pages.Sessions));

        foreach (var (metric, label) in new[]
                 {
                     (OverlayMetric.Fps, "FPS"), (OverlayMetric.AverageFps, "Average FPS"), (OverlayMetric.OnePercentLow, "1% low"),
                     (OverlayMetric.FrameTime, "Frame time"), (OverlayMetric.Cpu, "CPU %"), (OverlayMetric.Gpu, "GPU %"), (OverlayMetric.Ram, "RAM"),
                     (OverlayMetric.GpuTemperature, "GPU temperature"), (OverlayMetric.VramUsage, "GPU memory"), (OverlayMetric.SessionTime, "Session time"),
                 })
        {
            OverlayMetrics.Add(new OverlayMetricToggle(metric, label, S.Overlay.FullMetrics.Contains(metric), SaveOverlayMetrics));
        }
        RefreshPresentMonStatus();
    }

    private AppSettings S => _host.S;

    private void Changed(bool apply = false, [CallerMemberName] string? name = null)
    {
        _host.Settings.NotifyChanged();
        OnPropertyChanged(name);
        if (apply) _settingsApplied();
    }

    // ---------------- Appearance ----------------
    public IReadOnlyList<string> AccentPresets { get; } = new[] { "#8B5CF6", "#A855F7", "#6366F1", "#3B82F6", "#06B6D4", "#22D3EE", "#10B981", "#F43F5E" };
    public bool IsDarkTheme { get => S.Theme == AppTheme.Dark; set { S.Theme = value ? AppTheme.Dark : AppTheme.Light; Changed(apply: true); Raise(nameof(IsLightTheme)); } }
    public bool IsLightTheme { get => S.Theme == AppTheme.Light; set => IsDarkTheme = !value; }
    public string AccentColor
    {
        get => S.AccentColor;
        set
        {
            if (!SettingsService.IsHexColor(value)) return;
            S.AccentColor = value;
            Changed(apply: true);
        }
    }
    public bool EnableAnimations { get => S.EnableAnimations; set { S.EnableAnimations = value; Changed(apply: true); } }

    // ---------------- Startup ----------------
    public bool StartWithWindows { get => S.StartWithWindows; set { S.StartWithWindows = value; Changed(apply: true); } }
    public bool StartMinimized { get => S.StartMinimized; set { S.StartMinimized = value; Changed(apply: true); } }
    public bool MinimizeToTray { get => S.MinimizeToTray; set { S.MinimizeToTray = value; Changed(); } }
    public bool CloseToTray { get => S.CloseToTray; set { S.CloseToTray = value; Changed(); } }
    public bool ShowSplashScreen { get => S.ShowSplashScreen; set { S.ShowSplashScreen = value; Changed(); } }

    // ---------------- Overlay ----------------
    public bool OverlayEnabled { get => S.Overlay.Enabled; set { S.Overlay.Enabled = value; Changed(apply: true); } }
    public bool OverlayAutoShow { get => S.Overlay.ShowAutomaticallyWhenGameRuns; set { S.Overlay.ShowAutomaticallyWhenGameRuns = value; Changed(); } }
    public IReadOnlyList<Option<OL>> OverlayLayouts { get; } = new[]
    {
        new Option<OL>(OL.FpsOnly, "FPS only"),
        new Option<OL>(OL.Compact, "Compact (FPS, 1% low, frame time)"),
        new Option<OL>(OL.Full, "Full performance"),
        new Option<OL>(OL.Graph, "Graph (FPS + frame-time graph)"),
    };
    public Option<OL> OverlayLayout
    {
        get => OverlayLayouts.First(o => o.Value == S.Overlay.Layout);
        set { if (value is null) return; S.Overlay.Layout = value.Value; Changed(apply: true); }
    }
    public IReadOnlyList<Option<OC>> OverlayCorners { get; } = new[]
    {
        new Option<OC>(OC.TopLeft, "Top left"),
        new Option<OC>(OC.TopRight, "Top right"),
        new Option<OC>(OC.BottomLeft, "Bottom left"),
        new Option<OC>(OC.BottomRight, "Bottom right"),
        new Option<OC>(OC.Custom, "Custom (drag in edit mode)"),
    };
    public Option<OC> OverlayCorner
    {
        get => OverlayCorners.First(o => o.Value == S.Overlay.Corner);
        set { if (value is null) return; S.Overlay.Corner = value.Value; Changed(apply: true); }
    }
    public double OverlayFontSize { get => S.Overlay.FontSize; set { S.Overlay.FontSize = Math.Clamp(Math.Round(value), 8, 48); Changed(apply: true); } }
    public double OverlayOpacity { get => S.Overlay.BackgroundOpacity; set { S.Overlay.BackgroundOpacity = Math.Clamp(value, 0, 1); Changed(apply: true); } }
    public string OverlayTextColor { get => S.Overlay.TextColor; set { if (!SettingsService.IsHexColor(value)) return; S.Overlay.TextColor = value; Changed(apply: true); } }
    public string OverlayAccentColor { get => S.Overlay.AccentColor; set { if (!SettingsService.IsHexColor(value)) return; S.Overlay.AccentColor = value; Changed(apply: true); } }
    public bool OverlayAlwaysOnTop { get => S.Overlay.AlwaysOnTop; set { S.Overlay.AlwaysOnTop = value; Changed(apply: true); } }
    public string OverlayHotkey { get => S.Overlay.ToggleHotkey; set { S.Overlay.ToggleHotkey = value?.Trim() ?? ""; Changed(); } }
    public string OverlayEditHotkey { get => S.Overlay.EditModeHotkey; set { S.Overlay.EditModeHotkey = value?.Trim() ?? ""; Changed(); } }
    public string HotkeyStatus { get => _hotkeyStatus; set => Set(ref _hotkeyStatus, value); }
    public ObservableCollection<OverlayMetricToggle> OverlayMetrics { get; } = new();

    private void SaveOverlayMetrics()
    {
        S.Overlay.FullMetrics = OverlayMetrics.Where(m => m.IsEnabled).Select(m => m.Metric).ToList();
        _host.Settings.NotifyChanged();
        _settingsApplied();
    }

    // ---------------- FPS ----------------
    public bool AutoCaptureFps { get => S.Fps.AutoCaptureOnGameStart; set { S.Fps.AutoCaptureOnGameStart = value; Changed(); } }
    public string PresentMonPath { get => S.Fps.PresentMonPath ?? ""; set { S.Fps.PresentMonPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); Changed(); RefreshPresentMonStatus(); } }
    public string PresentMonStatus { get => _presentMonStatus; private set => Set(ref _presentMonStatus, value); }
    public bool PreferDisplayedFrames { get => S.Fps.PreferDisplayedFrames; set { S.Fps.PreferDisplayedFrames = value; Changed(); } }
    public double MaxValidFrameTimeMs { get => S.Fps.MaxValidFrameTimeMs; set { S.Fps.MaxValidFrameTimeMs = Math.Clamp(Math.Round(value), 100, 10_000); Changed(); } }
    public double GraphWindowSeconds { get => S.Fps.GraphWindowSeconds; set { S.Fps.GraphWindowSeconds = (int)Math.Clamp(value, 5, 300); Changed(); } }
    public double NoFramesTimeoutSeconds { get => S.Fps.NoFramesTimeoutSeconds; set { S.Fps.NoFramesTimeoutSeconds = (int)Math.Clamp(value, 5, 120); Changed(); } }

    // ---------------- Monitoring ----------------
    public IReadOnlyList<int> IntervalOptions { get; } = new[] { 250, 500, 1000, 2000, 5000 };
    public int SampleIntervalMs
    {
        get => S.Monitoring.SampleIntervalMs;
        set { S.Monitoring.SampleIntervalMs = value; if (_host.System.IsRunning) _host.System.Start(value); Changed(); }
    }
    public bool MinimalResourceMode { get => S.Monitoring.MinimalResourceMode; set { S.Monitoring.MinimalResourceMode = value; _host.System.MinimalMode = value; Changed(); } }
    public bool MonitorOnStartup { get => S.Monitoring.MonitorOnStartup; set { S.Monitoring.MonitorOnStartup = value; Changed(); } }

    // ---------------- Tracking ----------------
    public bool TrackPlaytime { get => S.TrackPlaytime; set { S.TrackPlaytime = value; Changed(); } }
    public bool TrackExternalGames { get => S.TrackExternallyLaunchedGames; set { S.TrackExternallyLaunchedGames = value; Changed(); } }

    // ---------------- Notifications ----------------
    public bool NotificationsEnabled { get => S.Notifications.Enabled; set { S.Notifications.Enabled = value; Changed(); } }
    public bool NotifyScanCompleted { get => S.Notifications.ScanCompleted; set { S.Notifications.ScanCompleted = value; Changed(); } }
    public bool NotifyNewGames { get => S.Notifications.NewGamesDiscovered; set { S.Notifications.NewGamesDiscovered = value; Changed(); } }
    public bool NotifyGameLaunched { get => S.Notifications.GameLaunched; set { S.Notifications.GameLaunched = value; Changed(); } }
    public bool NotifyLaunchFailed { get => S.Notifications.GameLaunchFailed; set { S.Notifications.GameLaunchFailed = value; Changed(); } }
    public bool NotifySessionEnded { get => S.Notifications.SessionEnded; set { S.Notifications.SessionEnded = value; Changed(); } }
    public bool NotifyBenchmark { get => S.Notifications.BenchmarkCompleted; set { S.Notifications.BenchmarkCompleted = value; Changed(); } }
    public bool NotifyThresholds { get => S.Notifications.PerformanceThreshold; set { S.Notifications.PerformanceThreshold = value; Changed(); } }
    public double FpsBelowThreshold { get => S.Notifications.FpsBelowThreshold; set { S.Notifications.FpsBelowThreshold = Math.Clamp(Math.Round(value), 0, 1000); Changed(); } }
    public double CpuAboveThreshold { get => S.Notifications.CpuAboveThresholdPercent; set { S.Notifications.CpuAboveThresholdPercent = Math.Clamp(Math.Round(value), 0, 100); Changed(); } }
    public double GpuTempAboveThreshold { get => S.Notifications.GpuTemperatureAboveC; set { S.Notifications.GpuTemperatureAboveC = Math.Clamp(Math.Round(value), 0, 120); Changed(); } }
    public double ThresholdSeconds { get => S.Notifications.ThresholdSustainSeconds; set { S.Notifications.ThresholdSustainSeconds = (int)Math.Clamp(value, 2, 300); Changed(); } }

    // ---------------- Privacy ----------------
    public bool DownloadCoverArt
    {
        get => S.Privacy.DownloadCoverArt;
        set { S.Privacy.DownloadCoverArt = value; Changed(); ((AsyncRelayCommand)DownloadCoversNowCommand).RaiseCanExecuteChanged(); }
    }
    public bool IncludeTitlesInDiagnostics { get => S.Privacy.IncludeGameTitlesInDiagnostics; set { S.Privacy.IncludeGameTitlesInDiagnostics = value; Changed(); } }

    // ---------------- Updates ----------------
    public string UpdateFeedUrl
    {
        get => S.Updates.FeedUrl ?? "";
        set
        {
            var v = value?.Trim() ?? "";
            if (v.Length > 0 && !v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                UpdateStatus = "The update feed must start with https://";
                return;
            }
            S.Updates.FeedUrl = v.Length == 0 ? null : v;
            Changed();
        }
    }
    public bool CheckUpdatesOnStartup { get => S.Updates.CheckOnStartup; set { S.Updates.CheckOnStartup = value; Changed(); } }
    public string UpdateStatus { get => _updateStatus; set => Set(ref _updateStatus, value); }

    // ---------------- Library tools / about ----------------
    public ObservableCollection<HealthIssue> HealthIssues { get; } = new();
    public string HealthSummary { get => _healthSummary; private set => Set(ref _healthSummary, value); }
    public string VersionText => $"Version {UpdateChecker.CurrentVersion.ToString(3)}";
    public string DataFolderText => _host.Paths.DataDirectory;
    public string ElevationText => DiagnosticReport.IsElevated() ? "Running with administrator rights" : "Running without administrator rights (recommended)";

    public ICommand SetAccentCommand { get; }
    public ICommand ApplyHotkeysCommand { get; }
    public ICommand EditOverlayPositionCommand { get; }
    public ICommand BrowsePresentMonCommand { get; }
    public ICommand ClearPresentMonCommand { get; }
    public ICommand FixPermissionsCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand RunHealthCheckCommand { get; }
    public ICommand RefreshStatusesCommand { get; }
    public ICommand DiagnosticReportCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand OpenUpdateCommand { get; }
    public ICommand ResetSettingsCommand { get; }
    public ICommand DownloadCoversNowCommand { get; }
    public ICommand ExportActivityCommand { get; }

    private void RefreshPresentMonStatus()
    {
        var path = _host.PresentMonPath;
        PresentMonStatus = path is null
            ? "PresentMon not found — FPS can't be measured. Reinstall FPS Hub or choose PresentMon.exe."
            : $"Using {path}";
    }

    private void BrowsePresentMon()
    {
        var file = _ui.PickFile("Locate PresentMon console executable", "PresentMon (PresentMon*.exe)|PresentMon*.exe|Programs (*.exe)|*.exe");
        if (file is not null) PresentMonPath = file;
    }

    private void Backup()
    {
        var path = _ui.SaveFile("Back up library and statistics", "Zip archive (*.zip)|*.zip", $"FPSHubRTX-backup-{DateTime.Now:yyyyMMdd}.zip");
        if (path is null) return;
        try
        {
            _host.Settings.SaveNow();
            BackupService.CreateBackup(_host.Paths, path);
            _shell.ShowBanner($"Backup saved to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowBanner("Backup failed: " + ex.Message, true);
        }
    }

    private void Restore()
    {
        var path = _ui.PickFile("Restore a backup", "Zip archive (*.zip)|*.zip");
        if (path is null) return;
        if (!_ui.Confirm("Restore backup", "Replace the current library, settings and statistics with the backup?\n\nA safety backup of the current data is made first. FPS Hub will reload the data.")) return;
        try
        {
            var safety = BackupService.Restore(_host.Paths, path, _host.Log);
            _host.Library.Reload();
            _ui.ShowInfo("Backup restored", $"Your data was restored. A safety copy of the previous data was saved as:\n{safety}\n\nRestart FPS Hub to reload settings and statistics.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _shell.ShowBanner("Restore failed: " + ex.Message, true);
        }
    }

    public void RunHealthCheck()
    {
        var issues = LibraryHealth.Check(_host.Library.GetAll());
        HealthIssues.Clear();
        foreach (var i in issues) HealthIssues.Add(i);
        HealthSummary = issues.Count == 0
            ? "No problems found."
            : $"{issues.Count(i => i.Severity == HealthSeverity.Problem)} problem(s), {issues.Count(i => i.Severity == HealthSeverity.Warning)} warning(s), {issues.Count(i => i.Severity == HealthSeverity.Info)} note(s).";
    }

    private void CreateDiagnosticReport()
    {
        try
        {
            var report = DiagnosticReport.Build(_host.Paths, S, _host.Library.GetAll(), LibraryHealth.Check(_host.Library.GetAll()),
                _host.PresentMonPath, _host.Fps.GetSnapshot(), _host.FileLog?.RecentEntries ?? Array.Empty<LogEntry>());
            var file = Path.Combine(_host.Paths.ReportsDirectory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, report);
            ShellService.OpenFile(file, out _);
            _shell.ShowBanner("Diagnostic report created. It contains no user names, tokens" + (S.Privacy.IncludeGameTitlesInDiagnostics ? "." : " or game titles."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowBanner("Couldn't write the report: " + ex.Message, true);
        }
    }

    public async Task CheckUpdatesAsync()
    {
        UpdateStatus = "Checking…";
        var (update, message) = await UpdateChecker.CheckAsync(S.Updates.FeedUrl, CancellationToken.None);
        _update = update;
        UpdateStatus = message;
        ((RelayCommand)OpenUpdateCommand).RaiseCanExecuteChanged();
    }

    private async Task DownloadCoversAsync()
    {
        var n = await Task.Run(() => CoverArtService.DownloadMissingSteamCoversAsync(_host.Library, _host.Paths, _host.Log, CancellationToken.None));
        _shell.ShowBanner($"Downloaded {n} cover image(s).");
    }

    private void ResetSettings()
    {
        if (!_ui.Confirm("Reset settings", "Reset all settings to their defaults? Your library and statistics are kept.")) return;
        _host.Settings.Reset();
        foreach (var m in OverlayMetrics) m.IsEnabled = S.Overlay.FullMetrics.Contains(m.Metric);
        OnPropertyChanged(string.Empty);
        _settingsApplied();
        RefreshPresentMonStatus();
        _shell.ShowBanner("Settings were reset to defaults.");
    }
}
