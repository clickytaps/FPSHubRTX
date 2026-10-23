using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Persistence;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Scanning;
using FPSHubRTX.Core.Scanning.Scanners;

namespace FPSHubRTX.Core.Services;

public sealed class LibraryFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<Game> Games { get; set; } = new();
    public List<string> Collections { get; set; } = new();
    public DateTime? LastScanUtc { get; set; }
}

public sealed record MergeResult(IReadOnlyList<Game> Added, IReadOnlyList<Game> Updated, IReadOnlyList<Game> NowMissing);

/// <summary>
/// The persistent game library. Thread-safe; every mutation is saved immediately.
/// Removing a game only removes the library entry — game files are never touched.
/// </summary>
public sealed class LibraryService
{
    private readonly JsonFileStore<LibraryFile> _store;
    private readonly ILog _log;
    private readonly object _gate = new();
    private LibraryFile _data;

    public LibraryService(string libraryFilePath, ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
        _store = new JsonFileStore<LibraryFile>(libraryFilePath, () => new LibraryFile(), _log);
        _data = _store.Load();
        // Defensive: a hand-edited file could contain duplicates or nulls.
        _data.Games = _data.Games.Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Id))
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).Select(grp => grp.First()).ToList();
    }

    /// <summary>Raised after any change (on the thread that made the change).</summary>
    public event EventHandler? Changed;

    public DateTime? LastScanUtc { get { lock (_gate) return _data.LastScanUtc; } }

    public IReadOnlyList<Game> GetAll()
    {
        lock (_gate) return _data.Games.Select(g => g.Clone()).ToList();
    }

    public Game? Get(string id)
    {
        lock (_gate) return _data.Games.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Clone();
    }

    public int Count { get { lock (_gate) return _data.Games.Count; } }

    public IReadOnlyList<string> GetCollections()
    {
        lock (_gate)
        {
            return _data.Collections.Concat(_data.Games.SelectMany(g => g.Collections))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public void AddCollection(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        Mutate(d =>
        {
            if (!d.Collections.Contains(name, StringComparer.CurrentCultureIgnoreCase)) d.Collections.Add(name);
        });
    }

    public void RemoveCollection(string name)
    {
        Mutate(d =>
        {
            d.Collections.RemoveAll(c => c.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            foreach (var g in d.Games) g.Collections.RemoveAll(c => c.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        });
    }

    /// <summary>
    /// Merges scan results into the library: new games are added, known games get fresh scanner data
    /// (paths, launch methods, status) while user customisations are preserved, and games that a
    /// fully-completed launcher scan no longer reports are marked Missing (never deleted).
    /// </summary>
    public MergeResult MergeScan(ScanSummary summary, bool markMissing = true)
    {
        var added = new List<Game>();
        var updated = new List<Game>();
        var missing = new List<Game>();

        Mutate(d =>
        {
            var index = d.Games.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
            var pathIndex = d.Games.Where(g => !string.IsNullOrWhiteSpace(g.InstallPath))
                .GroupBy(g => PathUtil.NormalizeKey(g.InstallPath!))
                .ToDictionary(grp => grp.Key, grp => grp.First());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var found in summary.Games)
            {
                seen.Add(found.Id);
                if (!index.TryGetValue(found.Id, out var existing))
                {
                    // A manually added game at the same folder is upgraded rather than duplicated.
                    if (!string.IsNullOrWhiteSpace(found.InstallPath)
                        && pathIndex.TryGetValue(PathUtil.NormalizeKey(found.InstallPath!), out var samePath)
                        && samePath.Launcher is LauncherKind.Manual or LauncherKind.Standalone
                        && found.Launcher is not LauncherKind.Standalone)
                    {
                        existing = samePath;
                        seen.Add(samePath.Id);
                        index.Remove(samePath.Id);
                        existing.Id = found.Id;
                        existing.Launcher = found.Launcher;
                        index[existing.Id] = existing;
                    }
                }

                if (existing is null)
                {
                    var g = found.Clone();
                    g.DateAddedUtc = DateTime.UtcNow;
                    d.Games.Add(g);
                    index[g.Id] = g;
                    added.Add(g.Clone());
                    continue;
                }

                ApplyScannerFields(existing, found);
                updated.Add(existing.Clone());
            }

            if (markMissing)
            {
                var completed = summary.CompletedSources.ToHashSet();
                foreach (var g in d.Games)
                {
                    if (seen.Contains(g.Id) || g.Launcher == LauncherKind.Manual || !completed.Contains(g.Launcher)) continue;
                    if (g.Status == InstallStatus.Missing) continue;
                    g.Status = InstallStatus.Missing;
                    g.StatusDetail = $"No longer reported by {g.Launcher}. It may have been uninstalled or its drive is disconnected.";
                    missing.Add(g.Clone());
                }
            }

            d.LastScanUtc = DateTime.UtcNow;
        });

        return new MergeResult(added, updated, missing);
    }

    private static void ApplyScannerFields(Game target, Game found)
    {
        target.SourceId = found.SourceId;
        target.Name = found.Name;
        target.InstallPath = found.InstallPath;
        target.ExecutablePath = found.ExecutablePath;
        target.LaunchMethods = found.LaunchMethods.Select(m => m.Clone()).ToList();
        target.Status = found.Status;
        target.StatusDetail = found.StatusDetail;
        target.CoverImagePath = found.CoverImagePath ?? (PathUtil.FileExistsSafe(target.CoverImagePath) ? target.CoverImagePath : null);
        target.LauncherPageUri = found.LauncherPageUri;
        target.VerifyFilesUri = found.VerifyFilesUri;
        target.LastScannedUtc = found.LastScannedUtc;
        foreach (var kv in found.Metadata) target.Metadata[kv.Key] = kv.Value;
        // Custom name/exe/args/cover/favourite/collections/notes/profile/playtime are user data: untouched.
    }

    /// <summary>Applies a change to one game and saves. Returns false when the game doesn't exist.</summary>
    public bool Update(string id, Action<Game> change)
    {
        var ok = false;
        Mutate(d =>
        {
            var g = d.Games.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (g is null) return;
            change(g);
            ok = true;
        });
        return ok;
    }

    /// <summary>Adds a game the user picked by hand (an .exe anywhere on disk).</summary>
    public Game AddManual(string executablePath, string? name = null, string? arguments = null)
    {
        if (!PathUtil.IsSafeAbsolutePath(executablePath) || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a valid .exe file.", nameof(executablePath));

        var dir = Path.GetDirectoryName(executablePath)!;
        var game = new Game
        {
            Id = "manual:" + PathUtil.StableHash(PathUtil.NormalizeKey(executablePath)),
            SourceId = PathUtil.StableHash(PathUtil.NormalizeKey(executablePath)),
            Launcher = LauncherKind.Manual,
            Name = string.IsNullOrWhiteSpace(name) ? FolderScanner.PrettifyFolderName(Path.GetFileNameWithoutExtension(executablePath)) : name.Trim(),
            InstallPath = dir,
            ExecutablePath = executablePath,
            CustomArguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments,
            LastScannedUtc = DateTime.UtcNow,
        };
        game.LaunchMethods.Add(LaunchMethod.Exe(executablePath, null, dir, "Run executable"));
        ScannerHelpers.ApplyInstallStatus(game);
        if (game.Status == InstallStatus.Installed && !PathUtil.FileExistsSafe(executablePath))
        {
            game.Status = InstallStatus.Missing;
            game.StatusDetail = "Executable not found";
        }

        Mutate(d =>
        {
            var existing = d.Games.FindIndex(g => g.Id == game.Id);
            if (existing >= 0) d.Games[existing] = game; else d.Games.Add(game);
        });
        return game.Clone();
    }

    /// <summary>Removes the library entry only. Installed game files are never deleted.</summary>
    public bool Remove(string id)
    {
        var removed = false;
        Mutate(d => removed = d.Games.RemoveAll(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0);
        return removed;
    }

    /// <summary>Points a game at a new install folder (after the user moved it) and re-detects its executable.</summary>
    public bool Relocate(string id, string newInstallPath, int searchDepth = 4)
    {
        if (!PathUtil.IsSafeAbsolutePath(newInstallPath) || !PathUtil.DirectoryExistsSafe(newInstallPath)) return false;
        return Update(id, g =>
        {
            var oldPath = g.InstallPath;
            g.InstallPath = newInstallPath;
            // Re-map executable paths that were inside the old folder.
            string? Remap(string? p)
            {
                if (p is null || oldPath is null || !PathUtil.IsUnder(p, oldPath)) return p;
                var rel = Path.GetRelativePath(oldPath, p);
                var candidate = PathUtil.SafeCombine(newInstallPath, rel);
                return candidate is not null && PathUtil.FileExistsSafe(candidate) ? candidate : null;
            }
            g.ExecutablePath = Remap(g.ExecutablePath) ?? ExecutableFinder.FindMainExecutable(newInstallPath, g.Name, searchDepth);
            g.CustomExecutablePath = Remap(g.CustomExecutablePath);
            foreach (var m in g.LaunchMethods.Where(m => m.Kind == LaunchMethodKind.Executable))
            {
                m.Target = Remap(m.Target) ?? g.ExecutablePath ?? m.Target;
                m.WorkingDirectory = m.Target is { Length: > 0 } ? Path.GetDirectoryName(m.Target) : m.WorkingDirectory;
            }
            ScannerHelpers.ApplyInstallStatus(g);
        });
    }

    /// <summary>Re-checks every game's folder/executable without a full scan (works offline).</summary>
    public int RefreshStatuses()
    {
        var changed = 0;
        Mutate(d =>
        {
            foreach (var g in d.Games)
            {
                var before = (g.Status, g.StatusDetail);
                if (g.Launcher == LauncherKind.Manual && g.EffectiveExecutablePath is { } exe)
                {
                    ScannerHelpers.ApplyInstallStatus(g);
                    if (g.Status == InstallStatus.Installed && !PathUtil.FileExistsSafe(exe))
                    {
                        g.Status = InstallStatus.Missing;
                        g.StatusDetail = "Executable not found";
                    }
                }
                else if (!string.IsNullOrWhiteSpace(g.InstallPath) && !(g.StatusDetail?.StartsWith("Store package", StringComparison.Ordinal) ?? false))
                {
                    var incomplete = g.Status == InstallStatus.Incomplete;
                    ScannerHelpers.ApplyInstallStatus(g, !incomplete);
                }
                if (before != (g.Status, g.StatusDetail)) changed++;
            }
        });
        return changed;
    }

    public void RecordPlay(string id, DateTime endUtc, TimeSpan duration)
    {
        Update(id, g =>
        {
            g.LastPlayedUtc = endUtc;
            g.TotalPlaytimeSeconds += Math.Max(0, duration.TotalSeconds);
            g.SessionCount++;
        });
    }

    /// <summary>Replaces the whole library (used by restore). Caller validates the data first.</summary>
    public void ReplaceAll(LibraryFile file)
    {
        Mutate(d =>
        {
            d.Games = file.Games.Where(g => !string.IsNullOrWhiteSpace(g.Id)).ToList();
            d.Collections = file.Collections;
            d.LastScanUtc = file.LastScanUtc;
        });
    }

    public void Reload()
    {
        lock (_gate) _data = _store.Load();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Mutate(Action<LibraryFile> change)
    {
        lock (_gate)
        {
            change(_data);
            try
            {
                _store.Save(_data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error("Could not save library", ex);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
