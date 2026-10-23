using System.Collections.ObjectModel;
using System.Windows.Input;
using FPSHubRTX.Core.Export;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Mvvm;
using FPSHubRTX.Core.Services;
using FPSHubRTX.Core.Statistics;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.ViewModels;

public sealed class BenchmarkRow
{
    public BenchmarkRow(BenchmarkRun run) => Run = run;
    public BenchmarkRun Run { get; }
    public string Label => Run.Label;
    public string Game => Run.GameName;
    public string Date => Run.StartUtc.ToLocalTime().ToString("g");
    public string Duration => Format.Clock(Run.EndUtc - Run.StartUtc);
    public string AverageFps => Format.Fps(Run.Summary?.AverageFps);
    public string OnePercentLow => Format.Fps(Run.Summary?.OnePercentLowFps);
    public string PointOnePercentLow => Format.Fps(Run.Summary?.PointOnePercentLowFps);
    public string Context => string.Join(" · ", new[] { Run.Resolution, Run.GraphicsPreset, Run.Scene }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public override string ToString() => $"{Label} — {Game} ({Date})";
}

public sealed class BenchmarksViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IPlatformUi _ui;
    private readonly IShell _shell;
    private string _label = "", _resolution = "", _preset = "", _scene = "", _notes = "";
    private string _status = "", _elapsed = "", _report = "", _comparison = "";
    private BenchmarkRow? _selected, _compareA, _compareB;
    private DateTime _startedUtc;
    private IReadOnlyList<double> _selectedSeries = Array.Empty<double>();

    public BenchmarksViewModel(AppHost host, IPlatformUi ui, IShell shell)
    {
        _host = host;
        _ui = ui;
        _shell = shell;
        StartCommand = new RelayCommand(Start, () => !IsRecording);
        StopCommand = new RelayCommand(Stop, () => IsRecording);
        ExportCsvCommand = new RelayCommand(() => Export("csv"), () => Selected is not null);
        ExportJsonCommand = new RelayCommand(() => Export("json"), () => Selected is not null);
        ExportReportCommand = new RelayCommand(() => Export("txt"), () => Selected is not null);
        ExportAllCommand = new RelayCommand(ExportAll, () => History.Count > 0);
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null);
        CopyReportCommand = new RelayCommand(() => _ui.SetClipboardText(SelectedReport), () => Selected is not null);
        _host.Benchmarks.Changed += (_, _) => _ui.Post(Refresh);
        _host.Bench.Completed += run => _ui.Post(() =>
        {
            Refresh();
            Selected = History.FirstOrDefault(h => h.Run.Id == run.Id);
            _host.Notifications.Notify(NotificationKind.BenchmarkCompleted, "Benchmark complete",
                $"{run.Label}: avg {Format.Fps(run.Summary?.AverageFps)} FPS, 1% low {Format.Fps(run.Summary?.OnePercentLowFps)}");
        });
    }

    public string Label { get => _label; set => Set(ref _label, value); }
    public string Resolution { get => _resolution; set => Set(ref _resolution, value); }
    public string GraphicsPreset { get => _preset; set => Set(ref _preset, value); }
    public string Scene { get => _scene; set => Set(ref _scene, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }

    public bool IsRecording => _host.Bench.IsRecording;
    public string RecordingStatus { get => _status; private set => Set(ref _status, value); }
    public string RecordingElapsed { get => _elapsed; private set => Set(ref _elapsed, value); }
    public int RecordedFrames => _host.Bench.RecordedFrames;

    public ObservableCollection<BenchmarkRow> History { get; } = new();

    public BenchmarkRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            SelectedReport = value is null ? "" : Exporters.BenchmarkReport(value.Run);
            SelectedFrameTimes = value is null ? Array.Empty<double>() : FpsMonitorService.Decimate(value.Run.Frames.Select(f => f.FrameTimeMs).ToList(), 800);
            foreach (var c in new[] { ExportCsvCommand, ExportJsonCommand, ExportReportCommand, DeleteCommand, CopyReportCommand }) ((RelayCommand)c).RaiseCanExecuteChanged();
        }
    }

    public string SelectedReport { get => _report; private set => Set(ref _report, value); }
    public IReadOnlyList<double> SelectedFrameTimes { get => _selectedSeries; private set => Set(ref _selectedSeries, value); }

    public BenchmarkRow? CompareA { get => _compareA; set { if (Set(ref _compareA, value)) UpdateComparison(); } }
    public BenchmarkRow? CompareB { get => _compareB; set { if (Set(ref _compareB, value)) UpdateComparison(); } }
    public string ComparisonText { get => _comparison; private set { if (Set(ref _comparison, value)) Raise(nameof(HasComparison)); } }
    public bool HasComparison => !string.IsNullOrEmpty(_comparison);

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand ExportAllCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CopyReportCommand { get; }

    public void Refresh()
    {
        var selectedId = Selected?.Run.Id;
        var aId = CompareA?.Run.Id;
        var bId = CompareB?.Run.Id;
        History.Clear();
        foreach (var r in _host.Benchmarks.GetAll()) History.Add(new BenchmarkRow(r));
        _selected = History.FirstOrDefault(h => h.Run.Id == selectedId);
        _compareA = History.FirstOrDefault(h => h.Run.Id == aId);
        _compareB = History.FirstOrDefault(h => h.Run.Id == bId);
        Raise(nameof(Selected), nameof(CompareA), nameof(CompareB));
        if (_selected is null) Selected = History.FirstOrDefault();
        UpdateComparison();
        ((RelayCommand)ExportAllCommand).RaiseCanExecuteChanged();
    }

    /// <summary>Called by the shell's refresh loop.</summary>
    public void Tick(FpsSnapshot fps)
    {
        Raise(nameof(IsRecording), nameof(RecordedFrames));
        ((RelayCommand)StartCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
        if (IsRecording)
        {
            RecordingElapsed = Format.Clock(DateTime.UtcNow - _startedUtc);
            RecordingStatus = fps.HasLiveData
                ? $"Recording {fps.GameName} — {RecordedFrames:N0} frames, current {Format.Fps(fps.CurrentFps)} FPS"
                : $"Recording paused: {fps.StateTitle}";
        }
        else
        {
            RecordingElapsed = "";
            RecordingStatus = fps.State == FpsCaptureState.Measuring
                ? $"Ready — measuring {fps.GameName}. Press Start when you're at your benchmark scene."
                : "Waiting for measured FPS. Launch a game and wait for the FPS Monitor to show 'Measuring'.";
        }
    }

    private void Start()
    {
        var fps = _host.Fps.GetSnapshot();
        var game = fps.GameId is null ? null : _host.Library.Get(fps.GameId);
        var error = _host.Bench.Start(new BenchmarkContext
        {
            Label = Label,
            Resolution = string.IsNullOrWhiteSpace(Resolution) ? null : Resolution.Trim(),
            GraphicsPreset = string.IsNullOrWhiteSpace(GraphicsPreset) ? null : GraphicsPreset.Trim(),
            Scene = string.IsNullOrWhiteSpace(Scene) ? null : Scene.Trim(),
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
        }, game?.DisplayName, game?.EffectiveExecutablePath is { } exe ? Path.GetFileName(exe) : null);
        if (error is not null)
        {
            _shell.ShowBanner(error, true);
            return;
        }
        _startedUtc = DateTime.UtcNow;
        Tick(fps);
    }

    private void Stop()
    {
        var run = _host.Bench.Stop();
        if (run?.Summary is null) _shell.ShowBanner("The benchmark ended without any measured frames, so it has no results.", true);
        Tick(_host.Fps.GetSnapshot());
    }

    private void UpdateComparison()
    {
        if (CompareA is null || CompareB is null || CompareA == CompareB)
        {
            ComparisonText = "";
            return;
        }
        ComparisonText = Exporters.ComparisonReport(BenchmarkComparer.Compare(CompareA.Run, CompareB.Run));
    }

    private void Export(string kind)
    {
        if (Selected is null) return;
        var run = Selected.Run;
        var safe = string.Concat(run.Label.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var (filter, ext, content) = kind switch
        {
            "csv" => ("CSV (*.csv)|*.csv", "csv", Exporters.BenchmarkFramesCsv(run)),
            "json" => ("JSON (*.json)|*.json", "json", Exporters.BenchmarkJson(run)),
            _ => ("Text (*.txt)|*.txt", "txt", Exporters.BenchmarkReport(run)),
        };
        var path = _ui.SaveFile("Export benchmark", filter, $"{safe}.{ext}");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, content);
            _shell.ShowBanner($"Exported to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowBanner("Export failed: " + ex.Message, true);
        }
    }

    private void ExportAll()
    {
        var path = _ui.SaveFile("Export all benchmark summaries", "CSV (*.csv)|*.csv", "fpshub-benchmarks.csv");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, Exporters.BenchmarkSummariesCsv(_host.Benchmarks.GetAll()));
            _shell.ShowBanner($"Exported {History.Count} benchmark summaries.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowBanner("Export failed: " + ex.Message, true);
        }
    }

    private void Delete()
    {
        if (Selected is null || !_ui.Confirm("Delete benchmark", $"Delete '{Selected.Label}'?")) return;
        _host.Benchmarks.Delete(Selected.Run.Id);
    }
}

public sealed record BarItem(string Label, string ValueText, double Fraction);

public sealed record SessionRow(string Game, string Date, string Duration, string Ended, string Fps, string OnePercentLow);

public sealed record FpsHistoryRow(string Game, string Best, string Latest, int Sessions, IReadOnlyList<double> Series);

/// <summary>Game Sessions / statistics page.</summary>
public sealed class SessionsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IPlatformUi _ui;
    private readonly IShell _shell;
    private PlaytimeReport _report = new();
    private IReadOnlyList<double> _daily = Array.Empty<double>();
    private IReadOnlyList<string> _dailyLabels = Array.Empty<string>();

    public SessionsViewModel(AppHost host, IPlatformUi ui, IShell shell)
    {
        _host = host;
        _ui = ui;
        _shell = shell;
        ExportJsonCommand = new RelayCommand(() => Export(json: true));
        ExportCsvCommand = new RelayCommand(() => Export(json: false));
        DeleteHistoryCommand = new RelayCommand(DeleteHistory);
    }

    public string Today => Format.Duration(_report.Today);
    public string ThisWeek => Format.Duration(_report.ThisWeek);
    public string ThisMonth => Format.Duration(_report.ThisMonth);
    public string AllTime => Format.Duration(_report.AllTime);
    public int SessionCount => _report.SessionCount;
    public string AverageSession => _report.SessionCount == 0 ? Format.NoData : Format.Duration(_report.AverageSessionLength);
    public string AverageFps => Format.Fps(_report.AverageMeasuredFps);
    public bool HasSessions => _report.SessionCount > 0;
    public IReadOnlyList<double> DailyMinutes => _daily;
    public IReadOnlyList<string> DailyLabels => _dailyLabels;

    public ObservableCollection<BarItem> MostPlayed { get; } = new();
    public ObservableCollection<SessionRow> RecentSessions { get; } = new();
    public ObservableCollection<FpsHistoryRow> FpsByGame { get; } = new();

    public ICommand ExportJsonCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand DeleteHistoryCommand { get; }

    public void Refresh()
    {
        var sessions = _host.Sessions.GetAll();
        _report = PlaytimeStatistics.Build(sessions, DateTime.UtcNow);
        _daily = _report.Last30Days.Select(d => Math.Round(d.Total.TotalMinutes, 1)).ToList();
        _dailyLabels = _report.Last30Days.Select(d => d.Day.ToString("dd MMM")).ToList();

        MostPlayed.Clear();
        var max = _report.MostPlayed.FirstOrDefault()?.Total.TotalSeconds ?? 0;
        foreach (var g in _report.MostPlayed)
            MostPlayed.Add(new BarItem(g.GameName, $"{Format.Duration(g.Total)} · {g.Sessions} session{(g.Sessions == 1 ? "" : "s")}", max > 0 ? g.Total.TotalSeconds / max : 0));

        RecentSessions.Clear();
        foreach (var s in sessions.OrderByDescending(s => s.StartUtc).Take(50))
        {
            var ended = s.EndReason switch
            {
                SessionEndReason.NonZeroExitCode => $"Exit code {s.ExitCode} (possible crash)",
                SessionEndReason.AppClosed => "FPS Hub closed",
                _ => "Closed normally",
            };
            var fps = s.FpsAvailability switch
            {
                FpsAvailability.Measured => Format.Fps(s.Fps?.AverageFps),
                FpsAvailability.Unavailable => "Unavailable",
                _ => "Not captured",
            };
            RecentSessions.Add(new SessionRow(s.GameName, s.StartUtc.ToLocalTime().ToString("g"), Format.Duration(s.Duration), ended, fps, Format.Fps(s.Fps?.OnePercentLowFps)));
        }

        FpsByGame.Clear();
        foreach (var h in PlaytimeStatistics.FpsHistoryByGame(sessions))
            FpsByGame.Add(new FpsHistoryRow(h.GameName, Format.Fps(h.BestAverageFps), Format.Fps(h.Points[^1].AverageFps), h.Points.Count, h.Points.Select(p => p.AverageFps).ToList()));

        OnPropertyChanged(string.Empty);
    }

    private void Export(bool json)
    {
        var path = _ui.SaveFile("Export activity history", json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv", json ? "fpshub-activity.json" : "fpshub-activity.csv");
        if (path is null) return;
        try
        {
            var sessions = _host.Sessions.GetAll();
            File.WriteAllText(path, json ? Exporters.SessionsJson(sessions) : Exporters.SessionsCsv(sessions));
            _shell.ShowBanner($"Exported {sessions.Count} sessions.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowBanner("Export failed: " + ex.Message, true);
        }
    }

    private void DeleteHistory()
    {
        if (!_ui.Confirm("Delete activity history",
                "Delete all recorded sessions, playtime totals and benchmark history?\n\nYour game library and settings are kept. This can't be undone (consider Settings → Back up first).")) return;
        _host.Sessions.DeleteAll();
        _host.Benchmarks.DeleteAll();
        foreach (var g in _host.Library.GetAll())
        {
            _host.Library.Update(g.Id, x =>
            {
                x.TotalPlaytimeSeconds = 0;
                x.SessionCount = 0;
                x.LastPlayedUtc = null;
            });
        }
        _shell.ShowBanner("Activity history deleted.");
        Refresh();
    }
}
