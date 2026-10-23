using System.Text.Json;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Launching;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Persistence;
using FPSHubRTX.Core.Services;

namespace FPSHubRTX.ViewModels;

/// <summary>
/// Headless end-to-end check used by build\smoke-test.ps1 and for troubleshooting:
///   FPSHubRTX.exe --self-test [--launch "Game name"] [--report path]
/// Scans with the current settings, verifies the library survives a reload, resolves launch plans,
/// samples system metrics, and optionally launches one game and waits for it to start and exit so the
/// session (and its FPS availability) is recorded. Writes a JSON report; exit code 0 = success.
/// </summary>
public static class SelfTest
{
    public sealed class Options
    {
        public string? LaunchGameName { get; init; }
        /// <summary>Launch arguments to set on the game (as a user would in its settings) before launching.</summary>
        public string? LaunchArguments { get; init; }
        public string? ReportPath { get; init; }
        public int TimeoutSeconds { get; init; } = 60;
    }

    public static Options ParseArgs(IReadOnlyList<string> args)
    {
        string? Value(string flag)
        {
            for (var i = 0; i < args.Count - 1; i++)
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
        return new Options
        {
            LaunchGameName = Value("--launch"),
            LaunchArguments = Value("--launch-args"),
            ReportPath = Value("--report"),
            TimeoutSeconds = int.TryParse(Value("--timeout"), out var t) ? Math.Clamp(t, 5, 600) : 60,
        };
    }

    public static async Task<int> RunAsync(AppHost host, Options options)
    {
        var report = new Dictionary<string, object?>
        {
            ["startedUtc"] = DateTime.UtcNow,
            ["version"] = UpdateChecker.CurrentVersion.ToString(),
            ["dataDirectory"] = LogSanitizer.Sanitize(host.Paths.DataDirectory),
        };
        var failures = new List<string>();

        // 1. Scan
        var summary = await host.Scanner.ScanAsync(host.S.Clone().Scan, null, CancellationToken.None);
        report["scan"] = summary.SourceResults.Select(r => new { Source = r.Kind.ToString(), r.LauncherDetected, r.Completed, Games = r.Games.Count, r.Error, Warnings = r.Warnings.Count }).ToList();
        var merge = host.Library.MergeScan(summary, markMissing: true);
        report["gamesFound"] = summary.Games.Count;
        report["newGames"] = merge.Added.Count;
        if (summary.SourceResults.Any(r => r.Error is not null)) failures.Add("A scanner reported an error.");

        // 2. Persistence: a fresh instance must read back the same library.
        var reloaded = new LibraryService(host.Paths.LibraryFile, host.Log);
        report["libraryCountAfterReload"] = reloaded.Count;
        if (reloaded.Count < summary.Games.Count) failures.Add("Library did not persist all games.");

        // 3. Launch-method resolution for every game (nothing is launched here).
        report["games"] = reloaded.GetAll().Select(g =>
        {
            var r = LaunchResolver.Resolve(g);
            return new { g.Name, Launcher = g.Launcher.ToString(), Status = g.Status.ToString(), Launchable = r.Success, Method = r.Plan?.Kind.ToString(), r.Error };
        }).ToList();

        // 4. FPS tooling and system metrics availability.
        report["presentMon"] = host.PresentMonPath is null ? "NOT FOUND" : LogSanitizer.Sanitize(host.PresentMonPath);
        report["elevated"] = DiagnosticReport.IsElevated();
        report["performanceLogUsers"] = DiagnosticReport.IsInPerformanceLogUsers();
        host.System.Sample();
        await Task.Delay(1000);
        var s = host.System.Sample();
        report["system"] = new
        {
            Cpu = s.CpuPercent,
            Gpu = s.GpuPercent,
            GpuNote = s.GpuNote,
            RamUsedBytes = s.RamUsedBytes,
            VramUsedBytes = s.GpuMemoryUsedBytes,
            GpuTemp = s.GpuTemperatureC,
            CpuTemp = s.CpuTemperatureC,
            Drives = s.Drives.Count,
        };

        // 5. Optional real launch → detection → exit → session saved.
        if (!string.IsNullOrWhiteSpace(options.LaunchGameName))
        {
            var game = reloaded.GetAll().FirstOrDefault(g => g.DisplayName.Equals(options.LaunchGameName, StringComparison.OrdinalIgnoreCase));
            if (game is null)
            {
                failures.Add($"Game '{options.LaunchGameName}' was not found by the scan.");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(options.LaunchArguments))
                {
                    host.Library.Update(game.Id, g => g.CustomArguments = options.LaunchArguments);
                    game = host.Library.Get(game.Id)!;
                }
                report["launch"] = await LaunchAndWatchAsync(host, game, TimeSpan.FromSeconds(options.TimeoutSeconds), failures);
            }
        }

        report["failures"] = failures;
        report["success"] = failures.Count == 0;
        report["finishedUtc"] = DateTime.UtcNow;

        var path = options.ReportPath ?? Path.Combine(host.Paths.ReportsDirectory, "self-test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonDefaults.Options));
        host.Log.Info($"Self-test finished: {(failures.Count == 0 ? "success" : string.Join(" ", failures))}");
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task<object> LaunchAndWatchAsync(AppHost host, Game game, TimeSpan timeout, List<string> failures)
    {
        var started = new TaskCompletionSource<RunningGame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<GameSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Running.GameStarted += (_, rg) => { if (rg.GameId == game.Id) started.TrySetResult(rg); };
        host.Tracker.SessionEnded += s => { if (s.GameId == game.Id) ended.TrySetResult(s); };
        host.Running.SetLibrary(host.Library.GetAll());
        host.Running.PollInterval = TimeSpan.FromSeconds(1);
        host.Running.Start();

        var result = host.Launcher.Launch(game);
        if (!result.Started)
        {
            failures.Add("Launch failed: " + result.Error);
            return new { Started = false, result.Error };
        }

        var detected = await Task.WhenAny(started.Task, Task.Delay(timeout)) == started.Task;
        if (!detected)
        {
            failures.Add("The launched game's process was not detected.");
            return new { Started = true, Detected = false };
        }

        var exited = await Task.WhenAny(ended.Task, Task.Delay(timeout)) == ended.Task;
        if (!exited)
        {
            failures.Add("The game did not exit within the timeout (session not saved).");
            return new { Started = true, Detected = true, Exited = false };
        }

        var session = await ended.Task;
        var persisted = new SessionStore(host.Paths.SessionsFile).GetAll().Any(s => s.Id == session.Id);
        if (!persisted) failures.Add("The session was not persisted.");
        return new
        {
            Started = true,
            Detected = true,
            Exited = true,
            SessionSaved = persisted,
            DurationSeconds = Math.Round(session.Duration.TotalSeconds, 1),
            session.ExitCode,
            FpsAvailability = session.FpsAvailability.ToString(),
            session.FpsUnavailableReason,
            AverageFps = session.Fps?.AverageFps,
            Method = result.Plan?.Kind.ToString(),
        };
    }
}
