using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// Steam: finds the Steam install from the registry, reads steamapps\libraryfolders.vdf to get every
/// library on every drive, then reads each appmanifest_*.acf. Launches through steam://rungameid so
/// Steam handles DRM, updates and cloud saves.
/// </summary>
public sealed class SteamScanner : IGameScanner
{
    // Steam "apps" that are runtimes/tools rather than games.
    private static readonly HashSet<string> ToolAppIds = new()
    {
        "228980",  // Steamworks Common Redistributables
        "1070560", // Steam Linux Runtime
        "1391110", // Steam Linux Runtime - soldier
        "1628350", // Steam Linux Runtime - sniper
        "1493710", // Proton Experimental
        "2180100", // Proton Hotfix
        "1826330", // Proton EasyAntiCheat Runtime
        "1161040", // Proton BattlEye Runtime
        "250820",  // SteamVR
    };

    public LauncherKind Kind => LauncherKind.Steam;
    public string DisplayName => "Steam";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind);
        var steamRoot = FindSteamRoot(ctx);
        if (steamRoot is null)
        {
            result.Completed = true;
            return result;
        }
        result.LauncherDetected = true;

        var libraries = GetLibraryFolders(steamRoot, ctx, result);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var library in libraries)
        {
            ctx.ThrowIfCancelled();
            if (ctx.IsExcluded(library)) continue;
            var steamapps = Path.Combine(library, "steamapps");
            ctx.ReportLocation?.Invoke(steamapps);
            if (!PathUtil.DirectoryExistsSafe(steamapps))
            {
                result.Warnings.Add($"Steam library not reachable: {library} (drive disconnected?)");
                continue;
            }

            foreach (var manifest in PathUtil.EnumerateFilesSafe(steamapps, "appmanifest_*.acf"))
            {
                ctx.ThrowIfCancelled();
                var game = ReadManifest(manifest, library, steamRoot, ctx, result);
                if (game is not null && seen.Add(game.SourceId)) result.Games.Add(game);
            }
        }

        result.Completed = true;
        return result;
    }

    internal static string? FindSteamRoot(ScanContext ctx)
    {
        var candidates = new[]
        {
            ctx.Registry.GetString(RegHive.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            ctx.Registry.GetString(RegHive.LocalMachine32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            ctx.Registry.GetString(RegHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            string.IsNullOrEmpty(ctx.Environment.ProgramFilesX86) ? null : Path.Combine(ctx.Environment.ProgramFilesX86, "Steam"),
        };
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var path = ScannerHelpers.TrimPath(c);
            if (PathUtil.IsSafeAbsolutePath(path) && PathUtil.DirectoryExistsSafe(Path.Combine(path, "steamapps"))) return path;
        }
        return null;
    }

    internal static List<string> GetLibraryFolders(string steamRoot, ScanContext ctx, ScannerResult result)
    {
        var libraries = new List<string> { steamRoot };
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        var text = PathUtil.ReadTextSafe(vdfPath);
        if (text is null)
        {
            result.Warnings.Add("libraryfolders.vdf not found; only the main Steam library was scanned.");
            return libraries;
        }

        var root = VdfParser.Parse(text);
        var folders = root["libraryfolders"] ?? root["LibraryFolders"];
        if (folders is null) return libraries;

        foreach (var (key, node) in folders.Children)
        {
            // New format: "0" { "path" "D:\\SteamLibrary" ... }; old format: "1" "D:\\SteamLibrary".
            string? path = node.IsObject ? node.GetString("path") : (int.TryParse(key, out _) ? node.Value : null);
            if (string.IsNullOrWhiteSpace(path)) continue;
            path = ScannerHelpers.TrimPath(path);
            if (!PathUtil.IsSafeAbsolutePath(path)) continue;
            if (!libraries.Any(l => PathUtil.NormalizeKey(l) == PathUtil.NormalizeKey(path))) libraries.Add(path);
        }
        return libraries;
    }

    private static Game? ReadManifest(string manifestPath, string library, string steamRoot, ScanContext ctx, ScannerResult result)
    {
        var text = PathUtil.ReadTextSafe(manifestPath, 1024 * 1024);
        if (text is null)
        {
            result.Warnings.Add($"Could not read {Path.GetFileName(manifestPath)}");
            return null;
        }

        var app = VdfParser.Parse(text)["AppState"];
        var appId = app?.GetString("appid");
        var name = app?.GetString("name");
        var installDir = app?.GetString("installdir");
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsDigit) || string.IsNullOrWhiteSpace(name)) return null;
        if (ToolAppIds.Contains(appId) || IsToolName(name)) return null;

        string? installPath = null;
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            installPath = PathUtil.SafeCombine(Path.Combine(library, "steamapps", "common"), installDir);
        }

        var game = ScannerHelpers.NewGame(LauncherKind.Steam, appId, name, installPath);
        game.Metadata["SteamAppId"] = appId;
        game.Metadata["SteamLibrary"] = library;
        game.LauncherPageUri = $"steam://nav/games/details/{appId}";
        game.VerifyFilesUri = $"steam://validate/{appId}";
        game.LaunchMethods.Add(LaunchMethod.ProtocolUri($"steam://rungameid/{appId}", "Steam (handles DRM, updates and cloud saves)"));

        // StateFlags bit 4 = "fully installed". Without it the game is still downloading / never finished.
        // (Bit 2 "update required" alone still leaves a playable install — Steam updates on launch.)
        var complete = long.TryParse(app?.GetString("StateFlags"), out var flags) && (flags & 4) == 4;
        if (complete && (flags & 2) == 2) game.Metadata["SteamUpdatePending"] = "true";

        ScannerHelpers.ApplyInstallStatus(game, complete);
        if (game.Status is InstallStatus.Installed or InstallStatus.Incomplete)
        {
            ScannerHelpers.DetectExecutable(game, ctx);
            var direct = ScannerHelpers.DirectExe(game.ExecutablePath, description: "Run executable directly (Steam may still start for DRM)");
            if (direct is not null) game.LaunchMethods.Add(direct);
        }

        game.CoverImagePath = FindCover(steamRoot, appId);
        return game;
    }

    private static bool IsToolName(string name)
        => name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Steamworks SDK Redist", StringComparison.OrdinalIgnoreCase);

    /// <summary>Steam keeps cover art locally in appcache\librarycache (old flat layout and newer per-app folders).</summary>
    internal static string? FindCover(string steamRoot, string appId)
    {
        var cache = Path.Combine(steamRoot, "appcache", "librarycache");
        if (!PathUtil.DirectoryExistsSafe(cache)) return null;

        string[] flat =
        {
            $"{appId}_library_600x900_2x.jpg", $"{appId}_library_600x900.jpg",
        };
        foreach (var f in flat)
        {
            var p = Path.Combine(cache, f);
            if (PathUtil.FileExistsSafe(p)) return p;
        }

        var appDir = Path.Combine(cache, appId);
        if (PathUtil.DirectoryExistsSafe(appDir))
        {
            var found = FindFile(appDir, "library_600x900*.jpg", 2) ?? FindFile(appDir, "library_capsule*.jpg", 2);
            if (found is not null) return found;
        }

        foreach (var f in new[] { $"{appId}_header.jpg", Path.Combine(appId, "header.jpg") })
        {
            var p = Path.Combine(cache, f);
            if (PathUtil.FileExistsSafe(p)) return p;
        }
        return null;
    }

    private static string? FindFile(string dir, string pattern, int depth)
    {
        var direct = PathUtil.EnumerateFilesSafe(dir, pattern).OrderByDescending(f => f.Contains("_2x", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (direct is not null || depth <= 0) return direct;
        foreach (var sub in PathUtil.EnumerateDirectoriesSafe(dir))
        {
            var r = FindFile(sub, pattern, depth - 1);
            if (r is not null) return r;
        }
        return null;
    }
}
