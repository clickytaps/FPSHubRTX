using System.Collections.ObjectModel;
using System.Windows.Input;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Monitoring;
using FPSHubRTX.Core.Mvvm;
using FPSHubRTX.Core.Services;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.ViewModels;

public sealed record RunningGameItem(string GameId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>FPS Monitor page. Shows only real PresentMon measurements, or an explicit "unavailable" state.</summary>
public sealed class FpsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IPlatformUi _ui;
    private readonly IShell _shell;
    private RunningGameItem? _selected;
    private FpsSnapshot _snapshot = FpsSnapshot.Idle("No game running", "Launch a game from your library — FPS measuring starts automatically.");
    private string _permissionStatus = "";

    public FpsViewModel(AppHost host, IPlatformUi ui, IShell shell)
    {
        _host = host;
        _ui = ui;
        _shell = shell;
        StartCaptureCommand = new RelayCommand(() => Start(false), () => Selected is not null);
        StartElevatedCaptureCommand = new RelayCommand(StartElevated, () => Selected is not null);
        StopCaptureCommand = new RelayCommand(() => _host.Fps.Stop(userInitiated: true), () => _host.Fps.IsActive);
        FixPermissionsCommand = new RelayCommand(FixPermissions);
        OpenSettingsCommand = new RelayCommand(() => shell.Navigate(Pages.Settings));
        RefreshPermissionStatus();
    }

    public ObservableCollection<RunningGameItem> RunningGames { get; } = new();

    public RunningGameItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                ((RelayCommand)StartCaptureCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StartElevatedCaptureCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public FpsSnapshot Snapshot => _snapshot;
    public string StateTitle => _snapshot.StateTitle;
    public string? StateDetail => _snapshot.StateDetail;
    public string GameName => _snapshot.GameName ?? "No game selected";
    public bool IsMeasuring => _snapshot.State == FpsCaptureState.Measuring;
    public bool IsUnavailable => _snapshot.State is FpsCaptureState.NoFrames or FpsCaptureState.AccessDenied or FpsCaptureState.NotInstalled or FpsCaptureState.Failed;
    public bool IsWaiting => _snapshot.State is FpsCaptureState.WaitingForFrames or FpsCaptureState.Paused;
    public bool IsAccessDenied => _snapshot.State == FpsCaptureState.AccessDenied;

    public string CurrentFps => Format.Fps(_snapshot.CurrentFps);
    public string AverageFps => Format.Fps(_snapshot.Session?.AverageFps);
    public string MinFps => Format.Fps(_snapshot.Session?.MinimumFps);
    public string MaxFps => Format.Fps(_snapshot.Session?.MaximumFps);
    public string OnePercentLow => _snapshot.Session is null ? Format.NoData
        : _snapshot.Session.OnePercentLowFps is { } v ? Format.Fps(v) : $"needs {FrameStatistics.MinFramesForOnePercentLow}+ frames";
    public string PointOnePercentLow => _snapshot.Session is null ? Format.NoData
        : _snapshot.Session.PointOnePercentLowFps is { } v ? Format.Fps(v) : $"needs {FrameStatistics.MinFramesForPointOnePercentLow}+ frames";
    public string FrameTime => Format.Ms(_snapshot.CurrentFrameTimeMs);
    public string AverageFrameTime => Format.Ms(_snapshot.Session?.AverageFrameTimeMs);
    public string P99FrameTime => Format.Ms(_snapshot.Session?.P99FrameTimeMs);
    public string SessionDuration => _snapshot.Elapsed == TimeSpan.Zero ? Format.NoData : Format.Clock(_snapshot.Elapsed);
    public string Stability => _snapshot.Session is null ? "Not enough frames yet" : FrameStatistics.Describe(_snapshot.Session.Stability);
    public string FrameCount => _snapshot.Session is null ? "0" : _snapshot.Session.FrameCount.ToString("N0");
    public string StutterCount => _snapshot.Session is null ? Format.NoData : _snapshot.Session.StutterFrameCount.ToString("N0");
    public string PresentInfo => _snapshot.PresentRuntime is null ? Format.NoData : $"{_snapshot.PresentRuntime} · {_snapshot.PresentMode}";
    public string MetricSource => string.IsNullOrEmpty(_snapshot.MetricSource) ? "PresentMon frame-presentation events" : "PresentMon · " + _snapshot.MetricSource;
    public IReadOnlyList<double> FrameTimeSeries => _snapshot.FrameTimeHistory;
    public IReadOnlyList<double> FpsSeries => _snapshot.FpsHistory;
    public bool PresentMonAvailable => _host.PresentMonPath is not null;
    public string PermissionStatus { get => _permissionStatus; private set => Set(ref _permissionStatus, value); }

    public IReadOnlyList<string> TroubleshootingTips { get; } = new[]
    {
        "Make sure the game is actually rendering (not minimised or sitting in its launcher window).",
        "If the game runs through a separate process (e.g. a '-Win64-Shipping.exe'), choose that executable in the game's settings.",
        "Windows must let PresentMon read presentation events: add your account to 'Performance Log Users' (button below, one-time, sign out afterwards) or use 'Capture with admin rights'.",
        "Games streamed from the cloud, games in some sandboxed/protected modes and video players may not expose presentation events.",
        "Vulkan and OpenGL games are measured when they present through the Windows display stack, which is the common case on Windows 10/11.",
    };

    public ICommand StartCaptureCommand { get; }
    public ICommand StartElevatedCaptureCommand { get; }
    public ICommand StopCaptureCommand { get; }
    public ICommand FixPermissionsCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public void UpdateRunningGames()
    {
        var running = _host.Running.Running.Select(r => new RunningGameItem(r.GameId, r.GameName)).ToList();
        if (!RunningGames.SequenceEqual(running))
        {
            RunningGames.Clear();
            foreach (var r in running) RunningGames.Add(r);
        }
        if (Selected is null || !RunningGames.Contains(Selected))
        {
            var target = _host.Fps.TargetGameId;
            Selected = RunningGames.FirstOrDefault(r => r.GameId == target) ?? RunningGames.FirstOrDefault();
        }
    }

    public void Update(FpsSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (snapshot.State == FpsCaptureState.Idle && RunningGames.Count == 0)
            _snapshot = FpsSnapshot.Idle("No game running", "Launch a game from your library — FPS measuring starts automatically.");
        OnPropertyChanged(string.Empty);
        ((RelayCommand)StopCaptureCommand).RaiseCanExecuteChanged();
    }

    private void Start(bool elevated)
    {
        if (Selected is null) return;
        var rg = _host.Running.Running.FirstOrDefault(r => r.GameId == Selected.GameId);
        var game = _host.Library.Get(Selected.GameId);
        if (rg is null || game is null)
        {
            _shell.ShowBanner("That game is no longer running.", true);
            return;
        }
        _host.Tracker.StartCapture(rg, game, elevated);
        Update(_host.Fps.GetSnapshot());
    }

    private void StartElevated()
    {
        if (!_ui.Confirm("Capture with administrator rights",
                "PresentMon will be started with administrator rights so it can read Windows' frame-presentation events.\n\n" +
                "Windows will show a permission prompt. Stopping an elevated capture early also needs a prompt; otherwise it stops by itself when the game closes.\n\n" +
                "Tip: adding your account to 'Performance Log Users' avoids this prompt permanently.")) return;
        Start(true);
    }

    private void FixPermissions()
    {
        if (!_ui.Confirm("Allow FPS measurement without admin rights",
                "FPS Hub will ask Windows to add your account to the built-in 'Performance Log Users' group. This lets PresentMon read frame-presentation events without administrator rights.\n\n" +
                "Windows will show one permission prompt. Sign out and back in afterwards for it to take effect.")) return;
        if (DiagnosticReport.RequestPerformanceLogUsersMembership(out var error))
            _ui.ShowInfo("Done", "Your account was added to 'Performance Log Users'. Sign out of Windows and back in, then FPS can be measured without administrator rights.");
        else
            _shell.ShowBanner("Couldn't change group membership: " + error, true);
        RefreshPermissionStatus();
    }

    public void RefreshPermissionStatus()
    {
        PermissionStatus = DiagnosticReport.IsElevated()
            ? "FPS Hub is running as administrator — PresentMon has the access it needs."
            : DiagnosticReport.IsInPerformanceLogUsers()
                ? "Your account is in 'Performance Log Users' — no admin rights needed."
                : "Your account is not in 'Performance Log Users'. FPS capture may report 'permission needed' until you fix this (one-time).";
    }
}

public sealed class MetricTile : ObservableObject
{
    private string _value = Format.NoData, _caption = "", _note = "";
    private bool _available;
    private double _fraction;

    public MetricTile(string title, string icon)
    {
        Title = title;
        Icon = icon;
    }

    public string Title { get; }
    public string Icon { get; }
    public string Value { get => _value; private set => Set(ref _value, value); }
    public string Caption { get => _caption; private set => Set(ref _caption, value); }
    /// <summary>Why a metric is unavailable, or where it comes from.</summary>
    public string Note { get => _note; private set => Set(ref _note, value); }
    public bool IsAvailable { get => _available; private set => Set(ref _available, value); }
    /// <summary>0..1 for gauge bars; 0 when unavailable.</summary>
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }

    public void SetValue(string value, string caption = "", double? fraction = null, string note = "")
    {
        Value = value;
        Caption = caption;
        Note = note;
        IsAvailable = true;
        Fraction = Math.Clamp(fraction ?? 0, 0, 1);
    }

    public void SetUnavailable(string note)
    {
        Value = Format.NoData;
        Caption = "Unavailable";
        Note = note;
        IsAvailable = false;
        Fraction = 0;
    }
}

public sealed record DriveRow(string Root, string Label, string FreeText, double UsedFraction);

/// <summary>Live CPU/GPU/memory/disk/network dashboard with configurable sampling.</summary>
public sealed class PerformanceViewModel : ObservableObject
{
    private readonly AppHost _host;
    private string _foreground = Format.NoData, _gameName = "No game running";
    private IReadOnlyList<double> _cpuHistory = Array.Empty<double>(), _gpuHistory = Array.Empty<double>(), _ramHistory = Array.Empty<double>(), _fpsHistory = Array.Empty<double>();

    public PerformanceViewModel(AppHost host)
    {
        _host = host;
        StartCommand = new RelayCommand(() => { _host.System.Start(_host.S.Monitoring.SampleIntervalMs); Raise(nameof(IsMonitoring)); }, () => !IsMonitoring);
        StopCommand = new RelayCommand(() => { _host.System.Stop(); Raise(nameof(IsMonitoring)); ClearTiles(); }, () => IsMonitoring);
    }

    public MetricTile Cpu { get; } = new("CPU", "");
    public MetricTile CpuTemp { get; } = new("CPU temperature", "");
    public MetricTile Gpu { get; } = new("GPU", "");
    public MetricTile GpuTemp { get; } = new("GPU temperature", "");
    public MetricTile Vram { get; } = new("GPU memory", "");
    public MetricTile Ram { get; } = new("System memory", "");
    public MetricTile GameMemory { get; } = new("Game memory", "");
    public MetricTile Disk { get; } = new("Disk activity", "");
    public MetricTile Network { get; } = new("Network", "");
    public MetricTile Fps { get; } = new("FPS", "");

    public ObservableCollection<DriveRow> Drives { get; } = new();

    public bool IsMonitoring => _host.System.IsRunning;

    public IReadOnlyList<int> IntervalOptions { get; } = new[] { 250, 500, 1000, 2000, 5000 };

    public int IntervalMs
    {
        get => _host.S.Monitoring.SampleIntervalMs;
        set
        {
            _host.S.Monitoring.SampleIntervalMs = value;
            _host.Settings.NotifyChanged();
            if (IsMonitoring) _host.System.Start(value);
            OnPropertyChanged();
        }
    }

    public bool MinimalMode
    {
        get => _host.S.Monitoring.MinimalResourceMode;
        set
        {
            _host.S.Monitoring.MinimalResourceMode = value;
            _host.System.MinimalMode = value;
            _host.Settings.NotifyChanged();
            OnPropertyChanged();
        }
    }

    public string ForegroundText { get => _foreground; private set => Set(ref _foreground, value); }
    public string GameName { get => _gameName; private set => Set(ref _gameName, value); }
    public IReadOnlyList<double> CpuHistory { get => _cpuHistory; private set => Set(ref _cpuHistory, value); }
    public IReadOnlyList<double> GpuHistory { get => _gpuHistory; private set => Set(ref _gpuHistory, value); }
    public IReadOnlyList<double> RamHistory { get => _ramHistory; private set => Set(ref _ramHistory, value); }
    public IReadOnlyList<double> FpsHistory { get => _fpsHistory; private set => Set(ref _fpsHistory, value); }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    public void Update(SystemSnapshot? s, FpsSnapshot fps)
    {
        Raise(nameof(IsMonitoring));
        ((RelayCommand)StartCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopCommand).RaiseCanExecuteChanged();

        if (fps.HasLiveData) Fps.SetValue(Format.Fps(fps.CurrentFps), $"avg {Format.Fps(fps.Session?.AverageFps)} · 1% low {Format.Fps(fps.Session?.OnePercentLowFps)}", null, "PresentMon");
        else Fps.SetUnavailable(fps.State == FpsCaptureState.Idle ? "No game is being measured" : fps.StateTitle);
        FpsHistory = fps.FpsHistory;

        var running = _host.Running.Running;
        GameName = running.Count == 0 ? "No game running" : string.Join(", ", running.Select(r => r.GameName));

        if (s is null || !IsMonitoring) return;

        if (s.CpuPercent is { } cpu) Cpu.SetValue(Format.Percent(cpu), "Total utilisation", cpu / 100, "GetSystemTimes");
        else Cpu.SetUnavailable("Collecting first sample…");

        if (s.CpuTemperatureC is { } ct) CpuTemp.SetValue(Format.Temp(ct), "", ct / 100, s.CpuTemperatureNote);
        else CpuTemp.SetUnavailable(s.CpuTemperatureNote);

        if (s.GpuPercent is { } gpu) Gpu.SetValue(Format.Percent(gpu), s.GameGpuPercent is { } gg ? $"Game: {gg:0}%" : "Busiest engine", gpu / 100, "Windows GPU Engine counters");
        else Gpu.SetUnavailable(s.GpuNote ?? "Unavailable");

        if (s.GpuTemperatureC is { } gt) GpuTemp.SetValue(Format.Temp(gt), "", gt / 100, s.GpuTemperatureNote);
        else GpuTemp.SetUnavailable(s.GpuTemperatureNote);

        if (s.GpuMemoryUsedBytes is { } vu)
            Vram.SetValue(Format.Bytes(vu), s.GpuMemoryTotalBytes is { } vt ? $"of {Format.Bytes(vt)}" : "Dedicated in use", s.GpuMemoryTotalBytes is > 0 ? vu / s.GpuMemoryTotalBytes : null, "GPU Adapter Memory counters");
        else Vram.SetUnavailable("GPU memory counters unavailable");

        if (s.RamUsedBytes is { } ru) Ram.SetValue(Format.Bytes(ru), $"{Format.Bytes(s.RamAvailableBytes)} available of {Format.Bytes(s.RamTotalBytes)}", s.RamPercent / 100, "GlobalMemoryStatusEx");
        else Ram.SetUnavailable("Unavailable");

        if (s.GameProcessMemoryBytes is { } gm) GameMemory.SetValue(Format.Bytes(gm), "Working set of the game's processes", null, "Process information");
        else GameMemory.SetUnavailable(running.Count == 0 ? "No game running" : "The game's process denied access (common with anti-cheat)");

        if (s.DiskActivePercent is { } disk) Disk.SetValue(Format.Percent(disk), $"R {Format.Rate(s.DiskReadBytesPerSec)} · W {Format.Rate(s.DiskWriteBytesPerSec)}", disk / 100, "PhysicalDisk counters");
        else Disk.SetUnavailable(MinimalMode ? "Off in minimal-resource mode" : "Disk counters unavailable");

        if (s.NetworkReceiveBytesPerSec is not null || s.NetworkSendBytesPerSec is not null)
            Network.SetValue($"↓ {Format.Rate(s.NetworkReceiveBytesPerSec)}", $"↑ {Format.Rate(s.NetworkSendBytesPerSec)}", null, "Network Interface counters");
        else Network.SetUnavailable(MinimalMode ? "Off in minimal-resource mode" : "Network counters unavailable");

        ForegroundText = s.ForegroundProcessName is null ? Format.NoData : $"{s.ForegroundProcessName} (pid {s.ForegroundProcessId})";

        var drives = s.Drives.Select(d => new DriveRow(d.Root, string.IsNullOrWhiteSpace(d.Label) ? d.Kind.ToString() : d.Label!,
            $"{Format.Bytes(d.FreeBytes)} free of {Format.Bytes(d.TotalBytes)}", d.TotalBytes > 0 ? 1 - (double)d.FreeBytes / d.TotalBytes : 0)).ToList();
        if (!Drives.SequenceEqual(drives))
        {
            Drives.Clear();
            foreach (var d in drives) Drives.Add(d);
        }

        var history = _host.System.History.TakeLast(_host.S.Monitoring.HistoryPoints).ToList();
        CpuHistory = history.Where(h => h.CpuPercent.HasValue).Select(h => h.CpuPercent!.Value).ToList();
        GpuHistory = history.Where(h => h.GpuPercent.HasValue).Select(h => h.GpuPercent!.Value).ToList();
        RamHistory = history.Where(h => h.RamPercent.HasValue).Select(h => h.RamPercent!.Value).ToList();
    }

    private void ClearTiles()
    {
        foreach (var t in new[] { Cpu, CpuTemp, Gpu, GpuTemp, Vram, Ram, GameMemory, Disk, Network })
            t.SetUnavailable("Monitoring is stopped");
    }
}

public sealed record OverlayLine(string Label, string Value, bool Highlight);

/// <summary>Content of the in-game overlay. Every value is a real measurement or "N/A".</summary>
public sealed class OverlayViewModel : ObservableObject
{
    private readonly AppHost _host;
    private IReadOnlyList<double> _graph = Array.Empty<double>();
    private bool _showGraph, _isEditMode;
    private string _status = "";

    public OverlayViewModel(AppHost host) => _host = host;

    public ObservableCollection<OverlayLine> Lines { get; } = new();
    public IReadOnlyList<double> Graph { get => _graph; private set => Set(ref _graph, value); }
    public bool ShowGraph { get => _showGraph; private set => Set(ref _showGraph, value); }
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    public bool IsEditMode { get => _isEditMode; set => Set(ref _isEditMode, value); }

    public double FontSize => _host.S.Overlay.FontSize;
    public string TextColor => _host.S.Overlay.TextColor;
    public string AccentColor => _host.S.Overlay.AccentColor;

    public void SettingsChanged() => Raise(nameof(FontSize), nameof(TextColor), nameof(AccentColor));

    public void Update(FpsSnapshot fps, SystemSnapshot? sys)
    {
        var settings = _host.S.Overlay;
        var layout = settings.Layout;
        List<OverlayMetric>? metrics = null;
        if (fps.GameId is not null && _host.Library.Get(fps.GameId) is { } game)
        {
            layout = game.Profile.OverlayLayout ?? layout;
            metrics = game.Profile.OverlayMetrics;
        }

        metrics ??= layout switch
        {
            OverlayLayout.FpsOnly => new List<OverlayMetric> { OverlayMetric.Fps },
            OverlayLayout.Compact => new List<OverlayMetric> { OverlayMetric.Fps, OverlayMetric.OnePercentLow, OverlayMetric.FrameTime },
            OverlayLayout.Graph => new List<OverlayMetric> { OverlayMetric.Fps, OverlayMetric.FrameTime },
            _ => settings.FullMetrics.Count > 0 ? settings.FullMetrics : new List<OverlayMetric> { OverlayMetric.Fps },
        };

        var live = fps.HasLiveData;
        var lines = new List<OverlayLine>();
        foreach (var m in metrics)
        {
            lines.Add(m switch
            {
                OverlayMetric.Fps => new OverlayLine("FPS", live ? Format.Fps(fps.CurrentFps) : "N/A", true),
                OverlayMetric.AverageFps => new OverlayLine("AVG", live ? Format.Fps(fps.Session?.AverageFps) : "N/A", false),
                OverlayMetric.OnePercentLow => new OverlayLine("1% LOW", live && fps.Session?.OnePercentLowFps is { } low ? Format.Fps(low) : "N/A", false),
                OverlayMetric.FrameTime => new OverlayLine("FRAME TIME", live ? Format.Ms(fps.CurrentFrameTimeMs) : "N/A", false),
                OverlayMetric.Cpu => new OverlayLine("CPU", sys?.CpuPercent is { } c ? Format.Percent(c) : "N/A", false),
                OverlayMetric.Gpu => new OverlayLine("GPU", sys?.GpuPercent is { } g ? Format.Percent(g) : "N/A", false),
                OverlayMetric.Ram => new OverlayLine("RAM", sys?.RamUsedBytes is { } r ? Format.Bytes(r) : "N/A", false),
                OverlayMetric.GpuTemperature => new OverlayLine("GPU TEMP", sys?.GpuTemperatureC is { } t ? Format.Temp(t) : "N/A", false),
                OverlayMetric.VramUsage => new OverlayLine("VRAM", sys?.GpuMemoryUsedBytes is { } v ? Format.Bytes(v) : "N/A", false),
                OverlayMetric.SessionTime => new OverlayLine("TIME", fps.Elapsed > TimeSpan.Zero ? Format.Clock(fps.Elapsed) : "N/A", false),
                _ => new OverlayLine(m.ToString(), "N/A", false),
            });
        }

        if (Lines.Count != lines.Count || !Lines.SequenceEqual(lines))
        {
            // Update in place to avoid flicker.
            for (var i = 0; i < lines.Count; i++)
            {
                if (i < Lines.Count) { if (Lines[i] != lines[i]) Lines[i] = lines[i]; }
                else Lines.Add(lines[i]);
            }
            while (Lines.Count > lines.Count) Lines.RemoveAt(Lines.Count - 1);
        }

        ShowGraph = layout == OverlayLayout.Graph;
        Graph = ShowGraph ? FpsMonitorService.Decimate(fps.FrameTimeHistory, 120) : Array.Empty<double>();
        Status = IsEditMode ? "Drag to move · press the edit hotkey again to lock"
            : live ? ""
            : fps.State == FpsCaptureState.Idle ? "" : fps.StateTitle;
    }
}
