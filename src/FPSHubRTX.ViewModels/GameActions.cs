using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Services;

namespace FPSHubRTX.ViewModels;

/// <summary>Shell-level operations page view models need (navigation and the message banner).</summary>
public interface IShell
{
    void Navigate(string pageKey);
    void ShowBanner(string message, bool isError = false);
    GameCardViewModel? FindCard(string gameId);
}

/// <summary>Actions available on a game card. Nothing here modifies or deletes game files.</summary>
public sealed class GameActions
{
    private readonly AppHost _host;
    private readonly IPlatformUi _ui;
    private readonly IShell _shell;

    public GameActions(AppHost host, IPlatformUi ui, IShell shell)
    {
        _host = host;
        _ui = ui;
        _shell = shell;
    }

    public async void Play(GameCardViewModel card)
    {
        var game = _host.Library.Get(card.Id);
        if (game is null) return;
        if (_host.Running.IsRunning(game.Id))
        {
            _shell.ShowBanner($"{game.DisplayName} is already running.");
            return;
        }

        card.IsLaunching = true;
        var result = await Task.Run(() => _host.Launcher.Launch(game));
        if (!result.Started && result.RequiresElevation &&
            _ui.Confirm("Administrator rights requested", $"{game.DisplayName} asks to run as administrator.\n\nLaunch it with administrator rights? (Windows will show a permission prompt.)"))
        {
            result = await Task.Run(() => _host.Launcher.Launch(game, runElevated: true));
        }

        if (!result.Started)
        {
            card.IsLaunching = false;
            _shell.ShowBanner($"Couldn't launch {game.DisplayName}: {result.Error}", isError: true);
            _host.Notifications.Notify(NotificationKind.GameLaunchFailed, "Launch failed", $"{game.DisplayName}: {result.Error}", NotificationLevel.Error);
            return;
        }

        if (result.Plan?.Warnings.Count > 0) _shell.ShowBanner(string.Join(" ", result.Plan.Warnings));
        // IsLaunching is cleared when the game's process is detected (or the detection times out).
    }

    public void ToggleFavorite(GameCardViewModel card)
        => _host.Library.Update(card.Id, g => g.IsFavorite = !g.IsFavorite);

    public void OpenFolder(GameCardViewModel card)
    {
        if (!ShellService.OpenFolder(card.InstallPath, out var error)) _shell.ShowBanner(error ?? "Couldn't open the folder.", true);
    }

    public void OpenLauncherPage(GameCardViewModel card)
    {
        if (!ShellService.OpenUri(card.Game.LauncherPageUri, out var error)) _shell.ShowBanner(error ?? "Couldn't open the launcher.", true);
    }

    public void VerifyFiles(GameCardViewModel card)
    {
        if (!_ui.Confirm("Verify game files", $"Ask {Format.Launcher(card.Launcher)} to verify the files of {card.Name}?\n\nThe official launcher performs the check; FPS Hub doesn't touch game files.")) return;
        if (!ShellService.OpenUri(card.Game.VerifyFilesUri, out var error)) _shell.ShowBanner(error ?? "Couldn't contact the launcher.", true);
    }

    public void Edit(GameCardViewModel card)
    {
        var game = _host.Library.Get(card.Id);
        if (game is null) return;
        var editor = new GameEditorViewModel(game, _host.Library.GetCollections(), _ui);
        if (!_ui.EditGame(editor)) return;
        _host.Library.Update(card.Id, editor.ApplyTo);
        var cover = editor.CustomCoverPath.Trim();
        if (cover.Length > 0 && !PathUtil.IsUnder(cover, _host.Paths.CoversDirectory)) ImportCover(card, cover);
        foreach (var c in editor.NewCollections) _host.Library.AddCollection(c);
        _host.Log.Info($"Edited game settings for {editor.DisplayName}");
    }

    public void Remove(GameCardViewModel card)
    {
        if (!_ui.Confirm("Remove from library",
                $"Remove {card.Name} from your FPS Hub library?\n\nThis only removes the entry. The game stays installed and no files are deleted. It will reappear if you scan again and it is still installed (unless you exclude its folder).")) return;
        _host.Library.Remove(card.Id);
        _shell.ShowBanner($"{card.Name} was removed from the library (the game itself is untouched).");
    }

    public void Locate(GameCardViewModel card)
    {
        var folder = _ui.PickFolder($"Select the folder where {card.Name} is now installed");
        if (folder is null) return;
        if (_host.Library.Relocate(card.Id, folder, _host.S.Scan.ExecutableSearchDepth))
        {
            var updated = _host.Library.Get(card.Id);
            if (updated?.EffectiveExecutablePath is null)
                _shell.ShowBanner($"Folder updated, but no executable was found for {card.Name}. Choose it manually.");
            else if (card.Launcher is not (LauncherKind.Standalone or LauncherKind.Manual))
                _shell.ShowBanner($"Folder updated. Because {card.Name} belongs to {Format.Launcher(card.Launcher)}, also point that launcher at the new location, then rescan.");
            else
                _shell.ShowBanner($"{card.Name} now points to {folder}.");
        }
        else
        {
            _shell.ShowBanner("That folder isn't accessible.", true);
        }
    }

    public void ChooseExecutable(GameCardViewModel card)
    {
        var exe = _ui.PickFile($"Choose the executable for {card.Name}", "Programs (*.exe)|*.exe");
        if (exe is null) return;
        if (!PathUtil.IsSafeAbsolutePath(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            _shell.ShowBanner("Please choose a .exe file.", true);
            return;
        }
        _host.Library.Update(card.Id, g => g.CustomExecutablePath = exe);
        _shell.ShowBanner($"{card.Name} will use {Path.GetFileName(exe)}. Set 'Launch method' to 'Run executable directly' in its settings if the launcher should be skipped.");
    }

    public void ChangeCover(GameCardViewModel card)
    {
        var image = _ui.PickFile($"Choose cover art for {card.Name}", "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp");
        if (image is null) return;
        ImportCover(card, image);
    }

    private void ImportCover(GameCardViewModel card, string image)
    {
        try
        {
            var info = new FileInfo(image);
            if (info.Length > 20 * 1024 * 1024)
            {
                _shell.ShowBanner("That image is larger than 20 MB.", true);
                return;
            }
            // Copy into the app's covers folder so the library doesn't depend on the original file.
            var dest = Path.Combine(_host.Paths.CoversDirectory, $"custom-{PathUtil.StableHash(card.Id)}{info.Extension.ToLowerInvariant()}");
            File.Copy(image, dest, overwrite: true);
            _host.Library.Update(card.Id, g => g.CustomCoverPath = dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Log.Warn("Cover import failed", ex);
            _shell.ShowBanner("Couldn't import that image: " + ex.Message, true);
        }
    }

    public void ShowFps(GameCardViewModel card) => _shell.Navigate(Pages.Fps);
}
