using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Launching;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Monitoring;
using FPSHubRTX.Core.Persistence;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Scanning;
using FPSHubRTX.Core.Services;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.ViewModels;

/// <summary>Composition root: creates and wires every service. Owned by the app for its whole lifetime.</summary>
public sealed class AppHost : IDisposable
{
    public AppHost(AppPaths paths, IPackageCatalog packages, IRegistryReader? registry = null, IProcessInspector? inspector = null,
        IProcessStarter? starter = null, IDriveProvider? drives = null, ILog? log = null)
    {
        Paths = paths;
        var fileLog = log as FileLog;
        Log = log ?? (fileLog = new FileLog(paths.LogsDirectory));
        FileLog = fileLog;
        Settings = new SettingsService(paths.SettingsFile, Log);
        Library = new LibraryService(paths.LibraryFile, Log);
        Sessions = new SessionStore(paths.SessionsFile, Log);
        Benchmarks = new BenchmarkStore(paths.BenchmarksDirectory, Log);
        Drives = drives ?? new SystemDriveProvider();
        Registry = registry ?? new WindowsRegistryReader();
        Scanner = new GameScanService(Registry, Drives, packages, Log);
        Inspector = inspector ?? new SystemProcessInspector();
        Running = new RunningGamesMonitor(Inspector, Log);
        Launcher = new GameLauncher(starter ?? new SystemProcessStarter(), Running, Log);
        Fps = new FpsMonitorService(
            () => PresentMonLocator.Find(paths.ApplicationDirectory, Settings.Current.Fps.PresentMonPath),
            exe => new PresentMonFrameCapture(exe, Log),
            () => Settings.Current.Fps,
            paths.CapturesDirectory,
            Log);
        System = new SystemMonitor(Log, Drives) { MinimalMode = Settings.Current.Monitoring.MinimalResourceMode };
        Tracker = new SessionTracker(Running, Fps, System, Library, Sessions, () => Settings.Current, Log);
        Bench = new BenchmarkService(Fps, System, Benchmarks, () => Settings.Current.Fps, Log);
        Notifications = new NotificationCenter(() => Settings.Current.Notifications);
        Thresholds = new ThresholdMonitor(Notifications, () => Settings.Current.Notifications);

        Running.SetLibrary(Library.GetAll());
        Library.Changed += (_, _) => Running.SetLibrary(Library.GetAll());
        CleanupOldCaptures();
    }

    public AppPaths Paths { get; }
    public ILog Log { get; }
    public FileLog? FileLog { get; }
    public SettingsService Settings { get; }
    public LibraryService Library { get; }
    public SessionStore Sessions { get; }
    public BenchmarkStore Benchmarks { get; }
    public IDriveProvider Drives { get; }
    public IRegistryReader Registry { get; }
    public GameScanService Scanner { get; }
    public IProcessInspector Inspector { get; }
    public RunningGamesMonitor Running { get; }
    public GameLauncher Launcher { get; }
    public FpsMonitorService Fps { get; }
    public SystemMonitor System { get; }
    public SessionTracker Tracker { get; }
    public BenchmarkService Bench { get; }
    public NotificationCenter Notifications { get; }
    public ThresholdMonitor Thresholds { get; }

    public AppSettings S => Settings.Current;

    public string? PresentMonPath => PresentMonLocator.Find(Paths.ApplicationDirectory, S.Fps.PresentMonPath);

    public void Start()
    {
        Running.Start();
        if (S.Monitoring.MonitorOnStartup) System.Start(S.Monitoring.SampleIntervalMs);
        Log.Info($"FPS Hub RTX {UpdateChecker.CurrentVersion} started. Data: {LogSanitizer.Sanitize(Paths.DataDirectory)}");
    }

    /// <summary>Stale capture CSVs from a crash are removed on startup (kept ones are under 7 days old).</summary>
    private void CleanupOldCaptures()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Paths.CapturesDirectory, "capture-*.csv"))
            {
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Capture cleanup failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            Tracker.FinalizeAll();
        }
        catch (Exception ex)
        {
            Log.Error("Finalising sessions failed", ex);
        }
        Tracker.Dispose();
        Fps.Dispose();
        System.Dispose();
        Running.Dispose();
        Settings.Dispose();
        Log.Info("FPS Hub RTX stopped.");
        FileLog?.Dispose();
    }
}
