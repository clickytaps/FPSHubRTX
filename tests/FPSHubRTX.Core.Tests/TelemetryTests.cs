using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Monitoring;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.Core.Tests;

public class FrameStatisticsTests
{
    [Fact]
    public void Constant_frame_times_give_exact_values()
    {
        var s = FrameStatistics.Compute(Frames.Repeat(10, 1000));
        Assert.Equal(1000, s.FrameCount);
        Assert.Equal(100, s.AverageFps, 6);
        Assert.Equal(100, s.MinimumFps, 6);
        Assert.Equal(100, s.MaximumFps, 6);
        Assert.Equal(100, s.OnePercentLowFps!.Value, 6);
        Assert.Equal(100, s.PointOnePercentLowFps!.Value, 6);
        Assert.Equal(StabilityRating.Excellent, s.Stability);
        Assert.Equal(0, s.StutterFrameCount);
    }

    [Fact]
    public void Average_is_time_weighted_not_mean_of_instant_fps()
    {
        // One second at 100 FPS (100 × 10 ms) plus one 1000 ms... no: 50 frames at 20 ms = 1 s at 50 FPS.
        var frames = Frames.Repeat(10, 100).Concat(Frames.Repeat(20, 50)).ToList();
        var s = FrameStatistics.Compute(frames);
        Assert.Equal(150 / 2.0, s.AverageFps, 6); // 150 frames in 2 s = 75 FPS (mean of instant FPS would be 83.3)
        Assert.Equal(50, s.MinimumFps, 6);
        Assert.Equal(100, s.MaximumFps, 6);
    }

    [Fact]
    public void One_percent_low_uses_99th_percentile_frame_time()
    {
        // 990 frames at 10 ms, 10 frames at 40 ms → P99 (nearest rank 990) = 10 ms; 991st+ are 40 ms.
        var frames = Frames.Repeat(10, 990).Concat(Frames.Repeat(40, 10)).ToList();
        var s = FrameStatistics.Compute(frames);
        Assert.Equal(100, s.OnePercentLowFps!.Value, 6);
        Assert.Equal(25, s.PointOnePercentLowFps!.Value, 6); // P99.9 → rank 999 → 40 ms
        Assert.Equal(10, s.StutterFrameCount);

        var frames2 = Frames.Repeat(10, 980).Concat(Frames.Repeat(40, 20)).ToList();
        Assert.Equal(25, FrameStatistics.Compute(frames2).OnePercentLowFps!.Value, 6);
    }

    [Fact]
    public void Low_percentiles_need_enough_frames()
    {
        var few = FrameStatistics.Compute(Frames.Repeat(16.6, 99));
        Assert.Null(few.OnePercentLowFps);
        Assert.Null(few.PointOnePercentLowFps);
        Assert.Equal(StabilityRating.NotEnoughData, few.Stability);
        var some = FrameStatistics.Compute(Frames.Repeat(16.6, 500));
        Assert.NotNull(some.OnePercentLowFps);
        Assert.Null(some.PointOnePercentLowFps);
    }

    [Fact]
    public void Pauses_and_invalid_values_are_excluded_and_counted()
    {
        var s = FrameStatistics.Compute(new[] { 10.0, 10, 5000, double.NaN, -1, 0, 10 }, maxValidFrameTimeMs: 2000);
        Assert.Equal(3, s.FrameCount);
        Assert.Equal(4, s.IgnoredFrameCount);
        Assert.Equal(100, s.AverageFps, 6);
    }

    [Fact]
    public void Empty_input_gives_no_numbers()
    {
        var s = FrameStatistics.Compute(Array.Empty<double>());
        Assert.Equal(0, s.FrameCount);
        Assert.Equal(0, s.AverageFps);
        Assert.Null(s.OnePercentLowFps);
    }

    [Fact]
    public void Histogram_matches_exact_statistics()
    {
        var rng = new Random(42);
        var frames = Enumerable.Range(0, 20_000).Select(_ => 6 + rng.NextDouble() * 4 + (rng.Next(200) == 0 ? 30 : 0)).ToList();
        var exact = FrameStatistics.Compute(frames);
        var hist = new FrameTimeHistogram();
        foreach (var f in frames) hist.Add(f);
        var approx = hist.ToSummary("test");
        Assert.Equal(exact.FrameCount, approx.FrameCount);
        Assert.Equal(exact.AverageFps, approx.AverageFps, 6);
        Assert.Equal(exact.MinimumFps, approx.MinimumFps, 6);
        Assert.Equal(exact.MaximumFps, approx.MaximumFps, 6);
        Assert.InRange(Math.Abs(exact.P99FrameTimeMs - approx.P99FrameTimeMs), 0, 0.011);
        Assert.InRange(Math.Abs(exact.OnePercentLowFps!.Value - approx.OnePercentLowFps!.Value), 0, 0.05);
        Assert.Equal(exact.Stability, approx.Stability);
    }

    [Fact]
    public void Histogram_handles_frames_over_one_second()
    {
        var hist = new FrameTimeHistogram(maxValidFrameTimeMs: 5000);
        foreach (var f in Frames.Repeat(10, 99)) hist.Add(f);
        hist.Add(1500);
        Assert.Equal(1500, hist.Percentile(1.0), 6);
        Assert.Equal(1000.0 / 1500, hist.ToSummary("x").MinimumFps, 6);
    }
}

public class PresentMonCsvParserTests
{
    [Fact]
    public void Parses_v2_default_columns_with_na_values()
    {
        var p = new PresentMonCsvParser();
        Assert.Null(p.ParseLine("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed"));
        var r = p.ParseLine("game.exe,1234,0x0000021F,DXGI,0,0,1,Hardware: Independent Flip,Application,1523.6604,NA,6.9443,NA,0.1234,1.5,2.5")!.Value;
        Assert.Equal(1234, r.ProcessId);
        Assert.Equal("game.exe", r.Application);
        Assert.Equal(6.9443, r.MsBetweenPresents!.Value, 6);
        Assert.Null(r.MsBetweenDisplayChange);
        Assert.Equal(1523.6604, r.TimeMs, 6);
        Assert.Equal("Hardware: Independent Flip", r.PresentMode);
        Assert.Equal("MsBetweenPresents", p.FrameTimeColumn);
    }

    [Fact]
    public void Parses_v1_metrics_with_time_in_seconds()
    {
        var p = new PresentMonCsvParser();
        p.ParseLine("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInSeconds,MsBetweenPresents,MsBetweenDisplayChange");
        var r = p.ParseLine("vk.exe,7,0x1,Other,1,0,0,Composed: Flip,2.5,16.67,16.67")!.Value;
        Assert.Equal(2500, r.TimeMs, 6);
        Assert.Equal("Other", r.Runtime);
        Assert.Equal(16.67, r.MsBetweenDisplayChange!.Value, 6);
    }

    [Fact]
    public void Handles_quoted_fields_and_garbage()
    {
        var p = new PresentMonCsvParser();
        p.ParseLine("Application,ProcessID,MsBetweenPresents");
        var r = p.ParseLine("\"My, \"\"Game\"\"\",42,8.5")!.Value;
        Assert.Equal("My, \"Game\"", r.Application);
        Assert.Equal(8.5, r.MsBetweenPresents!.Value, 6);
        Assert.Null(p.ParseLine("not,a,number"));
        Assert.Null(p.ParseLine(""));
        Assert.Null(p.ParseLine("only-one-field"));
    }

    [Fact]
    public void Arguments_quote_process_names_and_reject_unsafe_ones()
    {
        var args = PresentMonCapture.BuildArguments(new[] { "Game.exe", "bad\"name.exe", "Game.exe", "Other Game.exe" }, "C:\\Data\\capture.csv");
        Assert.Contains("--process_name \"Game.exe\"", args);
        Assert.Contains("--process_name \"Other Game.exe\"", args);
        Assert.DoesNotContain("bad", args);
        Assert.Equal(1, args.Split("\"Game.exe\"").Length - 1);
        Assert.Contains("--output_file \"C:\\Data\\capture.csv\"", args);
        Assert.Contains("--terminate_on_proc_exit", args);
        Assert.Contains("--stop_existing_session", args);
    }

    [Fact]
    public void Locator_prefers_override_then_bundled_copy()
    {
        using var t = new TempDir();
        Assert.Null(PresentMonLocator.Find(t.Root, null));
        var bundled = t.Exe("tools/PresentMon/PresentMon.exe", 10);
        Assert.Equal(bundled, PresentMonLocator.Find(t.Root, null));
        var custom = t.Exe("elsewhere/PresentMon-2.6.0-x64.exe", 10);
        Assert.Equal(custom, PresentMonLocator.Find(t.Root, custom));
        Assert.Equal(bundled, PresentMonLocator.Find(t.Root, Path.Combine(t.Root, "missing.exe")));
    }
}

public class FpsMonitorServiceTests
{
    private static (FpsMonitorService svc, List<FakeCapture> captures, ManualClock clock) Make(string? presentMon = "/fake/PresentMon.exe", FpsSettings? settings = null)
    {
        var captures = new List<FakeCapture>();
        var clock = new ManualClock();
        var s = settings ?? new FpsSettings();
        var svc = new FpsMonitorService(() => presentMon, _ => { var c = new FakeCapture(); captures.Add(c); return c; }, () => s,
            Path.Combine(Path.GetTempPath(), "fpshub-captures"), new MemoryLog(), clock.Func);
        return (svc, captures, clock);
    }

    private static FpsTarget Target() => new()
    {
        GameId = "g", GameName = "Game",
        ProcessIds = new HashSet<int> { 100 },
        ExecutableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "game.exe" },
    };

    [Fact]
    public void Without_presentmon_reports_unavailable_and_no_numbers()
    {
        var (svc, captures, _) = Make(presentMon: null);
        Assert.False(svc.Start(Target()));
        var snap = svc.GetSnapshot();
        Assert.Equal(FpsCaptureState.NotInstalled, snap.State);
        Assert.Null(snap.CurrentFps);
        Assert.Null(snap.Session);
        Assert.Empty(captures);
    }

    [Fact]
    public void Measured_frames_produce_live_statistics()
    {
        var (svc, captures, _) = Make();
        Assert.True(svc.Start(Target()));
        Assert.Equal(FpsCaptureState.WaitingForFrames, svc.State);
        Assert.Null(svc.GetSnapshot().CurrentFps);
        Assert.Equal(new[] { "game.exe" }, captures[0].StartedWith);

        var samples = new List<FrameSample>();
        svc.FramesCaptured += (_, f) => samples.AddRange(f);
        captures[0].Push(Frames.Make(100, "game.exe", Frames.Repeat(1000.0 / 144, 300)));

        var snap = svc.GetSnapshot();
        Assert.Equal(FpsCaptureState.Measuring, snap.State);
        Assert.Equal(144, snap.CurrentFps!.Value, 1);
        Assert.Equal(144, snap.Session!.AverageFps, 3);
        Assert.Equal(300, snap.Session.FrameCount);
        Assert.Equal(300, samples.Count);
        Assert.NotEmpty(snap.FpsHistory);

        var final = svc.Stop();
        Assert.Equal(300, final!.FrameCount);
        Assert.True(captures[0].Stopped);
        Assert.Equal(FpsCaptureState.Stopped, svc.State);
        svc.Dispose();
    }

    [Fact]
    public void Frames_from_other_processes_are_ignored_but_child_processes_with_known_exe_are_adopted()
    {
        var (svc, captures, _) = Make();
        svc.Start(Target());
        captures[0].Push(Frames.Make(999, "dwm.exe", Frames.Repeat(5, 200)));
        Assert.Equal(FpsCaptureState.WaitingForFrames, svc.State);
        captures[0].Push(Frames.Make(555, "game.exe", Frames.Repeat(10, 200)));
        Assert.Equal(200, svc.GetSnapshot().Session!.FrameCount);
        svc.Dispose();
    }

    [Fact]
    public void Secondary_swap_chain_is_filtered_out()
    {
        var (svc, captures, _) = Make();
        svc.Start(Target());
        // Interleave: main swap chain 2 frames per overlay swap chain frame.
        var batch = new List<FrameRecord>();
        double t = 0;
        for (var i = 0; i < 600; i++)
        {
            t += 5;
            batch.Add(new FrameRecord(100, "game.exe", "0xMAIN", "DXGI", "x", t, 10, null, null));
            if (i % 2 == 0) batch.Add(new FrameRecord(100, "game.exe", "0xOVERLAY", "DXGI", "x", t, 33, null, null));
        }
        captures[0].Push(batch);
        var s = svc.GetSnapshot().Session!;
        // Almost all accepted frames must be the 10 ms main swap chain (a few overlay frames may slip in before selection).
        Assert.InRange(s.AverageFrameTimeMs, 10, 10.6);
        svc.Dispose();
    }

    [Fact]
    public void No_frames_times_out_into_unavailable_with_explanation()
    {
        var (svc, _, clock) = Make(settings: new FpsSettings { NoFramesTimeoutSeconds = 10 });
        svc.Start(Target());
        clock.Advance(TimeSpan.FromSeconds(11));
        svc.Tick();
        var snap = svc.GetSnapshot();
        Assert.Equal(FpsCaptureState.NoFrames, snap.State);
        Assert.Equal("FPS unavailable for this game", snap.StateTitle);
        Assert.False(string.IsNullOrWhiteSpace(snap.StateDetail));
        Assert.Null(snap.CurrentFps);
        svc.Dispose();
    }

    [Fact]
    public void Stale_stream_switches_to_paused_and_hides_current_fps()
    {
        var (svc, captures, clock) = Make();
        svc.Start(Target());
        captures[0].Push(Frames.Make(100, "game.exe", Frames.Repeat(10, 150)));
        clock.Advance(TimeSpan.FromSeconds(5));
        svc.Tick();
        var snap = svc.GetSnapshot();
        Assert.Equal(FpsCaptureState.Paused, snap.State);
        Assert.Null(snap.CurrentFps);
        Assert.NotNull(snap.Session); // statistics so far are kept
        svc.Dispose();
    }

    [Fact]
    public void Access_denied_and_start_failures_are_reported()
    {
        var (svc, captures, _) = Make();
        svc.Start(Target());
        captures[0].LastError = "denied";
        captures[0].End(CaptureProcessState.AccessDenied);
        Assert.Equal(FpsCaptureState.AccessDenied, svc.State);
        svc.Dispose();

        var (svc2, _, _) = MakeThrowing();
        Assert.False(svc2.Start(Target()));
        Assert.Equal(FpsCaptureState.Failed, svc2.State);
    }

    private static (FpsMonitorService, List<FakeCapture>, ManualClock) MakeThrowing()
    {
        var clock = new ManualClock();
        var svc = new FpsMonitorService(() => "/fake/pm.exe", _ => new FakeCapture { ThrowOnStart = true }, () => new FpsSettings(), Path.GetTempPath(), new MemoryLog(), clock.Func);
        return (svc, new List<FakeCapture>(), clock);
    }

    [Fact]
    public void Decimate_keeps_spikes()
    {
        var values = Enumerable.Repeat(10.0, 1000).ToList();
        values[537] = 80;
        var d = FpsMonitorService.Decimate(values, 100);
        Assert.Equal(100, d.Count);
        Assert.Equal(80, d.Max());
    }
}

public class SystemMonitorTests
{
    [Fact]
    public void Never_invents_values_when_a_source_is_unavailable()
    {
        var monitor = new SystemMonitor(new MemoryLog(), new Platform.FixedDriveProvider());
        var s = monitor.Sample();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(s.CpuPercent);
            Assert.Null(s.GpuPercent);
            Assert.Null(s.CpuTemperatureC);
            Assert.Null(s.GpuTemperatureC);
            Assert.False(string.IsNullOrEmpty(s.GpuTemperatureNote));
        }
        else
        {
            // First CPU sample has no baseline yet, so it must be null rather than a guess.
            Assert.Null(s.CpuPercent);
        }
        monitor.Dispose();
    }

    [Fact]
    public void Gpu_engine_aggregation_matches_task_manager_rules()
    {
        var items = new[]
        {
            ("pid_10_luid_0x00000000_0x0000D1F2_phys_0_eng_0_engtype_3D", 30.0),
            ("pid_20_luid_0x00000000_0x0000D1F2_phys_0_eng_0_engtype_3D", 25.0),
            ("pid_10_luid_0x00000000_0x0000D1F2_phys_0_eng_3_engtype_Copy", 70.0),
            ("pid_10_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 5.0),
            ("garbage", 99.0),
        };
        var (overall, game) = GpuEngineMath.Aggregate(items, new HashSet<int> { 10 });
        Assert.Equal(70, overall); // busiest engine across adapters
        Assert.Equal(70, game);
        var (overall2, game2) = GpuEngineMath.Aggregate(items.Take(2), new HashSet<int> { 20 });
        Assert.Equal(55, overall2); // 3D engine summed over processes
        Assert.Equal(25, game2);
        Assert.Equal((null, null), GpuEngineMath.Aggregate(Array.Empty<(string, double)>(), null));
    }
}
