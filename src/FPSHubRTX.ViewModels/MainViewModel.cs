using FPSHubRTX.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows.Input;
using FPSHubRTX.Core.Launching;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Mvvm;
using FPSHubRTX.Core.Services;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.ViewModels;

public sealed class NavItem : ObservableObject
{
    private bool _selected;

    public NavItem(string key, string title, string glyph)
    {
        Key = key;
        Title = title;
        Glyph = glyph;
    }

    public string Key { get; }
    public string Title { get; }
    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets glyph.</summary>
    public string Glyph { get; }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>Root view model: navigation, the message banner, the live refresh loop and app-wide events.</summary>
public sealed class MainViewModel : ObservableObject, IShell, IDisposable
{
    private readonly AppHost _host;
    private readonly IPlatformUi _ui;
    private object _currentPage;
    private string _currentKey = Pages.Home;
    private string _banner = "";
    private bool _bannerIsError;
    private bool _overlayVisible;
    private bool _overlayEdit;
    private bool _overlayManuallyHidden;
    private string _runningSummary = "No game running";
    private Timer? _tick;
    private int _tickBusy;
    private int _tickCount;

    public MainViewModel(AppHost host, IPlatformUi ui)
    {
        _host = host;
        _ui = ui;
        Actions = new GameActions(host, ui, this);
        Games = new GamesViewModel(host, ui, this, Actions);
        Home = new HomeViewModel(host, this, id => Games.GetCard(id));
        Scan = new ScanViewModel(host, ui, this);
        Fps = new FpsViewModel(host, ui, this);
        Performance = new PerformanceViewModel(host);
        Benchmarks = new BenchmarksViewModel(host, ui, this);
        Sessions = new SessionsViewModel(host, ui, this);
        Settings = new SettingsViewModel(host, ui, this, ApplySettings, () => ToggleOverlayEditMode());
        Overlay = new OverlayViewModel(host);

        NavItems = new ObservableCollection<NavItem>
        {
            new(Pages.Home, "Home", ""),
            new(Pages.Games, "My Games", ""),
            new(Pages.Scan, "Scan Games", ""),
            new(Pages.Fps, "FPS Monitor", ""),
            new(Pages.Performance, "Performance", ""),
            new(Pages.Benchmarks, "Benchmarks", ""),
            new(Pages.Sessions, "Game Sessions", ""),
            new(Pages.Settings, "Settings", ""),
        };
        _currentPage = Home;
        NavItems[0].IsSelected = true;

        NavigateCommand = new RelayCommand(p => Navigate(p as string ?? Pages.Home));
        DismissBannerCommand = new RelayCommand(() => BannerMessage = "");
        ToggleOverlayCommand = new RelayCommand(ToggleOverlay);
        ScanNowCommand = new RelayCommand(() => Navigate(Pages.Scan + ":start"));

        _host.Library.Changed += (_, _) => _ui.Post(OnLibraryChanged);
        _host.Sessions.Changed += (_, _) => _ui.Post(() => { Sessions.Refresh(); Home.Refresh(); });
        _host.Running.GameStarted += (_, rg) => _ui.Post(() => OnGameStarted(rg));
        _host.Running.GameExited += (_, e) => _ui.Post(() => OnGameExited(e));
        _host.Running.LaunchTimedOut += (_, id) => _ui.Post(() => OnLaunchTimedOut(id));
        _host.Tracker.SessionEnded += s => _ui.Post(() => OnSessionEnded(s));
    }

    public GameActions Actions { get; }
    public HomeViewModel Home { get; }
    public GamesViewModel Games { get; }
    public ScanViewModel Scan { get; }
    public FpsViewModel Fps { get; }
    public PerformanceViewModel Performance { get; }
    public BenchmarksViewModel Benchmarks { get; }
    public SessionsViewModel Sessions { get; }
    public SettingsViewModel Settings { get; }
    public OverlayViewModel Overlay { get; }
    public ObservableCollection<NavItem> NavItems { get; }

    public object CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }
    public string CurrentPageKey { get => _currentKey; private set => Set(ref _currentKey, value); }

    public string BannerMessage
    {
        get => _banner;
        private set { if (Set(ref _banner, value)) Raise(nameof(HasBanner)); }
    }

    public bool HasBanner => !string.IsNullOrEmpty(_banner);
    public bool BannerIsError { get => _bannerIsError; private set => Set(ref _bannerIsError, value); }
    public bool IsOverlayVisible { get => _overlayVisible; private set => Set(ref _overlayVisible, value); }
    public string RunningSummary { get => _runningSummary; private set => Set(ref _runningSummary, value); }
    public string VersionText => $"v{UpdateChecker.CurrentVersion.ToString(3)}";

    public ICommand NavigateCommand { get; }
    public ICommand DismissBannerCommand { get; }
    public ICommand ToggleOverlayCommand { get; }
    public ICommand ScanNowCommand { get; }

    /// <summary>Loads data, applies settings, starts monitoring and the refresh loop.</summary>
    public void Initialize()
    {
        Games.Refresh();
        Home.Refresh();
        Sessions.Refresh();
        Benchmarks.Refresh();
        Settings.RunHealthCheck();
        ApplySettings();
        _host.Start();
        _tick = new Timer(_ => _ui.Post(Tick), null, 500, 500);

        if (_host.S.Updates.CheckOnStartup && !string.IsNullOrWhiteSpace(_host.S.Updates.FeedUrl))
            _ = Settings.CheckUpdatesAsync();
        if (_host.Library.Count == 0)
            ShowBanner("Welcome! Press SCAN GAMES to find the games installed on this PC.");
    }

    public void Navigate(string pageKey)
    {
        var startScan = pageKey.EndsWith(":start", StringComparison.Ordinal);
        var key = startScan ? pageKey[..^6] : pageKey;
        CurrentPage = key switch
        {
            Pages.Games => Games,
            Pages.Scan => Scan,
            Pages.Fps => Fps,
            Pages.Performance => Performance,
            Pages.Benchmarks => Benchmarks,
            Pages.Sessions => Sessions,
            Pages.Settings => Settings,
            _ => Home,
        };
        CurrentPageKey = key;
        foreach (var n in NavItems) n.IsSelected = n.Key == key;
        if (key == Pages.Sessions) Sessions.Refresh();
        if (key == Pages.Fps) Fps.RefreshPermissionStatus();
        if (key == Pages.Settings) Settings.RunHealthCheck();
        if (startScan) Scan.TriggerScan();
    }

    public void ShowBanner(string message, bool isError = false)
    {
        BannerIsError = isError;
        BannerMessage = message;
    }

    public GameCardViewModel? FindCard(string gameId) => Games.GetCard(gameId);

    private void OnLibraryChanged()
    {
        Games.Refresh();
        Home.Refresh();
        UpdateRunningFlags();
    }

    private void OnGameStarted(RunningGame rg)
    {
        var card = Games.GetCard(rg.GameId);
        if (card is not null)
        {
            card.IsLaunching = false;
            card.IsRunning = true;
        }
        if (rg.LaunchedFromHub)
            _host.Notifications.Notify(NotificationKind.GameLaunched, "Game started", $"{rg.GameName} is running.");
        _overlayManuallyHidden = false;
        UpdateOverlayAutoVisibility();
        Home.RefreshRunning();
        Fps.UpdateRunningGames();
    }

    private void OnGameExited(GameExitedEventArgs e)
    {
        var card = Games.GetCard(e.Game.GameId);
        if (card is not null) card.IsRunning = false;
        if (e.ExitCode is { } code && code != 0)
            ShowBanner($"{e.Game.GameName} closed with exit code {code} — it may have crashed. The session was saved.", true);
        UpdateOverlayAutoVisibility();
        Home.RefreshRunning();
        Fps.UpdateRunningGames();
    }

    private void OnLaunchTimedOut(string gameId)
    {
        var card = Games.GetCard(gameId);
        if (card is null || card.IsRunning) return;
        card.IsLaunching = false;
        ShowBanner($"{card.Name} hasn't started yet. If its launcher is asking you to sign in, update or confirm, finish that — FPS Hub will detect the game when it starts.");
    }

    private void OnSessionEnded(GameSession s)
    {
        var fps = s.FpsAvailability == FpsAvailability.Measured ? $", average {Format.Fps(s.Fps?.AverageFps)} FPS" : "";
        _host.Notifications.Notify(NotificationKind.SessionEnded, "Session ended", $"{s.GameName}: played {Format.Duration(s.Duration)}{fps}.");
    }

    private void UpdateRunningFlags()
    {
        foreach (var card in Games.AllCards)
        {
            var running = _host.Running.IsRunning(card.Id);
            if (card.IsRunning != running) card.IsRunning = running;
            if (card.IsLaunching && !_host.Running.IsPending(card.Id) && !running) card.IsLaunching = false;
        }
    }

    /// <summary>Runs on the UI thread every 500 ms: pulls the latest measurements into the visible view models.</summary>
    private void Tick()
    {
        if (Interlocked.Exchange(ref _tickBusy, 1) == 1) return;
        try
        {
            _tickCount++;
            _host.Tracker.RefreshProcesses();
            var fps = _host.Fps.GetSnapshot();
            var sys = _host.System.Latest;

            var running = _host.Running.Running;
            RunningSummary = running.Count == 0 ? "No game running"
                : fps.HasLiveData ? $"{fps.GameName} · {Format.Fps(fps.CurrentFps)} FPS"
                : $"{string.Join(", ", running.Select(r => r.GameName))} running";

            foreach (var card in Games.AllCards.Where(c => c.IsRunning))
                card.CurrentFpsText = fps.HasLiveData && fps.GameId == card.Id ? $"{Format.Fps(fps.CurrentFps)} FPS" : "";
            Home.LiveFpsText = fps.HasLiveData ? Format.Fps(fps.CurrentFps) : "";
            Home.LiveFpsGame = fps.HasLiveData ? fps.GameName ?? "" : "";

            if (ReferenceEquals(CurrentPage, Fps)) { Fps.UpdateRunningGames(); Fps.Update(fps); }
            else if (_tickCount % 4 == 0) Fps.UpdateRunningGames();
            if (ReferenceEquals(CurrentPage, Performance)) Performance.Update(sys, fps);
            if (ReferenceEquals(CurrentPage, Benchmarks) || _host.Bench.IsRecording) Benchmarks.Tick(fps);
            if (IsOverlayVisible) Overlay.Update(fps, sys);
            if (_tickCount % 4 == 0) UpdateRunningFlags();

            _host.Thresholds.Evaluate(fps, sys);
        }
        catch (Exception ex)
        {
            _host.Log.Error("UI refresh failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _tickBusy, 0);
        }
    }

    // ---------------- Overlay ----------------
    public void ToggleOverlay()
    {
        if (!_host.S.Overlay.Enabled)
        {
            ShowBanner("The overlay is turned off in Settings → Overlay.");
            return;
        }
        _overlayManuallyHidden = IsOverlayVisible;
        SetOverlay(!IsOverlayVisible);
    }

    public void ToggleOverlayEditMode()
    {
        _overlayEdit = !_overlayEdit;
        Overlay.IsEditMode = _overlayEdit;
        if (_overlayEdit) SetOverlay(true);
        _ui.SetOverlayEditMode(_overlayEdit);
        if (!_overlayEdit) UpdateOverlayAutoVisibility();
    }

    private void SetOverlay(bool visible)
    {
        IsOverlayVisible = visible;
        if (visible) Overlay.Update(_host.Fps.GetSnapshot(), _host.System.Latest);
        _ui.SetOverlayVisible(visible);
    }

    private void UpdateOverlayAutoVisibility()
    {
        var o = _host.S.Overlay;
        if (!o.Enabled)
        {
            if (IsOverlayVisible) SetOverlay(false);
            return;
        }
        if (_overlayEdit || !o.ShowAutomaticallyWhenGameRuns || _overlayManuallyHidden) return;

        var running = _host.Running.Running;
        var show = running.Any(r => _host.Library.Get(r.GameId)?.Profile.ShowOverlay ?? true);
        if (show != IsOverlayVisible) SetOverlay(show);
    }

    /// <summary>Applies settings that affect the shell (theme, overlay, hotkeys, startup).</summary>
    public void ApplySettings()
    {
        var s = _host.S;
        _ui.ApplyTheme(s.Theme, s.AccentColor, s.EnableAnimations);
        _ui.ApplyOverlaySettings(s.Overlay);
        Overlay.SettingsChanged();
        var hotkeyError = _ui.RegisterHotkeys(s.Overlay.ToggleHotkey, s.Overlay.EditModeHotkey, ToggleOverlay, ToggleOverlayEditMode);
        Settings.HotkeyStatus = hotkeyError ?? $"Active: {s.Overlay.ToggleHotkey} toggles the overlay, {s.Overlay.EditModeHotkey} moves it.";
        try
        {
            StartupRegistration.Apply(s.StartWithWindows, s.StartMinimized, Environment.ProcessPath ?? "");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _host.Log.Warn("Could not update start-with-Windows setting", ex);
        }
        UpdateOverlayAutoVisibility();
    }

    public void Dispose()
    {
        _tick?.Dispose();
        _tick = null;
    }
}
