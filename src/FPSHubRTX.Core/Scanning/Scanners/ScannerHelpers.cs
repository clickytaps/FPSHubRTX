using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

internal static class ScannerHelpers
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static Game NewGame(LauncherKind launcher, string sourceId, string name, string? installPath)
    {
        return new Game
        {
            Id = $"{launcher.ToString().ToLowerInvariant()}:{sourceId}",
            SourceId = sourceId,
            Launcher = launcher,
            Name = CleanName(name),
            InstallPath = installPath is null ? null : TrimPath(installPath),
            LastScannedUtc = DateTime.UtcNow,
        };
    }

    public static string TrimPath(string path)
    {
        var p = PathUtil.ToNative(path.Trim().Trim('"'));
        if (p.Length > 3) p = p.TrimEnd('\\', '/');
        return p;
    }

    public static string CleanName(string name)
    {
        var n = (name ?? "").Trim();
        // Strip trademark symbols that make sorting/searching awkward.
        n = n.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
        return n.Length == 0 ? "Unknown game" : n;
    }

    /// <summary>Sets Status/StatusDetail from the install folder and executable on disk.</summary>
    public static void ApplyInstallStatus(Game game, bool launcherSaysComplete = true)
    {
        if (string.IsNullOrWhiteSpace(game.InstallPath))
        {
            game.Status = game.LaunchMethods.Count > 0 ? InstallStatus.Installed : InstallStatus.Unknown;
            game.StatusDetail = game.LaunchMethods.Count > 0 ? null : "No install folder reported";
            return;
        }

        try
        {
            if (!Directory.Exists(game.InstallPath))
            {
                game.Status = InstallStatus.Missing;
                game.StatusDetail = "Install folder not found (drive disconnected or game moved)";
                return;
            }
            // Touch the folder to detect permission problems.
            using var _ = Directory.EnumerateFileSystemEntries(game.InstallPath).GetEnumerator();
        }
        catch (UnauthorizedAccessException)
        {
            game.Status = InstallStatus.Inaccessible;
            game.StatusDetail = "Install folder exists but access was denied";
            return;
        }
        catch (IOException ex)
        {
            game.Status = InstallStatus.Inaccessible;
            game.StatusDetail = "Install folder could not be read: " + ex.Message;
            return;
        }

        if (!launcherSaysComplete)
        {
            game.Status = InstallStatus.Incomplete;
            game.StatusDetail = "The launcher reports this install is incomplete (downloading or updating)";
            return;
        }

        game.Status = InstallStatus.Installed;
        game.StatusDetail = null;
    }

    /// <summary>Detects the main executable when the launcher didn't tell us.</summary>
    public static void DetectExecutable(Game game, ScanContext ctx)
    {
        if (!string.IsNullOrEmpty(game.ExecutablePath) && PathUtil.FileExistsSafe(game.ExecutablePath)) return;
        if (string.IsNullOrEmpty(game.InstallPath)) return;
        try
        {
            game.ExecutablePath = ExecutableFinder.FindMainExecutable(game.InstallPath, game.Name, ctx.Settings.ExecutableSearchDepth, ctx.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Log.Warn($"Executable detection failed for {game.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Uninstall registry entries (HKLM 64/32-bit and HKCU) whose publisher matches.
    /// Returns (subKeyName, displayName, installLocation, hive).
    /// </summary>
    public static IEnumerable<(string Key, string DisplayName, string? InstallLocation, RegHive Hive)> UninstallEntries(IRegistryReader reg, Func<string?, bool> publisherMatches)
    {
        foreach (var hive in new[] { RegHive.LocalMachine, RegHive.LocalMachine32, RegHive.CurrentUser })
        {
            foreach (var sub in reg.GetSubKeyNames(hive, UninstallKey))
            {
                var path = UninstallKey + "\\" + sub;
                var publisher = reg.GetString(hive, path, "Publisher");
                if (!publisherMatches(publisher)) continue;
                var name = reg.GetString(hive, path, "DisplayName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var system = reg.GetString(hive, path, "SystemComponent");
                if (system == "1") continue;
                yield return (sub, name!, reg.GetString(hive, path, "InstallLocation"), hive);
            }
        }
    }

    /// <summary>Executable direct-launch method rooted in the exe's folder.</summary>
    public static LaunchMethod? DirectExe(string? exePath, string? args = null, string? workingDir = null, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !PathUtil.IsSafeAbsolutePath(exePath)) return null;
        return LaunchMethod.Exe(exePath, args, workingDir ?? Path.GetDirectoryName(exePath), description ?? "Run the game executable directly");
    }
}
