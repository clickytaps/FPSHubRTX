using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;

namespace FPSHubRTX.Core.Telemetry;

public enum FpsCaptureState
{
    /// <summary>No capture running.</summary>
    Idle,
    /// <summary>PresentMon is not installed/bundled — FPS can't be measured.</summary>
    NotInstalled,
    /// <summary>Capture started, waiting for the first frame.</summary>
    WaitingForFrames,
    /// <summary>Real frames are being measured.</summary>
    Measuring,
    /// <summary>Capture is running but the game hasn't presented a frame for a while (minimised, paused, loading).</summary>
    Paused,
    /// <summary>No frames at all within the timeout — FPS unavailable for this game.</summary>
    NoFrames,
    /// <summary>Windows denied access to the ETW presentation events.</summary>
    AccessDenied,
    Failed,
    Stopped,
}

public sealed class FpsTarget
{
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public HashSet<int> ProcessIds { get; init; } = new();
    public HashSet<string> ExecutableNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool KeepFrameLog { get; init; }
}

/// <summary>Immutable view of the current FPS measurement for the UI and overlay.</summary>
public sealed class FpsSnapshot
{
    public FpsCaptureState State { get; init; }
    public string StateTitle { get; init; } = "";
    public string? StateDetail { get; init; }
    public string? GameId { get; init; }
    public string? GameName { get; init; }

    /// <summary>FPS over the last second of measured frames. Null unless State == Measuring.</summary>
    public double? CurrentFps { get; init; }
    /// <summary>Most recent frame time (ms). Null unless State == Measuring.</summary>
    public double? CurrentFrameTimeMs { get; init; }
    /// <summary>Statistics over the whole capture so far.</summary>
    public FrameStatsSummary? Session { get; init; }
    /// <summary>Recent frame times for the graph (oldest first, decimated to keep spikes).</summary>
    public IReadOnlyList<double> FrameTimeHistory { get; init; } = Array.Empty<double>();
    /// <summary>FPS for each completed second (oldest first).</summary>
    public IReadOnlyList<double> FpsHistory { get; init; } = Array.Empty<double>();
    public TimeSpan Elapsed { get; init; }
    public string? PresentRuntime { get; init; }
    public string? PresentMode { get; init; }
    public string MetricSource { get; init; } = "";

    public bool HasLiveData => State == FpsCaptureState.Measuring && CurrentFps is not null;

    public static FpsSnapshot Idle(string title = "Not measuring", string? detail = null) => new() { State = FpsCaptureState.Idle, StateTitle = title, StateDetail = detail };
}

public interface IFrameCapture : IDisposable
{
    event Action<IReadOnlyList<FrameRecord>>? FramesReceived;
    event Action<CaptureProcessState>? ProcessEnded;
    CaptureProcessState State { get; }
    string? LastError { get; }
    string? OutputFile { get; }
    void Start(IReadOnlyCollection<string> processNames, string outputFile, bool elevated);
    void Stop(bool allowElevatedStop);
}

/// <summary>Adapter so <see cref="PresentMonCapture"/> satisfies <see cref="IFrameCapture"/>.</summary>
public sealed class PresentMonFrameCapture : IFrameCapture
{
    private readonly PresentMonCapture _inner;
    public PresentMonFrameCapture(string exe, ILog log) => _inner = new PresentMonCapture(exe, log);
    public event Action<IReadOnlyList<FrameRecord>>? FramesReceived { add => _inner.FramesReceived += value; remove => _inner.FramesReceived -= value; }
    public event Action<CaptureProcessState>? ProcessEnded { add => _inner.ProcessEnded += value; remove => _inner.ProcessEnded -= value; }
    public CaptureProcessState State => _inner.State;
    public string? LastError => _inner.LastError;
    public string? OutputFile => _inner.OutputFile;
    public void Start(IReadOnlyCollection<string> processNames, string outputFile, bool elevated) => _inner.Start(processNames, outputFile, elevated);
    public void Stop(bool allowElevatedStop) => _inner.Stop(allowElevatedStop);
    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Turns the PresentMon frame stream into live statistics. Only real measured frames are ever reported:
/// whenever there is no data the snapshot says so explicitly (state + explanation) and all numbers are null.
/// </summary>
public sealed class FpsMonitorService : IDisposable
{
    private readonly Func<string?> _presentMonPath;
    private readonly Func<string, IFrameCapture> _captureFactory;
    private readonly Func<FpsSettings> _settings;
    private readonly string _capturesDirectory;
    private readonly ILog _log;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();

    private IFrameCapture? _capture;
    private FpsTarget? _target;
    private FpsCaptureState _state = FpsCaptureState.Idle;
    private string? _stateDetail;
    private DateTime _startedUtc;
    private DateTime _lastFrameUtc;
    private FrameTimeHistogram _histogram = new();
    private readonly SwapChainSelector _selector = new();
    private readonly LinkedList<(double TimeMs, double FrameTimeMs)> _window = new();
    private readonly List<double> _fpsPerSecond = new();
    private long _currentSecond = -1;
    private int _framesThisSecond;
    private double _lastFrameTime;
    private string? _runtime, _presentMode, _metricSource;
    private Timer? _timer;

    public FpsMonitorService(Func<string?> presentMonPath, Func<string, IFrameCapture> captureFactory, Func<FpsSettings> settings,
        string capturesDirectory, ILog log, Func<DateTime>? clock = null)
    {
        _presentMonPath = presentMonPath;
        _captureFactory = captureFactory;
        _settings = settings;
        _capturesDirectory = capturesDirectory;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Raised ~4× per second while a capture is active (background thread).</summary>
    public event Action<FpsSnapshot>? SnapshotUpdated;

    /// <summary>Raised with every batch of accepted frame samples (for benchmark recording).</summary>
    public event Action<string, IReadOnlyList<FrameSample>>? FramesCaptured;

    public bool IsActive { get { lock (_gate) return _capture is not null; } }

    public string? TargetGameId { get { lock (_gate) return _target?.GameId; } }

    public FpsCaptureState State { get { lock (_gate) return _state; } }

    public bool IsPresentMonAvailable => _presentMonPath() is not null;

    /// <summary>Starts measuring a running game. Returns false (with State/explanation set) when it can't.</summary>
    public bool Start(FpsTarget target, bool elevated = false)
    {
        StopInternal(allowElevatedStop: false, raise: false);
        var exe = _presentMonPath();
        lock (_gate)
        {
            _target = target;
            ResetStats();
            if (exe is null)
            {
                SetState(FpsCaptureState.NotInstalled,
                    "PresentMon (the frame-presentation monitor FPS Hub uses) was not found. Reinstall FPS Hub, or set its path in Settings → FPS.");
                RaiseSnapshot();
                return false;
            }
            if (target.ExecutableNames.Count == 0)
            {
                SetState(FpsCaptureState.Failed, "The game's process name is unknown, so frames can't be attributed to it.");
                RaiseSnapshot();
                return false;
            }

            var capture = _captureFactory(exe);
            capture.FramesReceived += OnFrames;
            capture.ProcessEnded += OnCaptureEnded;
            var file = Path.Combine(_capturesDirectory, $"capture-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            try
            {
                capture.Start(target.ExecutableNames, file, elevated);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
            {
                capture.FramesReceived -= OnFrames;
                capture.ProcessEnded -= OnCaptureEnded;
                capture.Dispose();
                SetState(FpsCaptureState.Failed, "Frame capture could not start: " + ex.Message);
                RaiseSnapshot();
                return false;
            }

            _capture = capture;
            _startedUtc = _clock();
            SetState(FpsCaptureState.WaitingForFrames, null);
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        }
        _log.Info($"FPS capture started for {target.GameName}");
        RaiseSnapshot();
        return true;
    }

    /// <summary>Adds process ids that belong to the target (games often spawn their real process later).</summary>
    public void UpdateTargetProcesses(string gameId, IEnumerable<int> pids, IEnumerable<string> exeNames)
    {
        lock (_gate)
        {
            if (_target is null || !_target.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var p in pids) _target.ProcessIds.Add(p);
            foreach (var n in exeNames) _target.ExecutableNames.Add(n);
        }
    }

    /// <summary>Stops measuring and returns the statistics for the capture (null when nothing was measured).</summary>
    public FrameStatsSummary? Stop(bool userInitiated = true) => StopInternal(userInitiated, raise: true);

    private FrameStatsSummary? StopInternal(bool allowElevatedStop, bool raise)
    {
        IFrameCapture? capture;
        FrameStatsSummary? summary;
        bool keepLog;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
            _timer?.Dispose();
            _timer = null;
            summary = _histogram.Count > 0 ? _histogram.ToSummary(_metricSource ?? "MsBetweenPresents") : null;
            keepLog = _target?.KeepFrameLog ?? false;
            if (capture is not null) SetState(FpsCaptureState.Stopped, summary is null ? "No frames were measured." : null);
        }
        if (capture is null) return summary;

        capture.FramesReceived -= OnFrames;
        capture.ProcessEnded -= OnCaptureEnded;
        try
        {
            capture.Stop(allowElevatedStop);
        }
        catch (Exception ex)
        {
            _log.Warn("Stopping frame capture failed", ex);
        }
        var file = capture.OutputFile;
        capture.Dispose();
        if (!keepLog && file is not null)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (raise) RaiseSnapshot();
        return summary;
    }

    /// <summary>Feeds parsed frames. Called by the capture's tail thread; public for tests.</summary>
    public void OnFrames(IReadOnlyList<FrameRecord> batch)
    {
        List<FrameSample>? accepted = null;
        string? gameId;
        lock (_gate)
        {
            if (_target is null) return;
            gameId = _target.GameId;
            var settings = _settings();
            foreach (var f in batch)
            {
                if (!_target.ProcessIds.Contains(f.ProcessId))
                {
                    var exeName = f.Application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? f.Application : f.Application + ".exe";
                    if (!_target.ExecutableNames.Contains(exeName)) continue;
                    _target.ProcessIds.Add(f.ProcessId);
                }
                if (!_selector.Accept(f)) continue;

                double? ft;
                if (settings.PreferDisplayedFrames && f.MsBetweenDisplayChange is > 0)
                {
                    ft = f.MsBetweenDisplayChange;
                    _metricSource = "MsBetweenDisplayChange (displayed frames)";
                }
                else
                {
                    ft = f.MsBetweenPresents;
                    _metricSource ??= "MsBetweenPresents (presented frames)";
                }
                if (ft is null || !FrameStatistics.IsValid(ft.Value, settings.MaxValidFrameTimeMs))
                {
                    if (ft is not null) _histogram.Add(ft.Value); // counted as ignored
                    continue;
                }

                _histogram.Add(ft.Value);
                _window.AddLast((f.TimeMs, ft.Value));
                _lastFrameTime = ft.Value;
                _runtime = f.Runtime ?? _runtime;
                _presentMode = f.PresentMode ?? _presentMode;
                (accepted ??= new List<FrameSample>()).Add(new FrameSample(f.TimeMs, ft.Value));

                var second = (long)Math.Floor(f.TimeMs / 1000.0);
                if (_currentSecond < 0) _currentSecond = second;
                if (second != _currentSecond)
                {
                    if (second == _currentSecond + 1) _fpsPerSecond.Add(_framesThisSecond);
                    _currentSecond = second;
                    _framesThisSecond = 0;
                    if (_fpsPerSecond.Count > 600) _fpsPerSecond.RemoveRange(0, _fpsPerSecond.Count - 600);
                }
                _framesThisSecond++;
            }

            if (accepted is not null)
            {
                _lastFrameUtc = _clock();
                if (_state is FpsCaptureState.WaitingForFrames or FpsCaptureState.NoFrames or FpsCaptureState.Paused)
                    SetState(FpsCaptureState.Measuring, null);
                var windowMs = Math.Max(5, settings.GraphWindowSeconds) * 1000.0;
                var newest = _window.Last!.Value.TimeMs;
                while (_window.First is not null && newest - _window.First.Value.TimeMs > windowMs) _window.RemoveFirst();
            }
        }
        if (accepted is not null) FramesCaptured?.Invoke(gameId!, accepted);
    }

    private void OnCaptureEnded(CaptureProcessState processState)
    {
        lock (_gate)
        {
            if (_capture is null) return;
            switch (processState)
            {
                case CaptureProcessState.AccessDenied:
                    SetState(FpsCaptureState.AccessDenied, _capture.LastError);
                    break;
                case CaptureProcessState.Failed:
                    SetState(FpsCaptureState.Failed, _capture.LastError ?? "PresentMon stopped unexpectedly.");
                    break;
                default:
                    if (_state != FpsCaptureState.Measuring && _histogram.Count == 0)
                        SetState(FpsCaptureState.NoFrames, NoFramesExplanation());
                    else
                        SetState(FpsCaptureState.Stopped, "The game closed; capture finished.");
                    break;
            }
        }
        RaiseSnapshot();
    }

    /// <summary>Periodic state evaluation (timer-driven; internal so tests can drive it).</summary>
    internal void Tick()
    {
        lock (_gate)
        {
            if (_capture is null) return;
            var now = _clock();
            var settings = _settings();
            if (_state == FpsCaptureState.WaitingForFrames && now - _startedUtc > TimeSpan.FromSeconds(Math.Max(5, settings.NoFramesTimeoutSeconds)))
            {
                SetState(FpsCaptureState.NoFrames, NoFramesExplanation());
            }
            else if (_state == FpsCaptureState.Measuring && now - _lastFrameUtc > TimeSpan.FromSeconds(3))
            {
                SetState(FpsCaptureState.Paused, "No new frames for a few seconds — the game may be minimised, paused or loading.");
            }
        }
        RaiseSnapshot();
    }

    public FpsSnapshot GetSnapshot()
    {
        lock (_gate) return BuildSnapshot();
    }

    private FpsSnapshot BuildSnapshot()
    {
        var measuring = _state == FpsCaptureState.Measuring;
        double? current = null;
        if (measuring && _window.Count > 1)
        {
            double sum = 0;
            var count = 0;
            for (var node = _window.Last; node is not null && sum < 1000; node = node.Previous)
            {
                sum += node.Value.FrameTimeMs;
                count++;
            }
            current = sum > 0 ? count * 1000.0 / sum : null;
        }

        return new FpsSnapshot
        {
            State = _state,
            StateTitle = Title(_state),
            StateDetail = _stateDetail,
            GameId = _target?.GameId,
            GameName = _target?.GameName,
            CurrentFps = current,
            CurrentFrameTimeMs = measuring && _window.Count > 0 ? _lastFrameTime : null,
            Session = _histogram.Count > 0 ? _histogram.ToSummary(_metricSource ?? "MsBetweenPresents") : null,
            FrameTimeHistory = Decimate(_window.Select(w => w.FrameTimeMs).ToList(), 600),
            FpsHistory = _fpsPerSecond.TakeLast(180).ToList(),
            Elapsed = _capture is null || _startedUtc == default ? TimeSpan.Zero : _clock() - _startedUtc,
            PresentRuntime = _runtime,
            PresentMode = _presentMode,
            MetricSource = _metricSource ?? "",
        };
    }

    /// <summary>Reduces a series to at most <paramref name="max"/> points keeping the worst frame time of each group (so spikes stay visible).</summary>
    public static IReadOnlyList<double> Decimate(IReadOnlyList<double> values, int max)
    {
        if (values.Count <= max) return values;
        var result = new List<double>(max);
        var group = (double)values.Count / max;
        for (var i = 0; i < max; i++)
        {
            var start = (int)(i * group);
            var end = Math.Min(values.Count, (int)((i + 1) * group));
            var worst = 0.0;
            for (var j = start; j < end; j++) worst = Math.Max(worst, values[j]);
            result.Add(worst);
        }
        return result;
    }

    private void ResetStats()
    {
        _histogram = new FrameTimeHistogram(_settings().MaxValidFrameTimeMs);
        _selector.Reset();
        _window.Clear();
        _fpsPerSecond.Clear();
        _currentSecond = -1;
        _framesThisSecond = 0;
        _lastFrameTime = 0;
        _runtime = _presentMode = _metricSource = null;
        _startedUtc = default;
        _lastFrameUtc = default;
    }

    private void SetState(FpsCaptureState state, string? detail)
    {
        _state = state;
        _stateDetail = detail;
    }

    private void RaiseSnapshot()
    {
        FpsSnapshot snap;
        lock (_gate) snap = BuildSnapshot();
        SnapshotUpdated?.Invoke(snap);
    }

    public static string Title(FpsCaptureState state) => state switch
    {
        FpsCaptureState.Measuring => "Measuring",
        FpsCaptureState.WaitingForFrames => "Waiting for the first frame…",
        FpsCaptureState.Paused => "No new frames",
        FpsCaptureState.NoFrames => "FPS unavailable for this game",
        FpsCaptureState.NotInstalled => "FPS unavailable — PresentMon not found",
        FpsCaptureState.AccessDenied => "FPS unavailable — permission needed",
        FpsCaptureState.Failed => "FPS unavailable — capture failed",
        FpsCaptureState.Stopped => "Capture stopped",
        _ => "Not measuring",
    };

    public static string NoFramesExplanation() =>
        "PresentMon saw no presented frames from this game's processes. Common causes: the game renders through a " +
        "process FPS Hub didn't detect (choose the correct executable in the game's settings), it is still on a " +
        "launcher/loading window, it is minimised, or it is protected/streamed in a way that hides presentation events. " +
        "See Help → FPS compatibility.";

    public void Dispose()
    {
        StopInternal(allowElevatedStop: false, raise: false);
    }
}
