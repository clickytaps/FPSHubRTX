using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Persistence;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Scanning;

namespace FPSHubRTX.Core.Services;

public enum HealthSeverity
{
    Info,
    Warning,
    Problem,
}

public sealed record HealthIssue(string GameId, string GameName, HealthSeverity Severity, string Issue, string Suggestion);

/// <summary>Read-only library checks. Never modifies or deletes game files.</summary>
public static class LibraryHealth
{
    public static IReadOnlyList<HealthIssue> Check(IReadOnlyList<Game> games)
    {
        var issues = new List<HealthIssue>();
        foreach (var g in games)
        {
            if (g.Status == InstallStatus.Missing)
                issues.Add(new(g.Id, g.DisplayName, HealthSeverity.Problem, g.StatusDetail ?? "Install folder missing", "Reconnect the drive, use 'Locate installation' if you moved the game, or remove the entry."));
            else if (g.Status == InstallStatus.Inaccessible)
                issues.Add(new(g.Id, g.DisplayName, HealthSeverity.Problem, g.StatusDetail ?? "Install folder not accessible", "Check folder permissions or that the drive is online."));
            else if (g.Status == InstallStatus.Incomplete)
                issues.Add(new(g.Id, g.DisplayName, HealthSeverity.Warning, "Install is incomplete or updating", "Finish the download/update in the game's launcher."));

            if (!string.IsNullOrWhiteSpace(g.CustomExecutablePath) && !PathUtil.FileExistsSafe(g.CustomExecutablePath))
                issues.Add(new(g.Id, g.DisplayName, HealthSeverity.Problem, "The custom executable no longer exists", "Pick the executable again in the game's settings."));

            var hasExeOnly = g.LaunchMethods.Count > 0 && g.LaunchMethods.All(m => m.Kind == LaunchMethodKind.Executable);
            if (hasExeOnly && g.Status == InstallStatus.Installed && g.LaunchMethods.All(m => !PathUtil.FileExistsSafe(m.Target)) && string.IsNullOrWhiteSpace(g.CustomExecutablePath))
                issues.Add(new(g.Id, g.DisplayName, HealthSeverity.Problem, "Executable not found", "Choose the correct executable in the game's settings or rescan."));

            if (g.LaunchMethods.Count == 0 && string.IsNullOrWhiteSpace(g.CustomExecutablePath))
                issues.Add(new(g.Id, g.DisplayName, HealthSeverity.Warning, "No launch method", "Choose the game's executable manually."));

            if (string.IsNullOrWhiteSpace(g.EffectiveExecutablePath) && g.Launcher is not (LauncherKind.Xbox or LauncherKind.Minecraft))
                issues.Add(new(g.Id, g.DisplayName, HealthSeverity.Info, "Main executable not identified", "FPS capture needs the game's process name — set the executable manually if FPS shows as unavailable."));

            if (!string.IsNullOrWhiteSpace(g.CustomCoverPath) && !PathUtil.FileExistsSafe(g.CustomCoverPath))
                issues.Add(new(g.Id, g.DisplayName, HealthSeverity.Info, "Custom cover image is missing", "Choose a new cover image."));
        }

        // Possible duplicates: same simplified name from different sources.
        foreach (var grp in games.GroupBy(g => ExecutableFinder.Simplify(g.DisplayName)).Where(grp => grp.Key.Length > 0 && grp.Count() > 1))
        {
            var sources = string.Join(", ", grp.Select(x => x.Launcher));
            foreach (var g in grp)
                issues.Add(new(g.Id, g.DisplayName, HealthSeverity.Info, $"Possible duplicate ({sources})", "If both entries are the same install, remove one from the library (game files are not affected)."));
        }

        return issues.OrderByDescending(i => i.Severity).ThenBy(i => i.GameName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

/// <summary>Zips the user's data (library, settings, sessions, benchmarks, custom covers) and restores it.</summary>
public static class BackupService
{
    private static readonly string[] DataFiles = { "library.json", "settings.json", "sessions.json" };

    public static string CreateBackup(AppPaths paths, string? destinationFile = null)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);
        var target = destinationFile ?? Path.Combine(paths.BackupsDirectory, $"FPSHubRTX-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var tmp = target + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var f in DataFiles)
            {
                var path = Path.Combine(paths.DataDirectory, f);
                if (File.Exists(path)) zip.CreateEntryFromFile(path, f);
            }
            foreach (var f in Directory.EnumerateFiles(paths.BenchmarksDirectory, "*.json"))
                zip.CreateEntryFromFile(f, "benchmarks/" + Path.GetFileName(f));
            foreach (var f in Directory.EnumerateFiles(paths.CoversDirectory))
                zip.CreateEntryFromFile(f, "covers/" + Path.GetFileName(f));
            var manifest = zip.CreateEntry("backup-info.json");
            using var w = new StreamWriter(manifest.Open(), Encoding.UTF8);
            w.Write(JsonSerializer.Serialize(new { App = "FPS Hub RTX", CreatedUtc = DateTime.UtcNow, Version = typeof(BackupService).Assembly.GetName().Version?.ToString() }));
        }
        File.Move(tmp, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Validates and restores a backup. Only known file names are extracted (no path traversal),
    /// library/sessions are parsed before anything is overwritten, and a safety backup of the current data is made first.
    /// </summary>
    public static string Restore(AppPaths paths, string zipFile, ILog log)
    {
        using var zip = ZipFile.OpenRead(zipFile);
        if (zip.GetEntry("backup-info.json") is null) throw new InvalidDataException("This file is not an FPS Hub RTX backup.");

        // Validate JSON files first.
        foreach (var name in DataFiles)
        {
            var entry = zip.GetEntry(name);
            if (entry is null) continue;
            if (entry.Length > 200 * 1024 * 1024) throw new InvalidDataException($"{name} in the backup is too large.");
            using var s = entry.Open();
            using var doc = JsonDocument.Parse(s); // throws if corrupt
        }

        var safety = CreateBackup(paths, Path.Combine(paths.BackupsDirectory, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip"));
        log.Info($"Safety backup created before restore: {Path.GetFileName(safety)}");

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            string? dest = null;
            if (DataFiles.Contains(name)) dest = Path.Combine(paths.DataDirectory, name);
            else if (name.StartsWith("benchmarks/", StringComparison.Ordinal) && IsPlainFileName(name[11..]) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                dest = Path.Combine(paths.BenchmarksDirectory, name[11..]);
            else if (name.StartsWith("covers/", StringComparison.Ordinal) && IsPlainFileName(name[7..]))
                dest = Path.Combine(paths.CoversDirectory, name[7..]);
            if (dest is null) continue;
            entry.ExtractToFile(dest, overwrite: true);
        }
        return safety;
    }

    private static bool IsPlainFileName(string name)
        => name.Length is > 0 and < 200 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains("..", StringComparison.Ordinal) && !name.Contains('/');
}
