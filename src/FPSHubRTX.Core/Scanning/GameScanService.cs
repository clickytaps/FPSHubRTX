using System.Diagnostics;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Scanning.Scanners;

namespace FPSHubRTX.Core.Scanning;

/// <summary>
/// Runs every enabled scanner off the UI thread, reports progress, supports cancellation, isolates
/// failures (one broken launcher never stops the scan) and removes duplicates.
/// </summary>
public sealed class GameScanService
{
    private readonly IReadOnlyList<IGameScanner> _scanners;
    private readonly Func<ScanEnvironment> _environment;
    private readonly IRegistryReader _registry;
    private readonly IDriveProvider _drives;
    private readonly IPackageCatalog _packages;
    private readonly ILog _log;

    public GameScanService(IRegistryReader registry, IDriveProvider drives, IPackageCatalog packages, ILog log,
        Func<ScanEnvironment>? environment = null, IReadOnlyList<IGameScanner>? scanners = null)
    {
        _registry = registry;
        _drives = drives;
        _packages = packages;
        _log = log;
        _environment = environment ?? ScanEnvironment.FromSystem;
        _scanners = scanners ?? CreateDefaultScanners();
    }

    public IReadOnlyList<IGameScanner> Scanners => _scanners;

    public static IReadOnlyList<IGameScanner> CreateDefaultScanners() => new IGameScanner[]
    {
        new SteamScanner(),
        new EpicScanner(),
        new XboxScanner(),
        new GogScanner(),
        new UbisoftScanner(),
        new EaScanner(),
        new BattleNetScanner(),
        new RiotScanner(),
        new MinecraftScanner(),
        new RobloxScanner(),
        new FolderScanner(),
    };

    public Task<ScanSummary> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Scan(settings, progress, ct), CancellationToken.None);

    private ScanSummary Scan(ScanSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var summary = new ScanSummary();
        var total = Stopwatch.StartNew();
        var enabled = _scanners
            .Where(s => settings.EnabledLaunchers.Count == 0 || settings.EnabledLaunchers.Contains(s.Kind))
            .ToList();
        var steps = enabled.Count + 1; // + validation/dedupe step
        var found = 0;
        var env = _environment();

        progress?.Report(new ScanProgress(ScanStage.Starting, "Preparing", null, 0, steps, 0));

        for (var i = 0; i < enabled.Count; i++)
        {
            var scanner = enabled[i];
            if (ct.IsCancellationRequested)
            {
                summary.Cancelled = true;
                break;
            }

            var source = scanner.DisplayName;
            var index = i;
            progress?.Report(new ScanProgress(ScanStage.Scanning, source, null, index, steps, found));
            var lastReport = Stopwatch.StartNew();
            var ctx = new ScanContext
            {
                Environment = env,
                Registry = _registry,
                Drives = _drives,
                Packages = _packages,
                Settings = settings,
                Log = _log,
                CancellationToken = ct,
                ReportLocation = location =>
                {
                    // Throttle location updates so a fast scanner doesn't flood the UI.
                    if (lastReport.ElapsedMilliseconds < 50) return;
                    lastReport.Restart();
                    progress?.Report(new ScanProgress(ScanStage.Scanning, source, location, index, steps, found));
                },
            };

            var sw = Stopwatch.StartNew();
            ScannerResult result;
            try
            {
                result = scanner.Scan(ctx);
            }
            catch (OperationCanceledException)
            {
                summary.Cancelled = true;
                result = new ScannerResult(scanner.Kind) { Error = "Cancelled" };
            }
            catch (Exception ex)
            {
                // A broken manifest or unexpected launcher layout must never stop the whole scan.
                _log.Error($"{source} scanner failed", ex);
                result = new ScannerResult(scanner.Kind) { Error = ex.Message, Completed = false };
            }
            result.Duration = sw.Elapsed;
            summary.SourceResults.Add(result);
            found += result.Games.Count;
            _log.Info($"{source}: {result.Games.Count} game(s), detected={result.LauncherDetected}, {result.Warnings.Count} warning(s), {sw.ElapsedMilliseconds} ms");
            foreach (var w in result.Warnings) _log.Warn($"{source}: {w}");

            if (summary.Cancelled) break;
        }

        progress?.Report(new ScanProgress(ScanStage.Validating, "Removing duplicates", null, steps - 1, steps, found));
        var all = summary.SourceResults.SelectMany(r => r.Games).ToList();
        var unique = Deduplicate(all, out var removed);
        summary.DuplicatesRemoved = removed;
        summary.Games.AddRange(unique.OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        summary.Duration = total.Elapsed;

        progress?.Report(new ScanProgress(summary.Cancelled ? ScanStage.Cancelled : ScanStage.Completed,
            summary.Cancelled ? "Cancelled" : "Done", null, steps, steps, summary.Games.Count));
        return summary;
    }

    /// <summary>
    /// Removes duplicates: identical ids, identical install folders (launcher entries win over folder
    /// detections), and standalone detections that are really a container of / inside a launcher game.
    /// </summary>
    public static List<Game> Deduplicate(IEnumerable<Game> games, out int removed)
    {
        var input = games.ToList();
        var byId = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in input)
        {
            if (!byId.TryGetValue(g.Id, out var existing) || Priority(g) > Priority(existing)) byId[g.Id] = g;
        }

        var byPath = new Dictionary<string, Game>(StringComparer.Ordinal);
        var noPath = new List<Game>();
        foreach (var g in byId.Values)
        {
            if (string.IsNullOrWhiteSpace(g.InstallPath))
            {
                noPath.Add(g);
                continue;
            }
            var key = PathUtil.NormalizeKey(g.InstallPath);
            if (!byPath.TryGetValue(key, out var existing) || Priority(g) > Priority(existing)) byPath[key] = g;
        }

        var launcherGames = byPath.Values.Where(g => g.Launcher != LauncherKind.Standalone).ToList();
        var result = new List<Game>(noPath);
        foreach (var g in byPath.Values)
        {
            if (g.Launcher == LauncherKind.Standalone && launcherGames.Any(l =>
                    PathUtil.IsUnder(g.InstallPath!, l.InstallPath!) || PathUtil.IsUnder(l.InstallPath!, g.InstallPath!)))
            {
                continue;
            }
            result.Add(g);
        }

        removed = input.Count - result.Count;
        return result;
    }

    /// <summary>Launcher-specific detections are richer than generic folder detections.</summary>
    private static int Priority(Game g) => g.Launcher switch
    {
        LauncherKind.Standalone => 0,
        LauncherKind.Manual => 1,
        _ => 10 + g.LaunchMethods.Count,
    };
}
