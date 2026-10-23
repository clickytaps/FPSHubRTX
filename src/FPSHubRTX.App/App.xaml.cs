using System.IO;
using System.Windows;
using System.Windows.Threading;
using FPSHubRTX.App.Services;
using FPSHubRTX.App.Views;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Persistence;
using FPSHubRTX.ViewModels;

namespace FPSHubRTX.App;

public partial class App : Application
{
    private const string SingleInstanceName = "FPSHubRTX.SingleInstance.v1";
    private const string ActivateEventName = "FPSHubRTX.Activate.v1";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private AppHost? _host;
    private MainViewModel? _main;
    private TrayIcon? _tray;
    private ILog _log = NullLog.Instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest(e.Args);
            return;
        }

        // One instance only: a second launch just brings the existing window forward.
        _singleInstance = new Mutex(true, SingleInstanceName, out var isFirst);
        if (!isFirst)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        var startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var appDir = AppContext.BaseDirectory;
        AppPaths paths;
        try
        {
            paths = AppPaths.ForCurrentUser(appDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("FPS Hub RTX could not create its data folder:\n\n" + ex.Message, "FPS Hub RTX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        SplashWindow? splash = null;
        if (!startMinimized && PeekShowSplash(paths))
        {
            splash = new SplashWindow();
            splash.Show();
        }

        var mainWindow = new MainWindow();
        var ui = new PlatformUi(mainWindow, Dispatcher);
        try
        {
            splash?.SetStatus("Loading library…");
            _host = new AppHost(paths, new StorePackageCatalog());
            _log = _host.Log;
            _host.Log.Info($"Starting (args: {string.Join(' ', e.Args)})");

            splash?.SetStatus("Starting monitors…");
            _main = new MainViewModel(_host, ui);
            mainWindow.DataContext = _main;
            mainWindow.Attach(_main, _host);
            ui.AttachOverlay(new OverlayWindow { DataContext = _main.Overlay }, _host);

            _tray = new TrayIcon(_main, mainWindow, _host, ExitApplication);
            _host.Notifications.AttachSink(_tray);
            _main.Initialize();
        }
        catch (Exception ex)
        {
            _log.Error("Startup failed", ex);
            splash?.Close();
            MessageBox.Show("FPS Hub RTX failed to start:\n\n" + ex.Message + "\n\nDetails were written to the log folder.", "FPS Hub RTX", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 1;
            Shutdown(1);
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var activateEvent = _activateEvent;
        var activateThread = new Thread(() =>
        {
            try
            {
                while (activateEvent.WaitOne())
                {
                    if (IsExiting) return;
                    Dispatcher.InvokeAsync(() => ui.ShowMainWindow());
                }
            }
            catch (ObjectDisposedException)
            {
                // App is shutting down.
            }
        }) { IsBackground = true, Name = "FPSHubRTX activation listener" };
        activateThread.Start();

        if (startMinimized && _host.S.MinimizeToTray)
        {
            // Stay in the tray.
        }
        else
        {
            mainWindow.Show();
            if (startMinimized) mainWindow.WindowState = WindowState.Minimized;
        }
        splash?.Close();
    }

    /// <summary>Headless end-to-end check (see SelfTest). No windows are shown.</summary>
    private void RunSelfTest(string[] args)
    {
        var exitCode = 1;
        try
        {
            var paths = AppPaths.ForCurrentUser(AppContext.BaseDirectory);
            using var host = new AppHost(paths, new StorePackageCatalog());
            _log = host.Log;
            exitCode = Task.Run(() => SelfTest.RunAsync(host, SelfTest.ParseArgs(args))).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Error("Self-test crashed", ex);
            try { Console.Error.WriteLine(ex); } catch (IOException) { }
        }
        Environment.ExitCode = exitCode; // the generated Main() is void, so set the process exit code explicitly
        Shutdown(exitCode);
    }

    private static bool PeekShowSplash(AppPaths paths)
    {
        try
        {
            return new JsonFileStore<Core.Models.AppSettings>(paths.SettingsFile, () => new Core.Models.AppSettings()).Load().ShowSplashScreen;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error("UI exception", e.Exception);
        MessageBox.Show("Something went wrong, but FPS Hub RTX will keep running.\n\n" + e.Exception.Message, "FPS Hub RTX",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>Real exit (tray "Exit", or closing the window when close-to-tray is off).</summary>
    public bool IsExiting { get; private set; }

    public void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;
        try
        {
            _main?.Dispose();
            _tray?.Dispose();
            _host?.Dispose(); // closes running sessions, stops PresentMon and monitors
        }
        catch (Exception ex)
        {
            _log.Error("Shutdown error", ex);
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateEvent?.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
