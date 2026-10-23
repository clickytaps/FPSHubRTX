using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Launching;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Monitoring;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.Core.Services;

/// <summary>A session that is still running.</summary>
public sealed class ActiveSession
{
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required LauncherKind Launcher { get; init; }
    public DateTime StartUtc { get; init; }
    public bool LaunchedFromHub { get; init; }
    public bool Tracked { get; init; }
    internal double CpuSum;
    internal double GpuSum;
    internal int CpuCount;
    internal int GpuCount;
    internal double PeakMemory;
    internal FrameStatsSummary? Fps;
    internal bool FpsAttempted;
    internal string? FpsUnavailableReason;
}

/// <summary>
/// Glues running-game detection, FPS capture and system sampling into play sessions:
/// starts capture when a game starts, follows its processes, and saves a <see cref="GameSession"/>
/// with real statistics when it exits (including crashes, reported by a non-zero exit code).
/// </summary>
public sealed class SessionTracker : IDisposable
{
    private readonly RunningGamesMonitor _monitor;
    private readonly FpsMonitorService _fps;
    private readonly SystemMonitor _system;
    private readonly LibraryService _library;
    private readonly SessionStore _sessions;
    private readonly Func<AppSettings> _settings;
    private readonly ILog _log;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveSession> _active = new(StringComparer.OrdinalIgnoreCase);

    public SessionTracker(RunningGamesMonitor monitor, FpsMonitorService fps, SystemMonitor system, LibraryService library,
        SessionStore sessions, Func<AppSettings> settings, ILog log, Func<DateTime>? clock = null)
    {
        _monitor = monitor;
        _fps = fps;
        _system = system;
        _library = library;
        _sessions = sessions;
        _settings = settings;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);

        _monitor.GameStarted += OnGameStarted;
        _monitor.GameExited += OnGameExited;
        _system.SampleTaken += OnSystemSample;
    }

    public event Action<ActiveSession>? SessionStarted;
    public event Action<GameSession>? SessionEnded;

    public IReadOnlyList<ActiveSession> Active
    {
        get { lock (_gate) return _active.Values.ToList(); }
    }

    private void OnGameStarted(object? sender, RunningGame rg)
    {
        var game = _library.Get(rg.GameId);
        if (game is null) return;
        var settings = _settings();
        var tracked = settings.TrackPlaytime && game.Profile.TrackSessions && (rg.LaunchedFromHub || settings.TrackExternallyLaunchedGames);

        var session = new ActiveSession
        {
            GameId = game.Id,
            GameName = game.DisplayName,
            Launcher = game.Launcher,
            StartUtc = rg.StartedUtc,
            LaunchedFromHub = rg.LaunchedFromHub,
            Tracked = tracked,
        };
        lock (_gate) _active[game.Id] = session;
        _system.SetGameProcesses(rg.ProcessIds.ToHashSet());

        var wantsFps = settings.Fps.AutoCaptureOnGameStart && game.Profile.AutoCaptureFps;
        if (wantsFps && !_fps.IsActive)
        {
            StartCapture(rg, game, elevated: false);
        }
        SessionStarted?.Invoke(session);
    }

    /// <summary>Starts (or restarts) FPS capture for a running game. Used automatically and by the FPS page.</summary>
    public bool StartCapture(RunningGame rg, Game game, bool elevated)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(game.Id, out var s)) s.FpsAttempted = true;
        }
        var target = new FpsTarget
        {
            GameId = game.Id,
            GameName = game.DisplayName,
            ProcessIds = rg.ProcessIds.ToHashSet(),
            ExecutableNames = rg.ExecutableNames.ToHashSet(StringComparer.OrdinalIgnoreCase),
            KeepFrameLog = game.Profile.KeepFrameLog,
        };
        if (!string.IsNullOrEmpty(game.EffectiveExecutablePath)) target.ExecutableNames.Add(Path.GetFileName(game.EffectiveExecutablePath));
        return _fps.Start(target, elevated);
    }

    /// <summary>Called by the UI's refresh loop so capture follows processes the game spawns later.</summary>
    public void RefreshProcesses()
    {
        foreach (var rg in _monitor.Running)
        {
            _fps.UpdateTargetProcesses(rg.GameId, rg.ProcessIds, rg.ExecutableNames);
        }
        var target = _fps.TargetGameId;
        var running = target is null ? _monitor.Running.FirstOrDefault() : _monitor.Running.FirstOrDefault(r => r.GameId == target);
        _system.SetGameProcesses(running?.ProcessIds.ToHashSet());
    }

    private void OnSystemSample(SystemSnapshot s)
    {
        lock (_gate)
        {
            foreach (var a in _active.Values)
            {
                if (s.CpuPercent is { } cpu) { a.CpuSum += cpu; a.CpuCount++; }
                if (s.GpuPercent is { } gpu) { a.GpuSum += gpu; a.GpuCount++; }
                if (s.GameProcessMemoryBytes is { } mem && mem > a.PeakMemory) a.PeakMemory = mem;
            }
        }
    }

    private void OnGameExited(object? sender, GameExitedEventArgs e)
    {
        ActiveSession? active;
        lock (_gate)
        {
            _active.Remove(e.Game.GameId, out active);
        }
        if (active is null) return;

        if (_fps.TargetGameId?.Equals(e.Game.GameId, StringComparison.OrdinalIgnoreCase) == true)
        {
            var state = _fps.State;
            active.Fps = _fps.Stop(userInitiated: false);
            active.FpsAttempted = true;
            if (active.Fps is null) active.FpsUnavailableReason = DescribeUnavailable(state);
        }

        Finish(active, e.ExitedUtc, e.ExitCode, e.ExitCode is null or 0 ? SessionEndReason.Exited : SessionEndReason.NonZeroExitCode);
    }

    private void Finish(ActiveSession active, DateTime endUtc, int? exitCode, SessionEndReason reason)
    {
        if (!active.Tracked) return;
        var session = new GameSession
        {
            GameId = active.GameId,
            GameName = active.GameName,
            Launcher = active.Launcher,
            StartUtc = active.StartUtc,
            EndUtc = endUtc < active.StartUtc ? active.StartUtc : endUtc,
            EndReason = reason,
            ExitCode = exitCode,
            LaunchedFromHub = active.LaunchedFromHub,
            Fps = active.Fps,
            FpsAvailability = active.Fps is { FrameCount: > 0 } ? FpsAvailability.Measured : active.FpsAttempted ? FpsAvailability.Unavailable : FpsAvailability.NotCaptured,
            FpsUnavailableReason = active.Fps is null ? active.FpsUnavailableReason : null,
            AverageCpuPercent = active.CpuCount > 0 ? active.CpuSum / active.CpuCount : null,
            AverageGpuPercent = active.GpuCount > 0 ? active.GpuSum / active.GpuCount : null,
            PeakGameMemoryBytes = active.PeakMemory > 0 ? active.PeakMemory : null,
        };
        _sessions.Add(session);
        _library.RecordPlay(active.GameId, session.EndUtc, session.Duration);
        _log.Info($"Session saved: {session.GameName}, {session.Duration:hh\\:mm\\:ss}, end={reason}, fps={session.FpsAvailability}");
        SessionEnded?.Invoke(session);
    }

    internal static string DescribeUnavailable(FpsCaptureState state) => state switch
    {
        FpsCaptureState.WaitingForFrames or FpsCaptureState.NoFrames or FpsCaptureState.Stopped
            => "No presented frames were seen from this game's processes",
        _ => FpsMonitorService.Title(state),
    };

    /// <summary>On app exit: close all running sessions at "now" so no playtime is lost.</summary>
    public void FinalizeAll()
    {
        List<ActiveSession> list;
        lock (_gate)
        {
            list = _active.Values.ToList();
            _active.Clear();
        }
        foreach (var a in list)
        {
            if (_fps.TargetGameId?.Equals(a.GameId, StringComparison.OrdinalIgnoreCase) == true)
            {
                var state = _fps.State;
                a.Fps = _fps.Stop(userInitiated: false);
                a.FpsAttempted = true;
                if (a.Fps is null) a.FpsUnavailableReason = DescribeUnavailable(state);
            }
            Finish(a, _clock(), null, SessionEndReason.AppClosed);
        }
    }

    public void Dispose()
    {
        _monitor.GameStarted -= OnGameStarted;
        _monitor.GameExited -= OnGameExited;
        _system.SampleTaken -= OnSystemSample;
    }
}
