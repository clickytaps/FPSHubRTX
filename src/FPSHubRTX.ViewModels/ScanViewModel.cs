using FPSHubRTX.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows.Input;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Mvvm;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Scanning;
using FPSHubRTX.Core.Services;

namespace FPSHubRTX.ViewModels;

public sealed class ScanSourceRow : ObservableObject
{
    private string _state = "Waiting";
    private string _detail = "";
    private int _count;
    private string _kind = "pending";

    public ScanSourceRow(LauncherKind kind, string name)
    {
        Launcher = kind;
        Name = name;
    }

    public LauncherKind Launcher { get; }
    public string Name { get; }
    public string State { get => _state; set => Set(ref _state, value); }
    /// <summary>pending | scanning | done | notfound | error | skipped — used by the view for colour/icons.</summary>
    public string Kind { get => _kind; set => Set(ref _kind, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public int Count { get => _count; set => Set(ref _count, value); }
}

public sealed class LauncherToggle : ObservableObject
{
    private readonly Action<LauncherKind, bool> _changed;
    private bool _enabled;

    public LauncherToggle(LauncherKind kind, string name, bool enabled, Action<LauncherKind, bool> changed)
    {
        Kind = kind;
        Name = name;
        _enabled = enabled;
        _changed = changed;
    }

    public LauncherKind Kind { get; }
    public string Name { get; }
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value)) _changed(Kind, value);
        }
    }
}

/// <summary>The SCAN GAMES experience: scope controls, live progress, cancellation and the completion summary.</summary>
public sealed class ScanViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IPlatformUi _ui;
    private readonly IShell _shell;
    private CancellationTokenSource? _cts;
    private bool _isScanning, _hasSummary;
    private double _progress;
    private string _currentSource = "", _currentLocation = "", _summaryTitle = "", _summaryText = "";
    private int _gamesFound;

    public ScanViewModel(AppHost host, IPlatformUi ui, IShell shell)
    {
        _host = host;
        _ui = ui;
        _shell = shell;
        StartScanCommand = new AsyncRelayCommand(StartAsync, () => !IsScanning, ex => _shell.ShowBanner("Scan failed: " + ex.Message, true));
        CancelCommand = new RelayCommand(Cancel, () => IsScanning);
        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveFolderCommand = new RelayCommand(p => RemoveFrom(_host.S.Scan.CustomFolders, CustomFolders, p as string));
        AddExcludedCommand = new RelayCommand(AddExcluded);
        RemoveExcludedCommand = new RelayCommand(p => RemoveFrom(_host.S.Scan.ExcludedDirectories, ExcludedFolders, p as string));
        ViewLibraryCommand = new RelayCommand(() => _shell.Navigate(Pages.Games));

        var scan = _host.S.Scan;
        foreach (var s in _host.Scanner.Scanners)
        {
            Launchers.Add(new LauncherToggle(s.Kind, s.DisplayName, scan.EnabledLaunchers.Count == 0 || scan.EnabledLaunchers.Contains(s.Kind), OnLauncherToggled));
        }
        foreach (var f in scan.CustomFolders) CustomFolders.Add(f);
        foreach (var f in scan.ExcludedDirectories) ExcludedFolders.Add(f);
        ResetRows();
    }

    public ObservableCollection<ScanSourceRow> Sources { get; } = new();
    public ObservableCollection<LauncherToggle> Launchers { get; } = new();
    public ObservableCollection<string> CustomFolders { get; } = new();
    public ObservableCollection<string> ExcludedFolders { get; } = new();
    public ObservableCollection<string> SummaryDetails { get; } = new();

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                Raise(nameof(IsIdle));
                ((AsyncRelayCommand)StartScanCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsIdle => !IsScanning;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string CurrentSource { get => _currentSource; private set => Set(ref _currentSource, value); }
    public string CurrentLocation { get => _currentLocation; private set => Set(ref _currentLocation, value); }
    public int GamesFound { get => _gamesFound; private set => Set(ref _gamesFound, value); }
    public bool HasSummary { get => _hasSummary; private set => Set(ref _hasSummary, value); }
    public string SummaryTitle { get => _summaryTitle; private set => Set(ref _summaryTitle, value); }
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }

    public bool IncludeRemovableDrives
    {
        get => _host.S.Scan.IncludeRemovableDrives;
        set { _host.S.Scan.IncludeRemovableDrives = value; _host.Settings.NotifyChanged(); OnPropertyChanged(); }
    }

    public bool ScanCommonFolders
    {
        get => _host.S.Scan.ScanCommonGameFolders;
        set { _host.S.Scan.ScanCommonGameFolders = value; _host.Settings.NotifyChanged(); OnPropertyChanged(); }
    }

    public bool MarkMissingGames
    {
        get => _host.S.Scan.MarkMissingGames;
        set { _host.S.Scan.MarkMissingGames = value; _host.Settings.NotifyChanged(); OnPropertyChanged(); }
    }

    public ICommand StartScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand AddExcludedCommand { get; }
    public ICommand RemoveExcludedCommand { get; }
    public ICommand ViewLibraryCommand { get; }

    /// <summary>Starts a scan (used by the big SCAN GAMES button on Home too).</summary>
    public void TriggerScan()
    {
        if (StartScanCommand.CanExecute(null)) StartScanCommand.Execute(null);
    }

    private void ResetRows()
    {
        Sources.Clear();
        foreach (var s in _host.Scanner.Scanners)
        {
            var enabled = Launchers.First(l => l.Kind == s.Kind).IsEnabled;
            Sources.Add(new ScanSourceRow(s.Kind, s.DisplayName) { State = enabled ? "Waiting" : "Skipped (disabled)", Kind = enabled ? "pending" : "skipped" });
        }
    }

    private async Task StartAsync()
    {
        if (Launchers.All(l => !l.IsEnabled))
        {
            _shell.ShowBanner("Enable at least one launcher or source to scan.", true);
            return;
        }

        IsScanning = true;
        HasSummary = false;
        SummaryDetails.Clear();
        Progress = 0;
        GamesFound = 0;
        ResetRows();
        _cts = new CancellationTokenSource();

        var progress = new Progress<ScanProgress>(OnProgress); // created on the UI thread → callbacks marshalled to it
        var before = _host.Library.Count;
        ScanSummary summary;
        try
        {
            // Scan a snapshot of the settings so toggles changed during the scan can't race with it.
            summary = await _host.Scanner.ScanAsync(_host.S.Clone().Scan, progress, _cts.Token);
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }

        foreach (var r in summary.SourceResults)
        {
            var row = Sources.FirstOrDefault(x => x.Launcher == r.Kind);
            if (row is null) continue;
            row.Count = r.Games.Count;
            if (r.Error is not null && r.Error != "Cancelled")
            {
                row.Kind = "error";
                row.State = "Error";
                row.Detail = r.Error;
            }
            else if (r.Error == "Cancelled")
            {
                row.Kind = "skipped";
                row.State = "Cancelled";
            }
            else if (!r.LauncherDetected && r.Games.Count == 0)
            {
                row.Kind = "notfound";
                row.State = "Not installed";
            }
            else
            {
                row.Kind = "done";
                row.State = $"{r.Games.Count} found";
                row.Detail = r.Warnings.Count > 0 ? string.Join(" ", r.Warnings.Take(3)) : $"{r.Duration.TotalSeconds:0.0}s";
            }
        }
        foreach (var row in Sources.Where(r => r.Kind == "pending"))
        {
            row.Kind = "skipped";
            row.State = summary.Cancelled ? "Cancelled" : "Skipped";
        }

        // A cancelled scan still saves what was found, but nothing is marked missing from an incomplete scan.
        var merge = _host.Library.MergeScan(summary, markMissing: !summary.Cancelled && _host.S.Scan.MarkMissingGames);
        _host.Library.RefreshStatuses();

        Progress = 100;
        GamesFound = summary.Games.Count;
        HasSummary = true;
        SummaryTitle = summary.Cancelled ? "Scan cancelled" : "Scan complete";
        SummaryText = $"{summary.Games.Count} game{(summary.Games.Count == 1 ? "" : "s")} found in {summary.Duration.TotalSeconds:0.0}s · " +
                      $"{merge.Added.Count} new · {merge.Updated.Count} updated · {merge.NowMissing.Count} missing";
        foreach (var g in merge.Added.Take(30)) SummaryDetails.Add($"New: {g.DisplayName} ({Format.Launcher(g.Launcher)})");
        if (merge.Added.Count > 30) SummaryDetails.Add($"…and {merge.Added.Count - 30} more new games");
        foreach (var g in merge.NowMissing.Take(20)) SummaryDetails.Add($"Missing: {g.DisplayName} — {g.StatusDetail}");
        foreach (var w in summary.SourceResults.SelectMany(r => r.Warnings).Take(10)) SummaryDetails.Add("Note: " + w);
        if (summary.DuplicatesRemoved > 0) SummaryDetails.Add($"{summary.DuplicatesRemoved} duplicate detection(s) were merged.");

        _host.Notifications.Notify(NotificationKind.ScanCompleted, SummaryTitle, SummaryText);
        if (merge.Added.Count > 0 && before > 0)
            _host.Notifications.Notify(NotificationKind.NewGamesDiscovered, "New games found", string.Join(", ", merge.Added.Take(5).Select(g => g.DisplayName)) + (merge.Added.Count > 5 ? "…" : ""));

        if (_host.S.Privacy.DownloadCoverArt)
        {
            _ = Task.Run(async () =>
            {
                try { await CoverArtService.DownloadMissingSteamCoversAsync(_host.Library, _host.Paths, _host.Log, CancellationToken.None); }
                catch (Exception ex) { _host.Log.Warn("Cover download failed", ex); }
            });
        }
    }

    private void OnProgress(ScanProgress p)
    {
        Progress = p.Fraction * 100;
        GamesFound = p.GamesFound;
        CurrentSource = p.Stage switch
        {
            ScanStage.Validating => "Checking install folders and removing duplicates…",
            ScanStage.Completed => "Finishing…",
            ScanStage.Cancelled => "Cancelling…",
            _ => $"Checking {p.CurrentSource}…",
        };
        CurrentLocation = p.CurrentLocation ?? "";
        if (p.Stage == ScanStage.Scanning)
        {
            foreach (var row in Sources)
            {
                if (row.Name == p.CurrentSource && row.Kind == "pending")
                {
                    row.Kind = "scanning";
                    row.State = "Scanning…";
                }
            }
        }
    }

    private void Cancel() => _cts?.Cancel();

    private void OnLauncherToggled(LauncherKind kind, bool enabled)
    {
        var set = _host.S.Scan.EnabledLaunchers;
        if (set.Count == 0) foreach (var l in Launchers) set.Add(l.Kind);
        if (enabled) set.Add(kind); else set.Remove(kind);
        _host.Settings.NotifyChanged();
        if (!IsScanning) ResetRows();
    }

    private void AddFolder()
    {
        var folder = _ui.PickFolder("Choose a folder that contains games (or a single game's folder). Internal, additional and external drives are all supported.");
        if (folder is null) return;
        if (!PathUtil.IsSafeAbsolutePath(folder)) return;
        var root = Path.GetPathRoot(folder);
        if (string.Equals(root?.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            _shell.ShowBanner("Please choose a folder rather than a whole drive — scanning an entire drive is slow and can pick up non-game programs.", true);
            return;
        }
        if (_host.S.Scan.CustomFolders.Any(f => PathUtil.NormalizeKey(f) == PathUtil.NormalizeKey(folder))) return;
        _host.S.Scan.CustomFolders.Add(folder);
        CustomFolders.Add(folder);
        _host.Settings.NotifyChanged();
    }

    private void AddExcluded()
    {
        var folder = _ui.PickFolder("Choose a folder FPS Hub should never scan");
        if (folder is null || !PathUtil.IsSafeAbsolutePath(folder)) return;
        if (_host.S.Scan.ExcludedDirectories.Any(f => PathUtil.NormalizeKey(f) == PathUtil.NormalizeKey(folder))) return;
        _host.S.Scan.ExcludedDirectories.Add(folder);
        ExcludedFolders.Add(folder);
        _host.Settings.NotifyChanged();
    }

    private void RemoveFrom(List<string> settingsList, ObservableCollection<string> view, string? folder)
    {
        if (folder is null) return;
        settingsList.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        view.Remove(folder);
        _host.Settings.NotifyChanged();
    }
}
