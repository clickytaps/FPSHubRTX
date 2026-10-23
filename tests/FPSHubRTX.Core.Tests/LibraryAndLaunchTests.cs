using System.ComponentModel;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Launching;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Persistence;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Scanning;
using FPSHubRTX.Core.Services;

namespace FPSHubRTX.Core.Tests;

public class GameScanServiceTests
{
    private sealed class StubScanner : IGameScanner
    {
        private readonly Func<ScanContext, ScannerResult> _scan;
        public StubScanner(LauncherKind kind, Func<ScanContext, ScannerResult> scan) { Kind = kind; _scan = scan; }
        public LauncherKind Kind { get; }
        public string DisplayName => Kind.ToString();
        public ScannerResult Scan(ScanContext context) => _scan(context);
    }

    private static Game G(LauncherKind k, string id, string? path, string name = "G")
        => new() { Id = $"{k.ToString().ToLowerInvariant()}:{id}", SourceId = id, Launcher = k, Name = name, InstallPath = path, Status = InstallStatus.Installed };

    [Fact]
    public async Task Failing_scanner_does_not_stop_scan_and_progress_is_reported()
    {
        using var t = new TempDir();
        var scanners = new IGameScanner[]
        {
            new StubScanner(LauncherKind.Steam, _ => throw new InvalidDataException("corrupt manifest")),
            new StubScanner(LauncherKind.Epic, _ => { var r = new ScannerResult(LauncherKind.Epic) { Completed = true, LauncherDetected = true }; r.Games.Add(G(LauncherKind.Epic, "a", "/x/a")); return r; }),
        };
        var service = new GameScanService(new InMemoryRegistry(), new FixedDriveProvider(), EmptyPackageCatalog.Instance, new MemoryLog(), () => Ctx.Env(t), scanners);
        var reports = new List<ScanProgress>();
        var summary = await service.ScanAsync(new ScanSettings(), new SyncProgress<ScanProgress>(reports.Add), CancellationToken.None);

        Assert.Single(summary.Games);
        Assert.Equal("corrupt manifest", summary.SourceResults[0].Error);
        Assert.False(summary.SourceResults[0].Completed);
        Assert.Contains(reports, r => r.Stage == ScanStage.Completed && r.Fraction == 1);
        Assert.Contains(reports, r => r.CurrentSource == "Epic");
    }

    [Fact]
    public async Task Cancellation_stops_early_and_is_reported()
    {
        using var t = new TempDir();
        using var cts = new CancellationTokenSource();
        var ran = new List<LauncherKind>();
        var scanners = new IGameScanner[]
        {
            new StubScanner(LauncherKind.Steam, c => { ran.Add(LauncherKind.Steam); cts.Cancel(); c.ThrowIfCancelled(); return new ScannerResult(LauncherKind.Steam); }),
            new StubScanner(LauncherKind.Epic, _ => { ran.Add(LauncherKind.Epic); return new ScannerResult(LauncherKind.Epic); }),
        };
        var service = new GameScanService(new InMemoryRegistry(), new FixedDriveProvider(), EmptyPackageCatalog.Instance, new MemoryLog(), () => Ctx.Env(t), scanners);
        var summary = await service.ScanAsync(new ScanSettings(), null, cts.Token);
        Assert.True(summary.Cancelled);
        Assert.Equal(new[] { LauncherKind.Steam }, ran);
    }

    [Fact]
    public async Task Disabled_launchers_are_skipped()
    {
        using var t = new TempDir();
        var ran = new List<LauncherKind>();
        var scanners = Enum.GetValues<LauncherKind>().Where(k => k != LauncherKind.Manual)
            .Select(k => (IGameScanner)new StubScanner(k, _ => { ran.Add(k); return new ScannerResult(k) { Completed = true }; })).ToArray();
        var service = new GameScanService(new InMemoryRegistry(), new FixedDriveProvider(), EmptyPackageCatalog.Instance, new MemoryLog(), () => Ctx.Env(t), scanners);
        await service.ScanAsync(new ScanSettings { EnabledLaunchers = new HashSet<LauncherKind> { LauncherKind.Gog } }, null, CancellationToken.None);
        Assert.Equal(new[] { LauncherKind.Gog }, ran);
    }

    [Fact]
    public void Deduplicate_prefers_launcher_entries_and_drops_container_folders()
    {
        var games = new[]
        {
            G(LauncherKind.Steam, "1", "/games/steamapps/common/A", "A"),
            G(LauncherKind.Standalone, "x1", "/games/steamapps/common/A", "A"),     // same folder → launcher wins
            G(LauncherKind.Standalone, "x2", "/games/steamapps", "steamapps"),       // container of a launcher game
            G(LauncherKind.Standalone, "x3", "/games/Indie", "Indie"),
            G(LauncherKind.Steam, "1", "/games/steamapps/common/A", "A"),            // same id twice
            G(LauncherKind.Minecraft, "launcher", null, "Minecraft Launcher"),
        };
        var unique = GameScanService.Deduplicate(games, out var removed);
        Assert.Equal(3, unique.Count);
        Assert.Equal(3, removed);
        Assert.Contains(unique, g => g.Id == "steam:1");
        Assert.Contains(unique, g => g.Id == "standalone:x3");
        Assert.Contains(unique, g => g.InstallPath is null);
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) { lock (this) _a(value); }
    }
}

public class LibraryServiceTests
{
    private static ScanSummary Summary(IEnumerable<Game> games, params LauncherKind[] completed)
    {
        var s = new ScanSummary();
        foreach (var k in completed) s.SourceResults.Add(new ScannerResult(k) { Completed = true });
        s.Games.AddRange(games);
        return s;
    }

    private static Game Steam(string id, string name, string path) => new()
    {
        Id = "steam:" + id, SourceId = id, Launcher = LauncherKind.Steam, Name = name, InstallPath = path, Status = InstallStatus.Installed,
        LaunchMethods = { LaunchMethod.ProtocolUri("steam://rungameid/" + id, "Steam") },
    };

    [Fact]
    public void Library_persists_across_instances()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "library.json");
        var lib = new LibraryService(file);
        lib.MergeScan(Summary(new[] { Steam("1", "Alpha", "/g/a"), Steam("2", "Beta", "/g/b") }, LauncherKind.Steam));
        lib.Update("steam:1", g => { g.IsFavorite = true; g.CustomName = "My Alpha"; });

        var reloaded = new LibraryService(file);
        Assert.Equal(2, reloaded.Count);
        var a = reloaded.Get("steam:1")!;
        Assert.True(a.IsFavorite);
        Assert.Equal("My Alpha", a.DisplayName);
        Assert.Equal(LaunchMethodKind.Uri, a.LaunchMethods[0].Kind);
        Assert.NotNull(reloaded.LastScanUtc);
    }

    [Fact]
    public void Rescan_preserves_customisations_and_marks_missing_only_for_completed_sources()
    {
        using var t = new TempDir();
        var lib = new LibraryService(Path.Combine(t.Root, "library.json"));
        var epicGame = new Game { Id = "epic:E", SourceId = "E", Launcher = LauncherKind.Epic, Name = "Epic Game", InstallPath = "/g/e", Status = InstallStatus.Installed };
        lib.MergeScan(Summary(new[] { Steam("1", "Alpha", "/g/a"), Steam("2", "Beta", "/g/b"), epicGame }, LauncherKind.Steam, LauncherKind.Epic));
        lib.Update("steam:1", g => { g.CustomArguments = "-novid"; g.Notes = "note"; g.TotalPlaytimeSeconds = 3600; g.Collections.Add("Shooters"); });

        // Second scan: Steam completed and only reports game 1 (renamed); Epic scanner did not complete.
        var result = lib.MergeScan(Summary(new[] { Steam("1", "Alpha Remastered", "/g/a2") }, LauncherKind.Steam));

        var a = lib.Get("steam:1")!;
        Assert.Equal("Alpha Remastered", a.Name);
        Assert.Equal("/g/a2", a.InstallPath);
        Assert.Equal("-novid", a.CustomArguments);
        Assert.Equal("note", a.Notes);
        Assert.Equal(3600, a.TotalPlaytimeSeconds);
        Assert.Contains("Shooters", a.Collections);

        Assert.Equal(InstallStatus.Missing, lib.Get("steam:2")!.Status);       // gone from a completed source
        Assert.Equal(InstallStatus.Installed, lib.Get("epic:E")!.Status);      // source didn't complete → untouched
        Assert.Single(result.NowMissing);
        Assert.Equal(3, lib.Count);                                            // nothing deleted
    }

    [Fact]
    public void Manual_add_remove_and_relocate_never_touch_files()
    {
        using var t = new TempDir();
        var exe = t.Exe("Old/MyGame/MyGame.exe");
        var lib = new LibraryService(Path.Combine(t.Root, "library.json"));
        var g = lib.AddManual(exe, null, "-windowed");
        Assert.Equal(LauncherKind.Manual, g.Launcher);
        Assert.Equal("MyGame", g.Name);
        Assert.Equal(InstallStatus.Installed, g.Status);

        // "Move" the game and relocate.
        var newDir = t.Dir("New", "MyGame");
        File.Copy(exe, Path.Combine(newDir, "MyGame.exe"));
        Assert.True(lib.Relocate(g.Id, newDir));
        var moved = lib.Get(g.Id)!;
        Assert.Equal(newDir, moved.InstallPath);
        Assert.Equal(Path.Combine(newDir, "MyGame.exe"), moved.ExecutablePath);
        Assert.Equal(Path.Combine(newDir, "MyGame.exe"), moved.LaunchMethods[0].Target);

        Assert.True(lib.Remove(g.Id));
        Assert.Equal(0, lib.Count);
        Assert.True(File.Exists(exe));                       // game files untouched
        Assert.True(File.Exists(Path.Combine(newDir, "MyGame.exe")));
        Assert.Throws<ArgumentException>(() => lib.AddManual(Path.Combine(t.Root, "notes.txt")));
    }

    [Fact]
    public void Manual_entry_upgrades_to_launcher_entry_instead_of_duplicating()
    {
        using var t = new TempDir();
        var exe = t.Exe("SteamLib/common/Alpha/alpha.exe");
        var lib = new LibraryService(Path.Combine(t.Root, "library.json"));
        var manual = lib.AddManual(exe);
        lib.Update(manual.Id, g => g.IsFavorite = true);
        lib.MergeScan(Summary(new[] { Steam("1", "Alpha", Path.GetDirectoryName(exe)!) }, LauncherKind.Steam));
        Assert.Equal(1, lib.Count);
        var g = lib.Get("steam:1")!;
        Assert.True(g.IsFavorite);
        Assert.Equal(LauncherKind.Steam, g.Launcher);
    }

    [Fact]
    public void Corrupt_library_file_falls_back_to_backup_without_crashing()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "library.json");
        var lib = new LibraryService(file);
        lib.MergeScan(Summary(new[] { Steam("1", "Alpha", "/g/a") }, LauncherKind.Steam));
        lib.Update("steam:1", g => g.IsFavorite = true); // second save creates a .bak
        File.WriteAllText(file, "{ broken json");

        var log = new MemoryLog();
        var reloaded = new LibraryService(file, log);
        Assert.Equal(1, reloaded.Count);
        Assert.Contains(log.Entries, e => e.Message.Contains("backup", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(Directory.GetFiles(t.Root, "library.json.corrupt-*"));

        File.Delete(file + ".bak");
        File.WriteAllText(file, "");
        Assert.Equal(0, new LibraryService(file).Count); // empty → fresh library, no exception
    }

    [Fact]
    public void Backup_and_restore_round_trip()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Dir("data"), t.Root);
        paths.EnsureCreated();
        var lib = new LibraryService(paths.LibraryFile);
        lib.MergeScan(Summary(new[] { Steam("1", "Alpha", "/g/a") }, LauncherKind.Steam));
        var zip = BackupService.CreateBackup(paths);

        lib.Remove("steam:1");
        Assert.Equal(0, lib.Count);
        var safety = BackupService.Restore(paths, zip, new MemoryLog());
        lib.Reload();
        Assert.Equal(1, lib.Count);
        Assert.True(File.Exists(safety));

        var notBackup = Path.Combine(t.Root, "random.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(t.Dir("empty"), notBackup);
        Assert.Throws<InvalidDataException>(() => BackupService.Restore(paths, notBackup, new MemoryLog()));
    }

    [Fact]
    public void Health_check_flags_missing_and_duplicates()
    {
        var games = new List<Game>
        {
            new() { Id = "steam:1", Name = "Alpha", Launcher = LauncherKind.Steam, Status = InstallStatus.Missing, StatusDetail = "gone" },
            new() { Id = "epic:1", Name = "ALPHA", Launcher = LauncherKind.Epic, Status = InstallStatus.Installed, LaunchMethods = { LaunchMethod.ProtocolUri("com.epicgames.launcher://x", "Epic") } },
            new() { Id = "manual:1", Name = "Beta", Launcher = LauncherKind.Manual, Status = InstallStatus.Installed, CustomExecutablePath = "/nope/beta.exe" },
        };
        var issues = LibraryHealth.Check(games);
        Assert.Contains(issues, i => i.GameId == "steam:1" && i.Severity == HealthSeverity.Problem);
        Assert.Contains(issues, i => i.GameId == "manual:1" && i.Issue.Contains("custom executable"));
        Assert.Equal(2, issues.Count(i => i.Issue.StartsWith("Possible duplicate")));
    }
}

public class LaunchResolverTests
{
    private static Game Game(params LaunchMethod[] methods) => new()
    {
        Id = "x:1", Name = "X", Launcher = LauncherKind.Steam, Status = InstallStatus.Installed, LaunchMethods = methods.ToList(),
        Metadata = { ["SteamAppId"] = "570" },
    };

    [Fact]
    public void Automatic_uses_first_valid_method_and_skips_missing_exe()
    {
        var g = Game(LaunchMethod.Exe("/missing/game.exe"), LaunchMethod.ProtocolUri("steam://rungameid/570", "Steam"));
        var r = LaunchResolver.Resolve(g, _ => false);
        Assert.True(r.Success);
        Assert.Equal("steam://rungameid/570", r.Plan!.FileName);
        Assert.True(r.Plan.UseShellExecute);
    }

    [Fact]
    public void Direct_preference_uses_exe_with_working_dir_and_custom_args()
    {
        using var t = new TempDir();
        var exe = t.Exe("Game/bin/game.exe");
        var g = Game(LaunchMethod.ProtocolUri("steam://rungameid/570", "Steam"), LaunchMethod.Exe(exe, "-base"));
        g.LaunchPreference = LaunchPreference.DirectExecutable;
        g.CustomArguments = "-novid -high";
        var r = LaunchResolver.Resolve(g);
        Assert.True(r.Success);
        Assert.Equal(exe, r.Plan!.FileName);
        Assert.Equal("-base -novid -high", r.Plan.Arguments);
        Assert.Equal(Path.GetDirectoryName(exe), r.Plan.WorkingDirectory);
        Assert.False(r.Plan.UseShellExecute);
    }

    [Fact]
    public void Custom_executable_wins_in_automatic_mode()
    {
        using var t = new TempDir();
        var custom = t.Exe("Game/real.exe");
        var g = Game(LaunchMethod.ProtocolUri("steam://rungameid/570", "Steam"));
        g.CustomExecutablePath = custom;
        Assert.Equal(custom, LaunchResolver.Resolve(g).Plan!.FileName);
    }

    [Fact]
    public void Steam_custom_arguments_go_through_run_uri()
    {
        var g = Game(LaunchMethod.ProtocolUri("steam://rungameid/570", "Steam"));
        g.CustomArguments = "-console";
        var plan = LaunchResolver.Resolve(g).Plan!;
        Assert.Equal("steam://run/570//-console/", plan.FileName);
    }

    [Fact]
    public void Arguments_for_non_steam_uri_produce_a_warning()
    {
        var g = Game(LaunchMethod.ProtocolUri("uplay://launch/5266/0", "Ubisoft"));
        g.Metadata.Clear();
        g.CustomArguments = "-x";
        var plan = LaunchResolver.Resolve(g).Plan!;
        Assert.Equal("uplay://launch/5266/0", plan.FileName);
        Assert.NotEmpty(plan.Warnings);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("steam://rungameid/1 && calc")]
    [InlineData("http://example.com")]
    public void Disallowed_uris_are_rejected(string uri)
    {
        var r = LaunchResolver.Resolve(Game(LaunchMethod.ProtocolUri(uri, "x")));
        Assert.False(r.Success);
    }

    [Fact]
    public void Packaged_app_launches_via_shell_appsfolder_and_validates_aumid()
    {
        var ok = LaunchResolver.Resolve(Game(LaunchMethod.Package("Microsoft.MinecraftUWP_8wekyb3d8bbwe!App", "Store")));
        Assert.Equal("explorer.exe", ok.Plan!.FileName);
        Assert.Equal("shell:AppsFolder\\Microsoft.MinecraftUWP_8wekyb3d8bbwe!App", ok.Plan.Arguments);
        Assert.False(LaunchResolver.Resolve(Game(LaunchMethod.Package("bad\" & calc", "x"))).Success);
        Assert.False(LaunchResolver.IsValidAumid("NoBang"));
    }

    [Fact]
    public void Newlines_in_arguments_are_rejected()
    {
        using var t = new TempDir();
        var g = Game(LaunchMethod.Exe(t.Exe("g.exe")));
        g.CustomArguments = "-a\r\n-b";
        Assert.False(LaunchResolver.Resolve(g).Success);
    }

    [Fact]
    public void Missing_game_explains_how_to_fix()
    {
        var g = Game(LaunchMethod.Exe("/gone/game.exe"));
        g.Status = InstallStatus.Missing;
        var r = LaunchResolver.Resolve(g);
        Assert.False(r.Success);
        Assert.Contains("Locate installation", r.Error);
    }

    [Fact]
    public void No_methods_gives_actionable_error()
    {
        var r = LaunchResolver.Resolve(Game());
        Assert.Contains("executable manually", r.Error);
    }

    [Fact]
    public void Launcher_reports_win32_errors_with_diagnostics()
    {
        using var t = new TempDir();
        var monitor = new RunningGamesMonitor(new FakeInspector(), new MemoryLog());
        var starter = new FakeStarter { Throw = new Win32Exception(740) };
        var launcher = new GameLauncher(starter, monitor, new MemoryLog());
        var g = Game(LaunchMethod.Exe(t.Exe("needsadmin.exe")));
        var result = launcher.Launch(g);
        Assert.False(result.Started);
        Assert.True(result.RequiresElevation);
        Assert.Contains("740", result.Diagnostic);
        Assert.False(monitor.IsPending(g.Id));

        starter.Throw = null;
        var ok = launcher.Launch(g);
        Assert.True(ok.Started);
        Assert.True(monitor.IsPending(g.Id));
    }
}

public class RunningGamesMonitorTests
{
    [Fact]
    public void Detects_start_child_processes_and_exit()
    {
        using var t = new TempDir();
        var dir = t.Dir("Games", "Alpha");
        var inspector = new FakeInspector();
        var clock = new ManualClock();
        var monitor = new RunningGamesMonitor(inspector, new MemoryLog(), clock.Func);
        monitor.SetLibrary(new[] { new Game { Id = "a", Name = "Alpha", InstallPath = dir } });
        RunningGame? started = null;
        GameExitedEventArgs? exited = null;
        monitor.GameStarted += (_, g) => started = g;
        monitor.GameExited += (_, e) => exited = e;

        monitor.ExpectLaunch("a", TimeSpan.FromSeconds(60));
        inspector.Processes.Add(new ProcessSnapshot(4_200_001, "launcher", Path.Combine(dir, "launcher.exe")));
        monitor.Poll();
        Assert.NotNull(started);
        Assert.True(started!.LaunchedFromHub);
        Assert.False(monitor.IsPending("a"));

        // Launcher stub exits, real game process continues → still running.
        inspector.Processes.Clear();
        inspector.Processes.Add(new ProcessSnapshot(4_200_002, "Alpha-Win64-Shipping", Path.Combine(dir, "Binaries", "Alpha-Win64-Shipping.exe")));
        monitor.Poll();
        Assert.Null(exited);
        Assert.Contains("Alpha-Win64-Shipping.exe", monitor.Running.Single().ExecutableNames);

        inspector.Processes.Clear();
        monitor.Poll();
        Assert.NotNull(exited);
        Assert.Empty(monitor.Running);
    }

    [Fact]
    public void Unrelated_processes_and_drive_roots_are_ignored()
    {
        using var t = new TempDir();
        var inspector = new FakeInspector();
        var monitor = new RunningGamesMonitor(inspector, new MemoryLog());
        monitor.SetLibrary(new[]
        {
            new Game { Id = "a", Name = "A", InstallPath = t.Dir("Games", "Alpha") },
            new Game { Id = "root", Name = "Root", InstallPath = "/" },
        });
        inspector.Processes.Add(new ProcessSnapshot(4_200_010, "explorer", "/Windows/explorer.exe"));
        inspector.Processes.Add(new ProcessSnapshot(4_200_011, "AlphaTool", Path.Combine(t.Root, "Games", "AlphaTool", "x.exe")));
        monitor.Poll();
        Assert.Empty(monitor.Running);
    }

    [Fact]
    public void Pending_launch_times_out()
    {
        var clock = new ManualClock();
        var monitor = new RunningGamesMonitor(new FakeInspector(), new MemoryLog(), clock.Func);
        string? timedOut = null;
        monitor.LaunchTimedOut += (_, id) => timedOut = id;
        monitor.ExpectLaunch("a", TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(31));
        monitor.Poll();
        Assert.Equal("a", timedOut);
    }

    [Fact]
    public void Process_name_hint_only_applies_while_launch_pending()
    {
        var inspector = new FakeInspector();
        var monitor = new RunningGamesMonitor(inspector, new MemoryLog());
        var game = new Game { Id = "mc", Name = "Minecraft", InstallPath = "/opt/Minecraft Launcher" };
        game.Metadata["ProcessNameHint"] = "javaw.exe";
        monitor.SetLibrary(new[] { game });
        inspector.Processes.Add(new ProcessSnapshot(4_200_020, "javaw", "/usr/lib/jvm/bin/javaw.exe"));
        monitor.Poll();
        Assert.Empty(monitor.Running); // some unrelated Java app

        monitor.ExpectLaunch("mc", TimeSpan.FromMinutes(1));
        monitor.Poll();
        Assert.Single(monitor.Running);
    }
}
