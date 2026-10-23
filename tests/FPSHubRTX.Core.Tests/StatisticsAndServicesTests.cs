using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Export;
using FPSHubRTX.Core.Launching;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Monitoring;
using FPSHubRTX.Core.Persistence;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Scanning;
using FPSHubRTX.Core.Services;
using FPSHubRTX.Core.Statistics;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.Core.Tests;

public class PlaytimeStatisticsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static GameSession S(string game, DateTime start, TimeSpan len, double? fps = null) => new()
    {
        GameId = game, GameName = game.ToUpperInvariant(), StartUtc = start, EndUtc = start + len,
        FpsAvailability = fps is null ? FpsAvailability.NotCaptured : FpsAvailability.Measured,
        Fps = fps is null ? null : new FrameStatsSummary { FrameCount = 1000, AverageFps = fps.Value },
    };

    [Fact]
    public void Totals_split_sessions_across_midnight_week_and_month()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc); // Friday
        var sessions = new[]
        {
            S("a", new DateTime(2026, 10, 22, 23, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(2)),   // 1h yesterday, 1h today
            S("b", new DateTime(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(30), fps: 120),
            S("a", new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1)),  // Monday this week
            S("a", new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(1)),  // Sunday last week
            S("c", new DateTime(2026, 9, 30, 23, 30, 0, DateTimeKind.Utc), TimeSpan.FromHours(1), fps: 60), // 30 min in October
        };
        var r = PlaytimeStatistics.Build(sessions, now, Utc);
        Assert.Equal(TimeSpan.FromMinutes(90), r.Today);
        Assert.Equal(TimeSpan.FromMinutes(60 + 60 + 30 + 60), r.ThisWeek);
        Assert.Equal(TimeSpan.FromMinutes(120 + 30 + 60 + 60 + 30), r.ThisMonth);
        Assert.Equal(TimeSpan.FromMinutes(120 + 30 + 60 + 60 + 60), r.AllTime);
        Assert.Equal(5, r.SessionCount);
        Assert.Equal(TimeSpan.FromMinutes(66), r.AverageSessionLength);
        Assert.Equal("a", r.MostPlayed[0].GameId);
        Assert.Equal(TimeSpan.FromHours(4), r.MostPlayed[0].Total);
        Assert.Equal(30, r.Last30Days.Count);
        Assert.Equal(TimeSpan.FromMinutes(90), r.Last30Days[^1].Total);
        Assert.Equal(90, r.AverageMeasuredFps!.Value, 6);
        Assert.Equal(2, r.MeasuredSessionCount);
    }

    [Fact]
    public void No_measurements_means_no_average_fps()
    {
        var r = PlaytimeStatistics.Build(new[] { S("a", DateTime.UtcNow.AddHours(-1), TimeSpan.FromMinutes(10)) }, DateTime.UtcNow, Utc);
        Assert.Null(r.AverageMeasuredFps);
        var empty = PlaytimeStatistics.Build(Array.Empty<GameSession>(), DateTime.UtcNow, Utc);
        Assert.Equal(TimeSpan.Zero, empty.AverageSessionLength);
    }

    [Fact]
    public void Fps_history_per_game_and_best_average()
    {
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var h = PlaytimeStatistics.FpsHistoryByGame(new[]
        {
            S("a", t0, TimeSpan.FromHours(1), 100), S("a", t0.AddDays(1), TimeSpan.FromHours(1), 140), S("a", t0.AddDays(2), TimeSpan.FromHours(1)),
            S("b", t0, TimeSpan.FromHours(1), 60),
        });
        Assert.Equal(2, h.Count);
        var a = h.Single(x => x.GameId == "a");
        Assert.Equal(2, a.Points.Count);
        Assert.Equal(140, a.BestAverageFps);
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(45, "45s")]
    [InlineData(600, "10m")]
    [InlineData(3725, "1h 02m")]
    [InlineData(90000, "25h 00m")]
    public void Duration_formatting(int seconds, string expected) => Assert.Equal(expected, PlaytimeStatistics.FormatDuration(TimeSpan.FromSeconds(seconds)));
}

public class ExportAndBenchmarkTests
{
    private static BenchmarkRun Run(string label, double ft, int frames, string? res = "2560x1440", string? preset = "High", string? scene = "Market")
    {
        var run = new BenchmarkRun
        {
            Label = label, GameId = "steam:1", GameName = "Game, \"Deluxe\"", Resolution = res, GraphicsPreset = preset, Scene = scene,
            StartUtc = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), EndUtc = new DateTime(2026, 10, 1, 10, 1, 0, DateTimeKind.Utc),
            System = new SystemInfo { CpuName = "CPU", GpuNames = { "GPU" } }, PresentMode = "Hardware: Independent Flip",
        };
        for (var i = 0; i < frames; i++) run.Frames.Add(new FrameSample(i * ft, ft));
        run.Summary = FrameStatistics.Compute(run.Frames.Select(f => f.FrameTimeMs));
        return run;
    }

    [Fact]
    public void Csv_escapes_quotes_commas_and_formula_injection()
    {
        Assert.Equal("\"a,b\"", Exporters.Csv("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", Exporters.Csv("say \"hi\""));
        Assert.Equal("'=cmd|' /C calc'!A0", Exporters.Csv("=cmd|' /C calc'!A0"));
        Assert.Equal("", Exporters.Csv(null));
    }

    [Fact]
    public void Benchmark_exports_contain_real_numbers()
    {
        var run = Run("Run 1", 10, 2000);
        var csv = Exporters.BenchmarkFramesCsv(run);
        Assert.StartsWith("TimeMs,FrameTimeMs,InstantFps", csv);
        Assert.Contains("10,10,100", csv);
        Assert.Equal(2001, csv.Trim().Split('\n').Length);

        var summary = Exporters.BenchmarkSummariesCsv(new[] { run });
        var lines = summary.Trim().Split('\n');
        Assert.Equal(Exporters.SummaryHeader, lines[0].Trim());
        Assert.Contains("\"Game, \"\"Deluxe\"\"\"", lines[1]);
        Assert.Contains(",100.0,", lines[1]);

        var json = Exporters.BenchmarkJson(run);
        var back = System.Text.Json.JsonSerializer.Deserialize<BenchmarkRun>(json, JsonDefaults.Options)!;
        Assert.Equal(2000, back.Frames.Count);
        Assert.Equal(run.Summary!.AverageFps, back.Summary!.AverageFps, 6);

        var report = Exporters.BenchmarkReport(run);
        Assert.Contains("Average FPS:      100.0", report);
        Assert.Contains("0.1% low FPS:", report);
    }

    [Fact]
    public void Comparison_flags_non_comparable_runs()
    {
        var a = Run("A", 10, 2000);
        var same = Run("B", 8, 2000);
        var c = BenchmarkComparer.Compare(a, same);
        Assert.True(c.DirectlyComparable);
        Assert.Equal(25, c.AverageFpsDeltaPercent!.Value, 6);

        var diff = Run("C", 8, 2000, res: "1920x1080", preset: null);
        var c2 = BenchmarkComparer.Compare(a, diff);
        Assert.False(c2.DirectlyComparable);
        Assert.Contains(c2.ComparabilityWarnings, w => w.Contains("resolution"));
        Assert.Contains(c2.ComparabilityWarnings, w => w.Contains("Graphics preset not recorded"));
        Assert.Contains("NOT be directly comparable", Exporters.ComparisonReport(c2));
    }

    [Fact]
    public void Sessions_export_csv_and_json()
    {
        var sessions = new[] { new GameSession { GameId = "a", GameName = "=Evil", StartUtc = DateTime.UtcNow.AddHours(-1), EndUtc = DateTime.UtcNow, FpsAvailability = FpsAvailability.Unavailable } };
        var csv = Exporters.SessionsCsv(sessions);
        Assert.Contains("'=Evil", csv);
        Assert.Contains("Unavailable", csv);
        Assert.Contains("\"GameName\": \"=Evil\"", Exporters.SessionsJson(sessions));
    }

    [Fact]
    public void Benchmark_store_round_trip_and_delete()
    {
        using var t = new TempDir();
        var store = new BenchmarkStore(t.Dir("bench"));
        var run = Run("Stored", 16.6, 500);
        store.Save(run);
        var fresh = new BenchmarkStore(Path.Combine(t.Root, "bench"));
        Assert.Single(fresh.GetAll());
        File.WriteAllText(Path.Combine(t.Root, "bench", "broken.json"), "{nope");
        Assert.Single(new BenchmarkStore(Path.Combine(t.Root, "bench")).GetAll()); // broken file skipped
        fresh.Delete(run.Id);
        Assert.Empty(fresh.GetAll());
    }
}

public class SettingsAndUtilityTests
{
    [Fact]
    public void Settings_persist_and_invalid_values_are_clamped()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "settings.json");
        using (var s = new SettingsService(file))
        {
            s.Current.Theme = AppTheme.Light;
            s.Current.Overlay.ToggleHotkey = "Ctrl+Alt+O";
            s.Current.Scan.CustomFolders.Add("/games");
            s.Current.Scan.EnabledLaunchers.Remove(LauncherKind.Roblox);
            s.SaveNow();
        }
        using var reloaded = new SettingsService(file);
        Assert.Equal(AppTheme.Light, reloaded.Current.Theme);
        Assert.Equal("Ctrl+Alt+O", reloaded.Current.Overlay.ToggleHotkey);
        Assert.Contains("/games", reloaded.Current.Scan.CustomFolders);
        Assert.DoesNotContain(LauncherKind.Roblox, reloaded.Current.Scan.EnabledLaunchers);

        var bad = new AppSettings { AccentColor = "purple", Updates = { FeedUrl = "http://insecure" } };
        bad.Monitoring.SampleIntervalMs = 1;
        bad.Overlay.FontSize = 500;
        SettingsService.Validate(bad);
        Assert.Equal("#8B5CF6", bad.AccentColor);
        Assert.Null(bad.Updates.FeedUrl);
        Assert.Equal(250, bad.Monitoring.SampleIntervalMs);
        Assert.Equal(48, bad.Overlay.FontSize);
    }

    [Fact]
    public void Settings_json_with_unknown_and_missing_fields_still_loads()
    {
        using var t = new TempDir();
        var file = t.File("settings.json", "{ \"Theme\": \"Light\", \"SomethingNew\": 5, \"Overlay\": null }");
        using var s = new SettingsService(file);
        Assert.Equal(AppTheme.Light, s.Current.Theme);
        Assert.NotNull(s.Current.Overlay);
    }

    [Theory]
    [InlineData("Ctrl+Shift+F10", 0x2u | 0x4u, 0x79u, "Ctrl+Shift+F10")]
    [InlineData("alt + o", 0x1u, (uint)'O', "Alt+O")]
    [InlineData("Win+Ctrl+Home", 0x8u | 0x2u, 0x24u, "Ctrl+Win+Home")]
    public void Hotkeys_parse(string text, uint mods, uint vk, string normalized)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(vk, g.VirtualKey);
        Assert.Equal(normalized, g.Text);
    }

    [Theory]
    [InlineData("F10")]          // no modifier → would steal the game's key
    [InlineData("Ctrl+Shift")]   // no key
    [InlineData("Ctrl+A+B")]     // two keys
    [InlineData("Ctrl+Banana")]
    [InlineData("")]
    public void Invalid_hotkeys_rejected(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Log_sanitizer_removes_personal_data_and_secrets()
    {
        LogSanitizer.Configure("alice", @"C:\Users\alice");
        try
        {
            var s = LogSanitizer.Sanitize(@"Opening C:\Users\alice\AppData\x and C:\Users\bob\Games, DESKTOP\alice, mail alice@example.com, access_token=abc123&x, Bearer eyJhbGciOi.xyz, max FPS");
            Assert.DoesNotContain("alice", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bob", s);
            Assert.DoesNotContain("abc123", s);
            Assert.DoesNotContain("eyJhbGciOi", s);
            Assert.Contains("%USERPROFILE%", s);
            Assert.Contains("max FPS", s);
        }
        finally
        {
            LogSanitizer.Configure(Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
    }

    [Theory]
    [InlineData("/games/x", true)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    [InlineData("/games/\u0001bad", false)]
    [InlineData("/games/a|b", false)]
    public void Path_validation(string path, bool ok)
    {
        if (OperatingSystem.IsWindows() && path.StartsWith('/')) path = "C:" + path.Replace('/', '\\');
        Assert.Equal(ok, PathUtil.IsSafeAbsolutePath(path));
    }

    [Fact]
    public void Path_containment()
    {
        Assert.True(PathUtil.IsUnder("/a/b/c.exe", "/a/b"));
        Assert.True(PathUtil.IsUnder("/A/B", "/a/b/"));
        Assert.False(PathUtil.IsUnder("/a/bc/x", "/a/b"));
        Assert.Null(PathUtil.SafeCombine("/a/b", "../../etc/passwd"));
        Assert.Equal(PathUtil.NormalizeKey("/a/b/c"), PathUtil.NormalizeKey(PathUtil.SafeCombine("/a/b", "c")!));
    }
}

public class NotificationTests
{
    private sealed class Sink : INotificationSink
    {
        public List<string> Shown { get; } = new();
        public void Show(string title, string message, NotificationLevel level) => Shown.Add(title);
    }

    [Fact]
    public void Per_type_preferences_are_respected()
    {
        var settings = new NotificationSettings { ScanCompleted = false };
        var center = new NotificationCenter(() => settings);
        var sink = new Sink();
        center.AttachSink(sink);
        Assert.False(center.Notify(NotificationKind.ScanCompleted, "scan", "x"));
        Assert.True(center.Notify(NotificationKind.GameLaunched, "launch", "x"));
        settings.Enabled = false;
        Assert.False(center.Notify(NotificationKind.GameLaunched, "launch", "x"));
        Assert.Equal(new[] { "launch" }, sink.Shown);
    }

    [Fact]
    public void Threshold_requires_sustained_breach_and_has_cooldown()
    {
        var clock = new ManualClock();
        var settings = new NotificationSettings { PerformanceThreshold = true, ThresholdSustainSeconds = 10 };
        var center = new NotificationCenter(() => settings);
        var sink = new Sink();
        center.AttachSink(sink);
        var th = new ThresholdMonitor(center, () => settings, clock.Func);
        var sustain = TimeSpan.FromSeconds(10);

        Assert.False(th.Check("cpu", true, sustain, "t", "m"));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(th.Check("cpu", true, sustain, "t", "m"));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(th.Check("cpu", true, sustain, "t", "m"));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(th.Check("cpu", true, sustain, "t", "m")); // cooldown
        Assert.False(th.Check("cpu", false, sustain, "t", "m")); // recovered resets
        Assert.Single(sink.Shown);
    }
}

public class SessionTrackerTests
{
    [Fact]
    public void Full_flow_launch_measure_exit_saves_session_with_real_fps()
    {
        using var t = new TempDir();
        var gameDir = t.Dir("Games", "Alpha");
        var exe = t.Exe("Games/Alpha/Alpha.exe");
        var log = new MemoryLog();
        var lib = new LibraryService(Path.Combine(t.Root, "library.json"), log);
        var game = lib.AddManual(exe, "Alpha");
        var sessions = new SessionStore(Path.Combine(t.Root, "sessions.json"), log);
        var settings = new AppSettings();
        var inspector = new FakeInspector();
        var clock = new ManualClock();
        var monitor = new RunningGamesMonitor(inspector, log, clock.Func);
        monitor.SetLibrary(lib.GetAll());
        var captures = new List<FakeCapture>();
        var fps = new FpsMonitorService(() => "/fake/PresentMon.exe", _ => { var c = new FakeCapture(); captures.Add(c); return c; }, () => settings.Fps, t.Dir("captures"), log, clock.Func);
        var system = new SystemMonitor(log, new FixedDriveProvider());
        using var tracker = new SessionTracker(monitor, fps, system, lib, sessions, () => settings, log, clock.Func);
        var starter = new FakeStarter { Pid = 4_300_001 };
        var launcher = new GameLauncher(starter, monitor, log);
        GameSession? ended = null;
        tracker.SessionEnded += s => ended = s;

        // PLAY
        var result = launcher.Launch(lib.Get(game.Id)!);
        Assert.True(result.Started);
        Assert.Equal(exe, starter.Started.Single().FileName);

        // Game process appears → session + capture start.
        inspector.Processes.Add(new ProcessSnapshot(4_300_001, "Alpha", exe));
        monitor.Poll();
        Assert.Single(tracker.Active);
        Assert.Single(captures);
        Assert.Contains("Alpha.exe", captures[0].StartedWith!);

        captures[0].Push(Frames.Make(4_300_001, "Alpha.exe", Frames.Repeat(10, 3000)));
        Assert.Equal(FpsCaptureState.Measuring, fps.State);

        // Game exits after 30 minutes.
        clock.Advance(TimeSpan.FromMinutes(30));
        inspector.Processes.Clear();
        monitor.Poll();

        Assert.NotNull(ended);
        Assert.Equal(FpsAvailability.Measured, ended!.FpsAvailability);
        Assert.Equal(100, ended.Fps!.AverageFps, 6);
        Assert.Equal(3000, ended.Fps.FrameCount);
        Assert.True(ended.LaunchedFromHub);
        Assert.Equal(TimeSpan.FromMinutes(30), ended.Duration);
        Assert.Single(sessions.GetAll());
        var updated = lib.Get(game.Id)!;
        Assert.Equal(1800, updated.TotalPlaytimeSeconds, 3);
        Assert.Equal(1, updated.SessionCount);
        Assert.NotNull(updated.LastPlayedUtc);
        Assert.True(captures[0].Stopped);

        // Persisted for the next start.
        Assert.Single(new SessionStore(Path.Combine(t.Root, "sessions.json")).GetAll());
        fps.Dispose();
    }

    [Fact]
    public void Unmeasurable_game_is_recorded_as_unavailable_and_app_close_finalises_sessions()
    {
        using var t = new TempDir();
        var exe = t.Exe("Games/Beta/Beta.exe");
        var log = new MemoryLog();
        var lib = new LibraryService(Path.Combine(t.Root, "library.json"), log);
        var game = lib.AddManual(exe, "Beta");
        var sessions = new SessionStore(Path.Combine(t.Root, "sessions.json"), log);
        var settings = new AppSettings();
        var inspector = new FakeInspector();
        var clock = new ManualClock();
        var monitor = new RunningGamesMonitor(inspector, log, clock.Func);
        monitor.SetLibrary(lib.GetAll());
        var fps = new FpsMonitorService(() => null, _ => new FakeCapture(), () => settings.Fps, t.Dir("captures"), log, clock.Func); // PresentMon missing
        using var tracker = new SessionTracker(monitor, fps, new SystemMonitor(log, new FixedDriveProvider()), lib, sessions, () => settings, log, clock.Func);

        inspector.Processes.Add(new ProcessSnapshot(4_300_100, "Beta", exe));
        monitor.Poll(); // started outside the hub
        Assert.Equal(FpsCaptureState.NotInstalled, fps.State);
        clock.Advance(TimeSpan.FromMinutes(5));
        tracker.FinalizeAll();

        var s = Assert.Single(sessions.GetAll());
        Assert.Equal(SessionEndReason.AppClosed, s.EndReason);
        Assert.False(s.LaunchedFromHub);
        Assert.Equal(FpsAvailability.Unavailable, s.FpsAvailability);
        Assert.Null(s.Fps);
    }

    [Fact]
    public void Tracking_disabled_records_nothing()
    {
        using var t = new TempDir();
        var exe = t.Exe("Games/Gamma/Gamma.exe");
        var log = new MemoryLog();
        var lib = new LibraryService(Path.Combine(t.Root, "library.json"), log);
        lib.AddManual(exe, "Gamma");
        var sessions = new SessionStore(Path.Combine(t.Root, "sessions.json"), log);
        var settings = new AppSettings { TrackPlaytime = false };
        var inspector = new FakeInspector();
        var monitor = new RunningGamesMonitor(inspector, log);
        monitor.SetLibrary(lib.GetAll());
        var fps = new FpsMonitorService(() => null, _ => new FakeCapture(), () => settings.Fps, t.Dir("c"), log);
        using var tracker = new SessionTracker(monitor, fps, new SystemMonitor(log, new FixedDriveProvider()), lib, sessions, () => settings, log);
        inspector.Processes.Add(new ProcessSnapshot(4_300_200, "Gamma", exe));
        monitor.Poll();
        inspector.Processes.Clear();
        monitor.Poll();
        Assert.Empty(sessions.GetAll());
    }
}

public class ViewModelSmokeTests
{
    [Fact]
    public void Game_editor_validates_and_applies_profile()
    {
        using var t = new TempDir();
        var game = new Game { Id = "x", Name = "Alpha", Launcher = LauncherKind.Steam };
        var vm = new ViewModels.GameEditorViewModel(game, new[] { "Shooters" }, null!)
        {
            CustomName = "Alpha GOTY",
            CustomArguments = "-dx12",
            CollectionsText = "Shooters, Favourites ,",
            AutoCaptureFps = false,
        };
        Assert.True(vm.Validate());
        vm.CustomExecutablePath = "/not/real.exe";
        Assert.False(vm.Validate());
        vm.CustomExecutablePath = t.Exe("real.exe");
        Assert.True(vm.Validate());
        vm.ApplyTo(game);
        Assert.Equal("Alpha GOTY", game.DisplayName);
        Assert.Equal(new[] { "Shooters", "Favourites" }, game.Collections);
        Assert.False(game.Profile.AutoCaptureFps);
        Assert.Equal(new[] { "Favourites" }, vm.NewCollections);
    }

    [Fact]
    public void Format_never_shows_numbers_for_missing_data()
    {
        Assert.Equal("—", ViewModels.Format.Fps(null));
        Assert.Equal("—", ViewModels.Format.Fps(double.NaN));
        Assert.Equal("—", ViewModels.Format.Ms(null));
        Assert.Equal("—", ViewModels.Format.Bytes(null));
        Assert.Equal("1.5 GB", ViewModels.Format.Bytes(1.5 * 1024 * 1024 * 1024).Replace(',', '.'));
        Assert.Equal("CS", ViewModels.Format.Initials("Counter-Strike 2"));
    }
}
