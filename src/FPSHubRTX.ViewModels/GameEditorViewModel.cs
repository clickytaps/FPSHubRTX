using System.Windows.Input;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Mvvm;
using FPSHubRTX.Core.Platform;
using LP = FPSHubRTX.Core.Models.LaunchPreference;
using OL = FPSHubRTX.Core.Models.OverlayLayout;

namespace FPSHubRTX.ViewModels;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Per-game profile editor (name, launch method, arguments, overlay, tracking, artwork, notes).</summary>
public sealed class GameEditorViewModel : ObservableObject
{
    private readonly Game _original;
    private readonly IPlatformUi _ui;
    private string _customName;
    private string _arguments;
    private string _customExe;
    private string _customCover;
    private string _notes;
    private string _troubleshooting;
    private string _collections;
    private Option<LaunchPreference> _launchPreference;
    private Option<OverlayLayout?> _overlayLayout;
    private Option<bool?> _showOverlay;
    private bool _autoCapture;
    private bool _trackSessions;
    private bool _keepFrameLog;
    private string? _error;

    public GameEditorViewModel(Game game, IReadOnlyList<string> existingCollections, IPlatformUi ui)
    {
        _original = game;
        _ui = ui;
        _customName = game.CustomName ?? "";
        _arguments = game.CustomArguments ?? "";
        _customExe = game.CustomExecutablePath ?? "";
        _customCover = game.CustomCoverPath ?? "";
        _notes = game.Notes ?? "";
        _troubleshooting = game.Profile.TroubleshootingNotes ?? "";
        _collections = string.Join(", ", game.Collections);
        ExistingCollections = existingCollections;

        LaunchPreferences = new[]
        {
            new Option<LaunchPreference>(LP.Automatic, "Automatic (recommended)"),
            new Option<LaunchPreference>(LP.OfficialLauncher, "Always use the official launcher"),
            new Option<LaunchPreference>(LP.DirectExecutable, "Run executable directly"),
        };
        _launchPreference = LaunchPreferences.First(o => o.Value == game.LaunchPreference);

        OverlayLayouts = new[]
        {
            new Option<OverlayLayout?>(null, "Use global setting"),
            new Option<OverlayLayout?>(OL.FpsOnly, "FPS only"),
            new Option<OverlayLayout?>(OL.Compact, "Compact"),
            new Option<OverlayLayout?>(OL.Full, "Full performance"),
            new Option<OverlayLayout?>(OL.Graph, "Graph"),
        };
        _overlayLayout = OverlayLayouts.First(o => o.Value == game.Profile.OverlayLayout);

        ShowOverlayOptions = new[]
        {
            new Option<bool?>(null, "Use global setting"),
            new Option<bool?>(true, "Show while this game runs"),
            new Option<bool?>(false, "Never show for this game"),
        };
        _showOverlay = ShowOverlayOptions.First(o => o.Value == game.Profile.ShowOverlay);

        _autoCapture = game.Profile.AutoCaptureFps;
        _trackSessions = game.Profile.TrackSessions;
        _keepFrameLog = game.Profile.KeepFrameLog;

        BrowseExeCommand = new RelayCommand(BrowseExe);
        ClearExeCommand = new RelayCommand(() => CustomExecutablePath = "");
        BrowseCoverCommand = new RelayCommand(BrowseCover);
        ClearCoverCommand = new RelayCommand(() => CustomCoverPath = "");
    }

    public string Title => $"Game settings — {_original.DisplayName}";
    public string DetectedName => _original.Name;
    public string DisplayName => string.IsNullOrWhiteSpace(_customName) ? _original.Name : _customName.Trim();
    public string LauncherName => Format.Launcher(_original.Launcher);
    public string InstallPath => _original.InstallPath ?? "—";
    public string DetectedExecutable => _original.ExecutablePath ?? "Not detected";
    public IReadOnlyList<string> LaunchMethodDescriptions => _original.LaunchMethods.Select(m => $"{m.Description}  [{m.Kind}]").DefaultIfEmpty("No launch method detected").ToList();
    public IReadOnlyList<string> ExistingCollections { get; }

    public IReadOnlyList<Option<LaunchPreference>> LaunchPreferences { get; }
    public IReadOnlyList<Option<OverlayLayout?>> OverlayLayouts { get; }
    public IReadOnlyList<Option<bool?>> ShowOverlayOptions { get; }

    public string CustomName { get => _customName; set => Set(ref _customName, value); }
    public string CustomArguments { get => _arguments; set => Set(ref _arguments, value); }
    public string CustomExecutablePath { get => _customExe; set => Set(ref _customExe, value); }
    public string CustomCoverPath { get => _customCover; set => Set(ref _customCover, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public string TroubleshootingNotes { get => _troubleshooting; set => Set(ref _troubleshooting, value); }
    public string CollectionsText { get => _collections; set => Set(ref _collections, value); }
    public Option<LaunchPreference> LaunchPreference { get => _launchPreference; set => Set(ref _launchPreference, value); }
    public Option<OverlayLayout?> OverlayLayout { get => _overlayLayout; set => Set(ref _overlayLayout, value); }
    public Option<bool?> ShowOverlay { get => _showOverlay; set => Set(ref _showOverlay, value); }
    public bool AutoCaptureFps { get => _autoCapture; set => Set(ref _autoCapture, value); }
    public bool TrackSessions { get => _trackSessions; set => Set(ref _trackSessions, value); }
    public bool KeepFrameLog { get => _keepFrameLog; set => Set(ref _keepFrameLog, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public ICommand BrowseExeCommand { get; }
    public ICommand ClearExeCommand { get; }
    public ICommand BrowseCoverCommand { get; }
    public ICommand ClearCoverCommand { get; }

    public IReadOnlyList<string> NewCollections => ParseCollections().Where(c => !ExistingCollections.Contains(c, StringComparer.CurrentCultureIgnoreCase)).ToList();

    private void BrowseExe()
    {
        var file = _ui.PickFile("Choose the game's executable", "Programs (*.exe)|*.exe");
        if (file is not null) CustomExecutablePath = file;
    }

    private void BrowseCover()
    {
        var file = _ui.PickFile("Choose cover art", "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp");
        if (file is not null) CustomCoverPath = file;
    }

    private List<string> ParseCollections()
        => CollectionsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.Length is > 0 and <= 60)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Validates input; returns false and sets <see cref="Error"/> when something is wrong.</summary>
    public bool Validate()
    {
        Error = null;
        if (CustomName.Length > 200) Error = "The name is too long.";
        else if (CustomArguments.Any(c => c is '\r' or '\n' or '\0')) Error = "Launch arguments must be on one line.";
        else if (CustomArguments.Length > 2000) Error = "Launch arguments are too long.";
        else if (CustomExecutablePath.Length > 0 && (!PathUtil.IsSafeAbsolutePath(CustomExecutablePath) || !CustomExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            Error = "The custom executable must be a full path to a .exe file.";
        else if (CustomExecutablePath.Length > 0 && !PathUtil.FileExistsSafe(CustomExecutablePath)) Error = "The custom executable doesn't exist.";
        else if (CustomCoverPath.Length > 0 && !PathUtil.FileExistsSafe(CustomCoverPath)) Error = "The cover image doesn't exist.";
        return Error is null;
    }

    public void ApplyTo(Game g)
    {
        g.CustomName = string.IsNullOrWhiteSpace(CustomName) || CustomName.Trim() == g.Name ? null : CustomName.Trim();
        g.CustomArguments = string.IsNullOrWhiteSpace(CustomArguments) ? null : CustomArguments.Trim();
        g.CustomExecutablePath = string.IsNullOrWhiteSpace(CustomExecutablePath) ? null : CustomExecutablePath.Trim();
        g.CustomCoverPath = string.IsNullOrWhiteSpace(CustomCoverPath) ? null : CustomCoverPath.Trim();
        g.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes;
        g.Collections = ParseCollections();
        g.LaunchPreference = LaunchPreference.Value;
        g.Profile.OverlayLayout = OverlayLayout.Value;
        g.Profile.ShowOverlay = ShowOverlay.Value;
        g.Profile.AutoCaptureFps = AutoCaptureFps;
        g.Profile.TrackSessions = TrackSessions;
        g.Profile.KeepFrameLog = KeepFrameLog;
        g.Profile.TroubleshootingNotes = string.IsNullOrWhiteSpace(TroubleshootingNotes) ? null : TroubleshootingNotes;
    }
}
