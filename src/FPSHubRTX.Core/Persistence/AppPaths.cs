namespace FPSHubRTX.Core.Persistence;

/// <summary>
/// Locations of all files the app writes. Everything lives in one per-user data folder:
/// %LOCALAPPDATA%\FPSHubRTX by default, or "Data" next to the exe for the portable build
/// (enabled by a file named "portable.txt" beside FPSHubRTX.exe).
/// The installer never touches this folder, so upgrades keep settings and statistics.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory, string applicationDirectory)
    {
        DataDirectory = dataDirectory;
        ApplicationDirectory = applicationDirectory;
    }

    public string DataDirectory { get; }
    public string ApplicationDirectory { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string LibraryFile => Path.Combine(DataDirectory, "library.json");
    public string SessionsFile => Path.Combine(DataDirectory, "sessions.json");
    public string BenchmarksDirectory => Path.Combine(DataDirectory, "benchmarks");
    public string CoversDirectory => Path.Combine(DataDirectory, "covers");
    public string CapturesDirectory => Path.Combine(DataDirectory, "captures");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    public string ReportsDirectory => Path.Combine(DataDirectory, "reports");

    public static AppPaths ForCurrentUser(string applicationDirectory)
    {
        var portableMarker = Path.Combine(applicationDirectory, "portable.txt");
        var overrideDir = Environment.GetEnvironmentVariable("FPSHUBRTX_DATA_DIR");
        string data;
        if (!string.IsNullOrWhiteSpace(overrideDir) && Path.IsPathFullyQualified(overrideDir))
        {
            // Used by automated smoke tests to run against an isolated data folder.
            data = overrideDir;
        }
        else if (File.Exists(portableMarker))
        {
            data = Path.Combine(applicationDirectory, "Data");
        }
        else
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
            data = Path.Combine(local, "FPSHubRTX");
        }
        var paths = new AppPaths(data, applicationDirectory);
        paths.EnsureCreated();
        return paths;
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataDirectory, BenchmarksDirectory, CoversDirectory, CapturesDirectory, LogsDirectory, BackupsDirectory, ReportsDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
