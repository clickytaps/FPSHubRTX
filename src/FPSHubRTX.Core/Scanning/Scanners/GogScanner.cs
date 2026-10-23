using System.Text.Json;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// GOG: every GOG install (Galaxy or offline installer) registers HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\{id}
/// with name, path and exe. The goggame-{id}.info file supplies the primary play task when the registry
/// doesn't. GOG games are DRM-free so direct launch is offered first; GOG Galaxy is offered when installed.
/// </summary>
public sealed class GogScanner : IGameScanner
{
    private const string GamesKey = @"SOFTWARE\GOG.com\Games";

    public LauncherKind Kind => LauncherKind.Gog;
    public string DisplayName => "GOG";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind);
        var galaxyExe = FindGalaxy(ctx);
        var ids = ctx.Registry.GetSubKeyNames(RegHive.LocalMachine32, GamesKey);
        result.LauncherDetected = galaxyExe is not null || ids.Count > 0;

        foreach (var id in ids)
        {
            ctx.ThrowIfCancelled();
            var key = GamesKey + "\\" + id;
            string? Get(string v) => ctx.Registry.GetString(RegHive.LocalMachine32, key, v);

            // DLC entries depend on a base game.
            if (!string.IsNullOrWhiteSpace(Get("dependsOn"))) continue;

            var name = Get("gameName") ?? Get("GAMENAME");
            var path = Get("path") ?? Get("PATH");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
            path = ScannerHelpers.TrimPath(path);
            if (!PathUtil.IsSafeAbsolutePath(path) || ctx.IsExcluded(path)) continue;
            ctx.ReportLocation?.Invoke(path);

            var gameId = Get("gameID") ?? id;
            var game = ScannerHelpers.NewGame(LauncherKind.Gog, gameId, name, path);
            game.Metadata["GogId"] = gameId;
            game.LauncherPageUri = galaxyExe is not null ? $"goggalaxy://openGameView/{gameId}" : null;

            string? exe = Get("exe") ?? Get("EXE");
            string? args = Get("launchParam");
            string? workingDir = Get("workingDir");
            if (!string.IsNullOrWhiteSpace(exe)) exe = ScannerHelpers.TrimPath(exe);

            if (string.IsNullOrWhiteSpace(exe) || !PathUtil.FileExistsSafe(exe))
            {
                (exe, args, workingDir) = ReadPlayTask(path, gameId) ?? (null, null, null);
            }

            if (!string.IsNullOrWhiteSpace(exe) && PathUtil.IsSafeAbsolutePath(exe)) game.ExecutablePath = exe;
            if (!string.IsNullOrWhiteSpace(workingDir)) workingDir = ScannerHelpers.TrimPath(workingDir);

            ScannerHelpers.ApplyInstallStatus(game);
            if (game.Status == InstallStatus.Installed) ScannerHelpers.DetectExecutable(game, ctx);

            var direct = ScannerHelpers.DirectExe(game.ExecutablePath, args, PathUtil.IsSafeAbsolutePath(workingDir) ? workingDir : null, "Run directly (GOG games are DRM-free)");
            if (direct is not null) game.LaunchMethods.Add(direct);
            if (galaxyExe is not null)
            {
                game.LaunchMethods.Add(LaunchMethod.Launcher(galaxyExe, $"/command=runGame /gameId={gameId} /path=\"{path}\"", "GOG Galaxy"));
            }
            result.Games.Add(game);
        }

        result.Completed = true;
        return result;
    }

    private static string? FindGalaxy(ScanContext ctx)
    {
        var clientPath = ctx.Registry.GetString(RegHive.LocalMachine32, @"SOFTWARE\GOG.com\GalaxyClient\paths", "client");
        var clientExe = ctx.Registry.GetString(RegHive.LocalMachine32, @"SOFTWARE\GOG.com\GalaxyClient", "clientExecutable") ?? "GalaxyClient.exe";
        if (!string.IsNullOrWhiteSpace(clientPath))
        {
            var exe = Path.Combine(ScannerHelpers.TrimPath(clientPath), clientExe);
            if (PathUtil.FileExistsSafe(exe)) return exe;
        }
        if (!string.IsNullOrEmpty(ctx.Environment.ProgramFilesX86))
        {
            var fallback = Path.Combine(ctx.Environment.ProgramFilesX86, "GOG Galaxy", "GalaxyClient.exe");
            if (PathUtil.FileExistsSafe(fallback)) return fallback;
        }
        return null;
    }

    /// <summary>Reads the primary FileTask from goggame-{id}.info.</summary>
    internal static (string? Exe, string? Args, string? WorkingDir)? ReadPlayTask(string installPath, string gameId)
    {
        var info = Path.Combine(installPath, $"goggame-{gameId}.info");
        if (!PathUtil.FileExistsSafe(info))
        {
            info = PathUtil.EnumerateFilesSafe(installPath, "goggame-*.info").FirstOrDefault() ?? "";
        }
        var text = info.Length == 0 ? null : PathUtil.ReadTextSafe(info, 1024 * 1024);
        if (text is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("playTasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) return null;
            JsonElement? chosen = null;
            foreach (var t in tasks.EnumerateArray())
            {
                var isFile = t.TryGetProperty("type", out var type) && type.GetString() == "FileTask";
                if (!isFile) continue;
                var primary = t.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True;
                if (primary) { chosen = t; break; }
                chosen ??= t;
            }
            if (chosen is null) return null;
            var task = chosen.Value;
            var rel = task.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(rel)) return null;
            var exe = PathUtil.SafeCombine(installPath, rel!.Replace('\\', Path.DirectorySeparatorChar));
            var args = task.TryGetProperty("arguments", out var a) ? a.GetString() : null;
            var wd = task.TryGetProperty("workingDir", out var w) && !string.IsNullOrWhiteSpace(w.GetString())
                ? PathUtil.SafeCombine(installPath, w.GetString()!.Replace('\\', Path.DirectorySeparatorChar))
                : null;
            return (exe, args, wd);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
