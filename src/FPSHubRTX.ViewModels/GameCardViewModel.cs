using System.Windows.Input;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Mvvm;

namespace FPSHubRTX.ViewModels;

/// <summary>One game card in the library / home page.</summary>
public sealed class GameCardViewModel : ObservableObject
{
    private Game _game;
    private bool _isRunning;
    private bool _isLaunching;
    private string _currentFps = "";

    public GameCardViewModel(Game game, GameActions actions)
    {
        _game = game;
        PlayCommand = new RelayCommand(() => actions.Play(this), () => !IsLaunching);
        FavoriteCommand = new RelayCommand(() => actions.ToggleFavorite(this));
        OpenFolderCommand = new RelayCommand(() => actions.OpenFolder(this));
        OpenLauncherPageCommand = new RelayCommand(() => actions.OpenLauncherPage(this), () => HasLauncherPage);
        VerifyFilesCommand = new RelayCommand(() => actions.VerifyFiles(this), () => CanVerify);
        EditCommand = new RelayCommand(() => actions.Edit(this));
        RemoveCommand = new RelayCommand(() => actions.Remove(this));
        LocateCommand = new RelayCommand(() => actions.Locate(this));
        ChooseExecutableCommand = new RelayCommand(() => actions.ChooseExecutable(this));
        ChangeCoverCommand = new RelayCommand(() => actions.ChangeCover(this));
        MeasureFpsCommand = new RelayCommand(() => actions.ShowFps(this));
    }

    public Game Game => _game;
    public string Id => _game.Id;
    public string Name => _game.DisplayName;
    public string? CoverPath => _game.EffectiveCoverPath;
    public bool HasCover => !string.IsNullOrWhiteSpace(CoverPath);
    public string Initials => Format.Initials(Name);
    public string PlaceholderColor => Format.PlaceholderColor(Name);
    public LauncherKind Launcher => _game.Launcher;
    public string LauncherName => Format.Launcher(_game.Launcher);
    public InstallStatus Status => _game.Status;
    public string StatusText => Format.Status(_game.Status);
    public string? StatusDetail => _game.StatusDetail;
    public bool IsInstalled => _game.Status == InstallStatus.Installed;
    public bool HasProblem => _game.Status is InstallStatus.Missing or InstallStatus.Inaccessible;
    public string Drive => _game.InstallDrive ?? "—";
    public string? InstallPath => _game.InstallPath;
    public string LastPlayedText => Format.Relative(_game.LastPlayedUtc);
    public string PlaytimeText => _game.TotalPlaytimeSeconds <= 0 ? "No playtime yet" : Format.Duration(_game.TotalPlaytime);
    public bool IsFavorite => _game.IsFavorite;
    public string CollectionsText => string.Join(", ", _game.Collections);
    public bool HasLauncherPage => !string.IsNullOrWhiteSpace(_game.LauncherPageUri);
    public bool CanVerify => !string.IsNullOrWhiteSpace(_game.VerifyFilesUri);
    public DateTime? LastPlayedUtc => _game.LastPlayedUtc;
    public double PlaytimeSeconds => _game.TotalPlaytimeSeconds;

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (Set(ref _isRunning, value))
            {
                Raise(nameof(PlayButtonText));
                if (!value) CurrentFpsText = "";
            }
        }
    }

    public bool IsLaunching
    {
        get => _isLaunching;
        set
        {
            if (Set(ref _isLaunching, value))
            {
                Raise(nameof(PlayButtonText));
                ((RelayCommand)PlayCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string PlayButtonText => IsLaunching ? "LAUNCHING…" : IsRunning ? "RUNNING" : "PLAY";

    /// <summary>Live measured FPS while running (empty when not measured — never a placeholder number).</summary>
    public string CurrentFpsText
    {
        get => _currentFps;
        set
        {
            if (Set(ref _currentFps, value)) Raise(nameof(HasCurrentFps));
        }
    }

    public bool HasCurrentFps => !string.IsNullOrEmpty(_currentFps);

    public ICommand PlayCommand { get; }
    public ICommand FavoriteCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenLauncherPageCommand { get; }
    public ICommand VerifyFilesCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand LocateCommand { get; }
    public ICommand ChooseExecutableCommand { get; }
    public ICommand ChangeCoverCommand { get; }
    public ICommand MeasureFpsCommand { get; }

    /// <summary>Refreshes from an updated library entry without recreating the card.</summary>
    public void Update(Game game)
    {
        _game = game;
        OnPropertyChanged(string.Empty); // all properties
        ((RelayCommand)OpenLauncherPageCommand).RaiseCanExecuteChanged();
        ((RelayCommand)VerifyFilesCommand).RaiseCanExecuteChanged();
    }
}
