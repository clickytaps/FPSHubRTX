using System.Text.Json;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// Epic Games Launcher: reads the per-game *.item JSON manifests in
/// %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests (location can be overridden by the launcher's
/// AppDataPath registry value). Launches through com.epicgames.launcher:// so Epic handles ownership checks.
/// </summary>
public sealed class EpicScanner : IGameScanner
{
    public LauncherKind Kind => LauncherKind.Epic;
    public string DisplayName => "Epic Games";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind);
        var manifestsDir = FindManifestsDirectory(ctx);
        if (manifestsDir is null)
        {
            result.Completed = true;
            return result;
        }
        result.LauncherDetected = true;
        ctx.ReportLocation?.Invoke(manifestsDir);

        foreach (var file in PathUtil.EnumerateFilesSafe(manifestsDir, "*.item"))
        {
            ctx.ThrowIfCancelled();
            var game = ReadItem(file, ctx, result);
            if (game is not null && result.Games.All(g => g.Id != game.Id)) result.Games.Add(game);
        }

        result.Completed = true;
        return result;
    }

    private static string? FindManifestsDirectory(ScanContext ctx)
    {
        var appData = ctx.Registry.GetString(RegHive.LocalMachine32, @"SOFTWARE\Epic Games\EpicGamesLauncher", "AppDataPath");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(appData) && PathUtil.IsSafeAbsolutePath(ScannerHelpers.TrimPath(appData)))
            candidates.Add(Path.Combine(ScannerHelpers.TrimPath(appData), "Manifests"));
        if (!string.IsNullOrEmpty(ctx.Environment.ProgramData))
            candidates.Add(Path.Combine(ctx.Environment.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests"));
        return candidates.FirstOrDefault(PathUtil.DirectoryExistsSafe);
    }

    private static Game? ReadItem(string file, ScanContext ctx, ScannerResult result)
    {
        var text = PathUtil.ReadTextSafe(file, 4 * 1024 * 1024);
        if (text is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            bool Bool(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

            var displayName = Str("DisplayName");
            var appName = Str("AppName");
            var installLocation = Str("InstallLocation");
            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(appName)) return null;

            // Skip DLC / add-ons: they share the main game's folder and have a different MainGameAppName.
            var mainGame = Str("MainGameAppName");
            if (!string.IsNullOrEmpty(mainGame) && !string.Equals(mainGame, appName, StringComparison.OrdinalIgnoreCase)) return null;
            if (root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            {
                var list = cats.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString() ?? "").ToList();
                if (list.Any(c => c.Equals("addons", StringComparison.OrdinalIgnoreCase) || c.Equals("digitalextras", StringComparison.OrdinalIgnoreCase)))
                    return null;
                if (list.Count > 0 && !list.Any(c => c.Equals("games", StringComparison.OrdinalIgnoreCase))) return null;
            }

            if (!string.IsNullOrWhiteSpace(installLocation))
            {
                installLocation = ScannerHelpers.TrimPath(installLocation);
                if (!PathUtil.IsSafeAbsolutePath(installLocation)) installLocation = null;
            }

            var game = ScannerHelpers.NewGame(LauncherKind.Epic, appName!, displayName!, installLocation);
            var ns = Str("CatalogNamespace");
            var itemId = Str("CatalogItemId");
            game.Metadata["EpicAppName"] = appName!;
            if (ns is not null) game.Metadata["EpicNamespace"] = ns;
            if (itemId is not null) game.Metadata["EpicCatalogItemId"] = itemId;

            var uriId = !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(itemId)
                ? $"{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(itemId)}%3A{Uri.EscapeDataString(appName!)}"
                : Uri.EscapeDataString(appName!);
            game.LaunchMethods.Add(LaunchMethod.ProtocolUri($"com.epicgames.launcher://apps/{uriId}?action=launch&silent=true", "Epic Games Launcher (handles ownership checks)"));
            game.LauncherPageUri = "com.epicgames.launcher://library";

            var launchExe = Str("LaunchExecutable");
            if (!string.IsNullOrWhiteSpace(installLocation) && !string.IsNullOrWhiteSpace(launchExe))
            {
                var exe = PathUtil.SafeCombine(installLocation, launchExe!);
                if (exe is not null && PathUtil.FileExistsSafe(exe)) game.ExecutablePath = exe;
            }

            ScannerHelpers.ApplyInstallStatus(game, !Bool("bIsIncompleteInstall"));
            if (game.Status == InstallStatus.Installed)
            {
                ScannerHelpers.DetectExecutable(game, ctx);
                var direct = ScannerHelpers.DirectExe(game.ExecutablePath, Str("LaunchCommand"), description: "Run executable directly (many Epic games require the launcher)");
                if (direct is not null) game.LaunchMethods.Add(direct);
            }
            return game;
        }
        catch (JsonException ex)
        {
            result.Warnings.Add($"Unreadable Epic manifest {Path.GetFileName(file)}: {ex.Message}");
            return null;
        }
    }
}
