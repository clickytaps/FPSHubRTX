using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// Standalone games: only looks in folders the user chose (Settings → Scan locations) and, when enabled,
/// the conventional "X:\Games" folder on each drive in scope. It never walks whole drives.
/// A chosen folder can either BE a game folder or CONTAIN game folders (one per subfolder).
/// </summary>
public sealed class FolderScanner : IGameScanner
{
    public LauncherKind Kind => LauncherKind.Standalone;
    public string DisplayName => "Standalone & custom folders";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind) { LauncherDetected = true };
        foreach (var root in GetRoots(ctx))
        {
            ctx.ThrowIfCancelled();
            if (ctx.IsExcluded(root)) continue;
            ctx.ReportLocation?.Invoke(root);

            if (!PathUtil.DirectoryExistsSafe(root))
            {
                result.Warnings.Add($"Folder not available: {root}");
                continue;
            }

            if (IsGameFolder(root, ctx))
            {
                var g = BuildGame(root, ctx);
                if (g is not null) result.Games.Add(g);
                continue;
            }

            ScanContainer(root, ctx, result, containerDepth: 0);
        }

        result.Completed = true;
        return result;
    }

    private static readonly string[] LauncherLibraryNames = { "steamapps", "SteamLibrary", "XboxGames", "__Installer", ".egstore" };

    private static void ScanContainer(string container, ScanContext ctx, ScannerResult result, int containerDepth)
    {
        foreach (var sub in PathUtil.EnumerateDirectoriesSafe(container))
        {
            ctx.ThrowIfCancelled();
            if (ctx.IsExcluded(sub)) continue;
            var subName = Path.GetFileName(sub);
            if (LauncherLibraryNames.Any(n => n.Equals(subName, StringComparison.OrdinalIgnoreCase))) continue;
            if (PathUtil.DirectoryExistsSafe(Path.Combine(sub, "steamapps"))) continue; // a Steam library — handled by the Steam scanner
            ctx.ReportLocation?.Invoke(sub);

            // A folder such as "Epic Games" or "GOG Games" that holds several games: look one level deeper.
            if (containerDepth < 1 && !ExecutableFinder.LooksLikeGameFolder(sub)
                && PathUtil.EnumerateDirectoriesSafe(sub).Count(ExecutableFinder.LooksLikeGameFolder) >= 2)
            {
                ScanContainer(sub, ctx, result, containerDepth + 1);
                continue;
            }

            var g = BuildGame(sub, ctx);
            if (g is not null) result.Games.Add(g);
        }
    }

    internal static List<string> GetRoots(ScanContext ctx)
    {
        var roots = new List<string>();
        foreach (var folder in ctx.Settings.CustomFolders)
        {
            if (!string.IsNullOrWhiteSpace(folder) && PathUtil.IsSafeAbsolutePath(folder)) roots.Add(folder.Trim());
        }
        if (ctx.Settings.ScanCommonGameFolders)
        {
            foreach (var drive in ctx.ScanDrives)
            {
                var games = Path.Combine(drive.RootPath, "Games");
                if (PathUtil.DirectoryExistsSafe(games)) roots.Add(games);
            }
        }
        return roots.DistinctBy(PathUtil.NormalizeKey).ToList();
    }

    /// <summary>A folder is itself a game when it has engine/store markers and an executable at the top level.</summary>
    private static bool IsGameFolder(string dir, ScanContext ctx)
        => ExecutableFinder.LooksLikeGameFolder(dir)
           && PathUtil.EnumerateFilesSafe(dir, "*.exe").Any(f => !ExecutableFinder.IsExcludedExecutableName(f));

    private static Game? BuildGame(string dir, ScanContext ctx)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        string? exe;
        try
        {
            exe = ExecutableFinder.FindMainExecutable(dir, name, ctx.Settings.ExecutableSearchDepth, ctx.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (exe is null) return null;

        var game = ScannerHelpers.NewGame(LauncherKind.Standalone, PathUtil.StableHash(PathUtil.NormalizeKey(dir)), PrettifyFolderName(name), dir);
        game.ExecutablePath = exe;
        var method = ScannerHelpers.DirectExe(exe, description: "Run executable");
        if (method is not null) game.LaunchMethods.Add(method);

        // GOG offline installers outside the registry still have goggame-*.info with a proper play task.
        var gogTask = GogScanner.ReadPlayTask(dir, "*");
        if (gogTask?.Exe is { } gogExe && PathUtil.FileExistsSafe(gogExe))
        {
            game.ExecutablePath = gogExe;
            game.LaunchMethods.Clear();
            game.LaunchMethods.Add(LaunchMethod.Exe(gogExe, gogTask.Value.Args, gogTask.Value.WorkingDir ?? Path.GetDirectoryName(gogExe), "Run executable (GOG play task)"));
        }

        ScannerHelpers.ApplyInstallStatus(game);
        return game;
    }

    internal static string PrettifyFolderName(string name)
    {
        var n = name.Replace('_', ' ').Trim();
        while (n.Contains("  ", StringComparison.Ordinal)) n = n.Replace("  ", " ", StringComparison.Ordinal);
        return n.Length == 0 ? name : n;
    }
}
