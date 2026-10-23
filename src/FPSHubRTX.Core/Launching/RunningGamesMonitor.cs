using System.Diagnostics;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Launching;

/// <summary>A library game that currently has at least one running process.</summary>
public sealed class RunningGame
{
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public DateTime StartedUtc { get; init; }
    public bool LaunchedFromHub { get; init; }
    public HashSet<int> ProcessIds { get; } = new();
    public HashSet<string> ExecutableNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The first process found; its exit code is reported when the game closes.</summary>
    public int PrimaryProcessId { get; set; }
    internal Process? PrimaryHandle { get; set; }
}

public sealed class GameExitedEventArgs : EventArgs
{
    public required RunningGame Game { get; init; }
    public DateTime ExitedUtc { get; init; }
    public int? ExitCode { get; init; }
}

/// <summary>
/// Detects which library games are running by matching process image paths against install folders
/// (works for games started by FPS Hub, by a launcher, or by the user). Polls every couple of seconds
/// on a background timer; all resources are released when a game exits or monitoring stops.
/// </summary>
public sealed class RunningGamesMonitor : IDisposable
{
    private sealed record GameIndexEntry(string GameId, string Name, string InstallKey, string? ProcessNameHint);

    private sealed class PendingLaunch
    {
        public required string GameId { get; init; }
        public DateTime DeadlineUtc { get; init; }
        public HashSet<int> PidHints { get; } = new();
    }

    private readonly IProcessInspector _inspector;
    private readonly ILog _log;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, RunningGame> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingLaunch> _pending = new(StringComparer.OrdinalIgnoreCase);
    private List<GameIndexEntry> _index = new();
    private Timer? _timer;
    private int _polling;

    public RunningGamesMonitor(IProcessInspector inspector, ILog log, Func<DateTime>? clock = null)
    {
        _inspector = inspector;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public event EventHandler<RunningGame>? GameStarted;
    public event EventHandler<GameExitedEventArgs>? GameExited;
    public event EventHandler<string>? LaunchTimedOut;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public IReadOnlyList<RunningGame> Running
    {
        get { lock (_gate) return _running.Values.ToList(); }
    }

    public bool IsRunning(string gameId)
    {
        lock (_gate) return _running.ContainsKey(gameId);
    }

    public bool IsPending(string gameId)
    {
        lock (_gate) return _pending.ContainsKey(gameId);
    }

    /// <summary>Provides the games to watch for. Called whenever the library changes.</summary>
    public void SetLibrary(IEnumerable<Game> games)
    {
        var entries = new List<GameIndexEntry>();
        foreach (var g in games)
        {
            if (string.IsNullOrWhiteSpace(g.InstallPath)) continue;
            var key = PathUtil.NormalizeKey(g.InstallPath);
            // Never treat a drive root or a top-level folder like "C:\Program Files" as a game folder.
            if (key.Count(c => c == '\\') < 2 && !key.StartsWith('/')) continue;
            if (key.Length < 6) continue;
            g.Metadata.TryGetValue("ProcessNameHint", out var hint);
            entries.Add(new GameIndexEntry(g.Id, g.DisplayName, key, hint));
        }
        // Most specific folder first so nested installs resolve to the right game.
        entries.Sort((a, b) => b.InstallKey.Length.CompareTo(a.InstallKey.Length));
        lock (_gate) _index = entries;
    }

    public void ExpectLaunch(string gameId, TimeSpan timeout)
    {
        lock (_gate)
        {
            _pending[gameId] = new PendingLaunch { GameId = gameId, DeadlineUtc = _clock() + timeout };
        }
        // Poll sooner than usual so the "launching" state resolves quickly.
        _timer?.Change(TimeSpan.FromMilliseconds(750), PollInterval);
    }

    public void AddPidHint(string gameId, int pid)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(gameId, out var p)) p.PidHints.Add(pid);
        }
    }

    public void CancelExpectedLaunch(string gameId)
    {
        lock (_gate) _pending.Remove(gameId);
    }

    public void Start()
    {
        _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>One detection pass. Public so tests can drive it deterministically.</summary>
    public void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        var started = new List<RunningGame>();
        var exited = new List<GameExitedEventArgs>();
        var timedOut = new List<string>();
        try
        {
            var snapshot = _inspector.GetProcesses();
            var now = _clock();
            lock (_gate)
            {
                var alive = new HashSet<int>(snapshot.Select(s => s.Pid));
                var matches = new Dictionary<string, List<ProcessSnapshot>>(StringComparer.OrdinalIgnoreCase);

                foreach (var proc in snapshot)
                {
                    var gameId = MatchGame(proc);
                    if (gameId is null) continue;
                    if (!matches.TryGetValue(gameId, out var list)) matches[gameId] = list = new List<ProcessSnapshot>();
                    list.Add(proc);
                }

                // New or growing games.
                foreach (var (gameId, procs) in matches)
                {
                    if (!_running.TryGetValue(gameId, out var rg))
                    {
                        var entry = _index.First(e => e.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase));
                        var fromHub = _pending.Remove(gameId);
                        rg = new RunningGame
                        {
                            GameId = gameId,
                            GameName = entry.Name,
                            StartedUtc = fromHub ? now : EarliestStart(procs, now),
                            LaunchedFromHub = fromHub,
                            PrimaryProcessId = procs[0].Pid,
                            PrimaryHandle = TryOpen(procs[0].Pid),
                        };
                        _running[gameId] = rg;
                        started.Add(rg);
                    }
                    foreach (var p in procs)
                    {
                        rg.ProcessIds.Add(p.Pid);
                        rg.ExecutableNames.Add(p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name : p.Name + ".exe");
                    }
                }

                // Exited games: none of their processes are alive and nothing matches any more.
                foreach (var rg in _running.Values.ToList())
                {
                    rg.ProcessIds.RemoveWhere(pid => !alive.Contains(pid));
                    if (rg.ProcessIds.Count > 0 || matches.ContainsKey(rg.GameId)) continue;
                    _running.Remove(rg.GameId);
                    exited.Add(new GameExitedEventArgs { Game = rg, ExitedUtc = now, ExitCode = TryExitCode(rg.PrimaryHandle) });
                    rg.PrimaryHandle?.Dispose();
                    rg.PrimaryHandle = null;
                }

                foreach (var p in _pending.Values.Where(p => now > p.DeadlineUtc).ToList())
                {
                    _pending.Remove(p.GameId);
                    timedOut.Add(p.GameId);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Running-game detection failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }

        // Raise events outside the lock.
        foreach (var s in started)
        {
            _log.Info($"Game started: {s.GameName} (pid {s.PrimaryProcessId}, launched from hub: {s.LaunchedFromHub})");
            GameStarted?.Invoke(this, s);
        }
        foreach (var e in exited)
        {
            _log.Info($"Game exited: {e.Game.GameName} (exit code {(e.ExitCode?.ToString() ?? "unknown")})");
            GameExited?.Invoke(this, e);
        }
        foreach (var t in timedOut) LaunchTimedOut?.Invoke(this, t);
    }

    private string? MatchGame(ProcessSnapshot proc)
    {
        if (!string.IsNullOrEmpty(proc.ImagePath))
        {
            var key = PathUtil.NormalizeKey(proc.ImagePath);
            foreach (var e in _index)
            {
                if (key.StartsWith(e.InstallKey + "\\", StringComparison.Ordinal)) return e.GameId;
            }
        }

        // Fallback for processes that live outside the install folder (e.g. Minecraft's Java runtime),
        // only while that specific game is being launched from the hub, to avoid false positives.
        foreach (var pending in _pending.Values)
        {
            if (pending.PidHints.Contains(proc.Pid)) return pending.GameId;
            var entry = _index.FirstOrDefault(e => e.GameId.Equals(pending.GameId, StringComparison.OrdinalIgnoreCase));
            if (entry?.ProcessNameHint is { } hint &&
                (proc.Name.Equals(hint, StringComparison.OrdinalIgnoreCase) || (proc.Name + ".exe").Equals(hint, StringComparison.OrdinalIgnoreCase)))
                return pending.GameId;
        }

        // Already-running games keep their hinted processes.
        foreach (var rg in _running.Values)
        {
            if (rg.ProcessIds.Contains(proc.Pid)) return rg.GameId;
        }
        return null;
    }

    private static DateTime EarliestStart(List<ProcessSnapshot> procs, DateTime fallback)
    {
        var earliest = fallback;
        foreach (var p in procs)
        {
            try
            {
                using var proc = Process.GetProcessById(p.Pid);
                var start = proc.StartTime.ToUniversalTime();
                if (start < earliest) earliest = start;
            }
            catch (Exception)
            {
                // Access denied or exited — fall back to "now".
            }
        }
        return earliest;
    }

    private static Process? TryOpen(int pid)
    {
        Process? p = null;
        try
        {
            p = Process.GetProcessById(pid);
            _ = p.Handle; // obtain a handle now so the exit code stays readable after exit
            return p;
        }
        catch (Exception)
        {
            p?.Dispose(); // e.g. anti-cheat protected process: exit code will be reported as unknown
            return null;
        }
    }

    private static int? TryExitCode(Process? p)
    {
        if (p is null) return null;
        try
        {
            return p.HasExited ? p.ExitCode : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            foreach (var rg in _running.Values) rg.PrimaryHandle?.Dispose();
        }
    }
}
