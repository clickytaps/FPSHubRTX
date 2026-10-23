using System.Collections.ObjectModel;
using System.Windows.Input;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Mvvm;
using FPSHubRTX.Core.Statistics;

namespace FPSHubRTX.ViewModels;

public static class Pages
{
    public const string Home = "Home";
    public const string Games = "Games";
    public const string Scan = "Scan";
    public const string Fps = "Fps";
    public const string Performance = "Performance";
    public const string Benchmarks = "Benchmarks";
    public const string Sessions = "Sessions";
    public const string Settings = "Settings";
}

public sealed class HomeViewModel : ObservableObject
{
    private readonly AppHost _host;
    private int _gameCount, _runningCount, _installedCount, _launcherCount;
    private string _averageFps = Format.NoData, _averageFpsCaption = "", _today = "0m", _lastScan = "";
    private string _liveFps = "", _liveFpsGame = "";

    public HomeViewModel(AppHost host, IShell shell, Func<string, GameCardViewModel?> cardLookup)
    {
        _host = host;
        CardLookup = cardLookup;
        ScanCommand = new RelayCommand(() => shell.Navigate(Pages.Scan + ":start"));
        OpenLibraryCommand = new RelayCommand(() => shell.Navigate(Pages.Games));
        OpenFpsCommand = new RelayCommand(() => shell.Navigate(Pages.Fps));
        OpenStatsCommand = new RelayCommand(() => shell.Navigate(Pages.Sessions));
    }

    private Func<string, GameCardViewModel?> CardLookup { get; }

    public int GameCount { get => _gameCount; private set { if (Set(ref _gameCount, value)) Raise(nameof(HasGames), nameof(NoGames)); } }
    public int RunningCount { get => _runningCount; private set => Set(ref _runningCount, value); }
    public int InstalledCount { get => _installedCount; private set => Set(ref _installedCount, value); }
    public int LauncherCount { get => _launcherCount; private set => Set(ref _launcherCount, value); }
    public bool HasGames => GameCount > 0;
    public bool NoGames => GameCount == 0;

    /// <summary>Average of measured session FPS over 30 days, or a dash when nothing was measured.</summary>
    public string AverageFpsText { get => _averageFps; private set => Set(ref _averageFps, value); }
    public string AverageFpsCaption { get => _averageFpsCaption; private set => Set(ref _averageFpsCaption, value); }
    public string TodayPlaytime { get => _today; private set => Set(ref _today, value); }
    public string LastScanText { get => _lastScan; private set => Set(ref _lastScan, value); }
    public string LiveFpsText { get => _liveFps; set { if (Set(ref _liveFps, value)) Raise(nameof(HasLiveFps)); } }
    public string LiveFpsGame { get => _liveFpsGame; set => Set(ref _liveFpsGame, value); }
    public bool HasLiveFps => !string.IsNullOrEmpty(_liveFps);

    public ObservableCollection<GameCardViewModel> RecentGames { get; } = new();
    public ObservableCollection<GameCardViewModel> RunningGames { get; } = new();

    public ICommand ScanCommand { get; }
    public ICommand OpenLibraryCommand { get; }
    public ICommand OpenFpsCommand { get; }
    public ICommand OpenStatsCommand { get; }

    public void Refresh()
    {
        var games = _host.Library.GetAll();
        GameCount = games.Count;
        InstalledCount = games.Count(g => g.Status == InstallStatus.Installed);
        LauncherCount = games.Select(g => g.Launcher).Distinct().Count();
        LastScanText = _host.Library.LastScanUtc is { } t ? "Last scan: " + Format.Relative(t) : "Not scanned yet";

        var report = PlaytimeStatistics.Build(_host.Sessions.GetAll(), DateTime.UtcNow);
        TodayPlaytime = Format.Duration(report.Today);
        if (report.AverageMeasuredFps is { } avg)
        {
            AverageFpsText = Format.Fps(avg);
            AverageFpsCaption = $"Average of {report.MeasuredSessionCount} measured session{(report.MeasuredSessionCount == 1 ? "" : "s")} (30 days)";
        }
        else
        {
            AverageFpsText = Format.NoData;
            AverageFpsCaption = "No FPS measured yet — play a game to collect data";
        }

        Sync(RecentGames, games.Where(g => g.LastPlayedUtc is not null).OrderByDescending(g => g.LastPlayedUtc).Take(6).Select(g => g.Id));
        RefreshRunning();
    }

    public void RefreshRunning()
    {
        var running = _host.Running.Running;
        RunningCount = running.Count;
        Sync(RunningGames, running.Select(r => r.GameId));
    }

    private void Sync(ObservableCollection<GameCardViewModel> target, IEnumerable<string> ids)
    {
        var cards = ids.Select(CardLookup).Where(c => c is not null).Cast<GameCardViewModel>().ToList();
        if (target.SequenceEqual(cards)) return;
        target.Clear();
        foreach (var c in cards) target.Add(c);
    }
}

public sealed class GamesViewModel : ObservableObject
{
    public const string AllOption = "All";

    private readonly AppHost _host;
    private readonly IPlatformUi _ui;
    private readonly IShell _shell;
    private readonly GameActions _actions;
    private readonly Dictionary<string, GameCardViewModel> _cards = new(StringComparer.OrdinalIgnoreCase);
    private string _search = "";
    private string _launcherFilter = AllOption, _driveFilter = AllOption, _collectionFilter = AllOption;
    private string _sort = "Name";
    private bool _favoritesOnly;
    private bool _showMissing = true;
    private int _shown, _total;

    public GamesViewModel(AppHost host, IPlatformUi ui, IShell shell, GameActions actions)
    {
        _host = host;
        _ui = ui;
        _shell = shell;
        _actions = actions;
        AddGameCommand = new RelayCommand(AddGame);
        NewCollectionCommand = new RelayCommand(NewCollection);
        DeleteCollectionCommand = new RelayCommand(DeleteCollection, () => CollectionFilter != AllOption);
        ScanCommand = new RelayCommand(() => shell.Navigate(Pages.Scan + ":start"));
        ClearFiltersCommand = new RelayCommand(() =>
        {
            _search = "";
            _launcherFilter = _driveFilter = _collectionFilter = AllOption;
            _favoritesOnly = false;
            OnPropertyChanged(string.Empty);
            ApplyFilter();
        });
    }

    public ObservableCollection<GameCardViewModel> Games { get; } = new();
    public ObservableCollection<string> LauncherOptions { get; } = new() { AllOption };
    public ObservableCollection<string> DriveOptions { get; } = new() { AllOption };
    public ObservableCollection<string> CollectionOptions { get; } = new() { AllOption };
    public IReadOnlyList<string> SortOptions { get; } = new[] { "Name", "Recently played", "Most played", "Favourites first" };

    public string SearchText { get => _search; set { if (Set(ref _search, value)) ApplyFilter(); } }
    public string LauncherFilter { get => _launcherFilter; set { if (Set(ref _launcherFilter, value ?? AllOption)) ApplyFilter(); } }
    public string DriveFilter { get => _driveFilter; set { if (Set(ref _driveFilter, value ?? AllOption)) ApplyFilter(); } }
    public string CollectionFilter
    {
        get => _collectionFilter;
        set
        {
            if (Set(ref _collectionFilter, value ?? AllOption))
            {
                ApplyFilter();
                ((RelayCommand)DeleteCollectionCommand).RaiseCanExecuteChanged();
            }
        }
    }
    public string SortOption { get => _sort; set { if (Set(ref _sort, value ?? "Name")) ApplyFilter(); } }
    public bool FavoritesOnly { get => _favoritesOnly; set { if (Set(ref _favoritesOnly, value)) ApplyFilter(); } }
    public bool ShowMissing { get => _showMissing; set { if (Set(ref _showMissing, value)) ApplyFilter(); } }
    public int ShownCount { get => _shown; private set => Set(ref _shown, value); }
    public int TotalCount { get => _total; private set { if (Set(ref _total, value)) Raise(nameof(LibraryEmpty)); } }
    public bool LibraryEmpty => TotalCount == 0;
    public bool NothingMatches => TotalCount > 0 && ShownCount == 0;

    public ICommand AddGameCommand { get; }
    public ICommand NewCollectionCommand { get; }
    public ICommand DeleteCollectionCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand ClearFiltersCommand { get; }

    public GameCardViewModel? GetCard(string id) => _cards.TryGetValue(id, out var c) ? c : null;

    public IReadOnlyCollection<GameCardViewModel> AllCards => _cards.Values;

    /// <summary>Re-reads the library, reusing existing cards so running/launching state is kept.</summary>
    public void Refresh()
    {
        var games = _host.Library.GetAll();
        var ids = new HashSet<string>(games.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _cards.Keys.Where(k => !ids.Contains(k)).ToList()) _cards.Remove(stale);
        foreach (var g in games)
        {
            if (_cards.TryGetValue(g.Id, out var card)) card.Update(g);
            else _cards[g.Id] = new GameCardViewModel(g, _actions);
        }
        TotalCount = games.Count;

        SyncOptions(LauncherOptions, games.Select(g => Format.Launcher(g.Launcher)));
        SyncOptions(DriveOptions, games.Select(g => g.InstallDrive).Where(d => d is not null)!);
        SyncOptions(CollectionOptions, _host.Library.GetCollections());
        if (!LauncherOptions.Contains(LauncherFilter)) _launcherFilter = AllOption;
        if (!DriveOptions.Contains(DriveFilter)) _driveFilter = AllOption;
        if (!CollectionOptions.Contains(CollectionFilter)) _collectionFilter = AllOption;
        Raise(nameof(LauncherFilter), nameof(DriveFilter), nameof(CollectionFilter));
        ApplyFilter();
    }

    private static void SyncOptions(ObservableCollection<string> target, IEnumerable<string?> values)
    {
        var wanted = new[] { AllOption }.Concat(values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)).ToList();
        if (target.SequenceEqual(wanted)) return;
        target.Clear();
        foreach (var w in wanted) target.Add(w);
    }

    public void ApplyFilter()
    {
        IEnumerable<GameCardViewModel> q = _cards.Values;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            q = q.Where(c => c.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase) || c.LauncherName.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        }
        if (LauncherFilter != AllOption) q = q.Where(c => c.LauncherName == LauncherFilter);
        if (DriveFilter != AllOption) q = q.Where(c => c.Drive.Equals(DriveFilter, StringComparison.OrdinalIgnoreCase));
        if (CollectionFilter != AllOption) q = q.Where(c => c.Game.Collections.Contains(CollectionFilter, StringComparer.CurrentCultureIgnoreCase));
        if (FavoritesOnly) q = q.Where(c => c.IsFavorite);
        if (!ShowMissing) q = q.Where(c => !c.HasProblem);

        q = SortOption switch
        {
            "Recently played" => q.OrderByDescending(c => c.LastPlayedUtc ?? DateTime.MinValue).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            "Most played" => q.OrderByDescending(c => c.PlaytimeSeconds).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            "Favourites first" => q.OrderByDescending(c => c.IsFavorite).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var list = q.ToList();
        if (!Games.SequenceEqual(list))
        {
            Games.Clear();
            foreach (var c in list) Games.Add(c);
        }
        ShownCount = list.Count;
        Raise(nameof(NothingMatches));
    }

    private void AddGame()
    {
        var exe = _ui.PickFile("Choose a game's executable to add it to your library", "Programs (*.exe)|*.exe");
        if (exe is null) return;
        try
        {
            var game = _host.Library.AddManual(exe);
            _shell.ShowBanner($"{game.Name} was added. Open its settings to rename it or add launch arguments.");
        }
        catch (ArgumentException ex)
        {
            _shell.ShowBanner(ex.Message, true);
        }
    }

    private void NewCollection()
    {
        var name = _ui.Prompt("New collection", "Collection name:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        _host.Library.AddCollection(name);
        _shell.ShowBanner($"Collection '{name.Trim()}' created. Add games to it from each game's settings.");
    }

    private void DeleteCollection()
    {
        if (CollectionFilter == AllOption) return;
        if (!_ui.Confirm("Delete collection", $"Delete the collection '{CollectionFilter}'? Games stay in your library.")) return;
        _host.Library.RemoveCollection(CollectionFilter);
        CollectionFilter = AllOption;
    }
}
