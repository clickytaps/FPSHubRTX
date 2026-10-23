using System.Text.Json.Serialization;

namespace FPSHubRTX.Core.Models;

/// <summary>Where a game was discovered.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LauncherKind>))]
public enum LauncherKind
{
    Steam,
    Epic,
    Xbox,
    Gog,
    Ubisoft,
    EA,
    BattleNet,
    Riot,
    Minecraft,
    Roblox,
    Standalone,
    Manual,
}

[JsonConverter(typeof(JsonStringEnumConverter<InstallStatus>))]
public enum InstallStatus
{
    Unknown,
    Installed,
    /// <summary>Launcher reports the install is not complete (downloading/updating).</summary>
    Incomplete,
    /// <summary>Install folder or executable no longer exists.</summary>
    Missing,
    /// <summary>Folder exists but could not be read (permissions, offline drive).</summary>
    Inaccessible,
}

/// <summary>Which launch method the user prefers for a game.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LaunchPreference>))]
public enum LaunchPreference
{
    /// <summary>Use the first valid detected method (official launcher first where one is required).</summary>
    Automatic,
    /// <summary>Always go through the official launcher/protocol.</summary>
    OfficialLauncher,
    /// <summary>Run the game executable directly (only valid where the store allows it).</summary>
    DirectExecutable,
}

/// <summary>
/// A game in the persistent library. Fields set by the scanner are refreshed on every scan;
/// fields prefixed "Custom" and the profile are user customisations and are never overwritten by a scan.
/// </summary>
public sealed class Game
{
    /// <summary>Stable identifier, e.g. "steam:730", "epic:Fortnite", "folder:3f2a…".</summary>
    public string Id { get; set; } = "";

    /// <summary>The launcher's own identifier (Steam app id, Epic AppName, GOG id, …).</summary>
    public string SourceId { get; set; } = "";

    public LauncherKind Launcher { get; set; }

    /// <summary>Name reported by the launcher or derived from the folder.</summary>
    public string Name { get; set; } = "";

    public string? CustomName { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName!;

    public string? InstallPath { get; set; }

    /// <summary>Main executable detected by the scanner (may be null for packaged/URI-only titles).</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Executable chosen manually by the user; overrides detection.</summary>
    public string? CustomExecutablePath { get; set; }

    [JsonIgnore]
    public string? EffectiveExecutablePath => string.IsNullOrWhiteSpace(CustomExecutablePath) ? ExecutablePath : CustomExecutablePath;

    /// <summary>Launch methods detected by the scanner, best first.</summary>
    public List<LaunchMethod> LaunchMethods { get; set; } = new();

    public LaunchPreference LaunchPreference { get; set; } = LaunchPreference.Automatic;

    public string? CustomArguments { get; set; }

    public InstallStatus Status { get; set; } = InstallStatus.Unknown;

    public string? StatusDetail { get; set; }

    /// <summary>Cover art found locally (e.g. Steam library cache) or downloaded with the user's permission.</summary>
    public string? CoverImagePath { get; set; }

    public string? CustomCoverPath { get; set; }

    [JsonIgnore]
    public string? EffectiveCoverPath => string.IsNullOrWhiteSpace(CustomCoverPath) ? CoverImagePath : CustomCoverPath;

    public bool IsFavorite { get; set; }

    public List<string> Collections { get; set; } = new();

    public DateTime DateAddedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastScannedUtc { get; set; }

    public DateTime? LastPlayedUtc { get; set; }

    public double TotalPlaytimeSeconds { get; set; }

    public int SessionCount { get; set; }

    public string? Notes { get; set; }

    /// <summary>URI that opens the game's page in its official launcher (allow-listed schemes only).</summary>
    public string? LauncherPageUri { get; set; }

    /// <summary>URI that asks the official launcher to verify game files, where supported.</summary>
    public string? VerifyFilesUri { get; set; }

    public GameProfile Profile { get; set; } = new();

    /// <summary>Extra launcher metadata (catalog ids, product codes, …).</summary>
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string? InstallDrive
    {
        get
        {
            if (string.IsNullOrWhiteSpace(InstallPath)) return null;
            try
            {
                var root = Path.GetPathRoot(InstallPath);
                return string.IsNullOrEmpty(root) ? null : root.TrimEnd('\\', '/');
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    [JsonIgnore]
    public TimeSpan TotalPlaytime => TimeSpan.FromSeconds(TotalPlaytimeSeconds);

    public Game Clone()
    {
        var copy = (Game)MemberwiseClone();
        copy.LaunchMethods = LaunchMethods.Select(m => m.Clone()).ToList();
        copy.Collections = new List<string>(Collections);
        copy.Metadata = new Dictionary<string, string>(Metadata, StringComparer.OrdinalIgnoreCase);
        copy.Profile = Profile.Clone();
        return copy;
    }

    public override string ToString() => $"{DisplayName} ({Launcher})";
}

[JsonConverter(typeof(JsonStringEnumConverter<LaunchMethodKind>))]
public enum LaunchMethodKind
{
    /// <summary>Start an executable with arguments and a working directory.</summary>
    Executable,
    /// <summary>Open an allow-listed launcher protocol URI (steam://, com.epicgames.launcher://, …).</summary>
    Uri,
    /// <summary>Run an official launcher executable with a launch command (Riot Client, Battle.net, GOG Galaxy).</summary>
    LauncherCommand,
    /// <summary>Activate an installed MSIX/Store package by AUMID ("PackageFamilyName!AppId").</summary>
    PackagedApp,
}

public sealed class LaunchMethod
{
    public LaunchMethodKind Kind { get; set; }

    /// <summary>Executable path, URI or AUMID depending on <see cref="Kind"/>.</summary>
    public string Target { get; set; } = "";

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>Human readable description, e.g. "Steam (steam://rungameid/730)".</summary>
    public string Description { get; set; } = "";

    /// <summary>True when this method goes through the official launcher (handles DRM/login/updates).</summary>
    public bool IsOfficialLauncher { get; set; }

    public LaunchMethod Clone() => (LaunchMethod)MemberwiseClone();

    public static LaunchMethod Exe(string path, string? args = null, string? workingDir = null, string? description = null) => new()
    {
        Kind = LaunchMethodKind.Executable,
        Target = path,
        Arguments = args,
        WorkingDirectory = workingDir,
        Description = description ?? "Run executable directly",
    };

    public static LaunchMethod ProtocolUri(string uri, string description) => new()
    {
        Kind = LaunchMethodKind.Uri,
        Target = uri,
        Description = description,
        IsOfficialLauncher = true,
    };

    public static LaunchMethod Launcher(string launcherExe, string args, string description) => new()
    {
        Kind = LaunchMethodKind.LauncherCommand,
        Target = launcherExe,
        Arguments = args,
        Description = description,
        IsOfficialLauncher = true,
    };

    public static LaunchMethod Package(string aumid, string description) => new()
    {
        Kind = LaunchMethodKind.PackagedApp,
        Target = aumid,
        Description = description,
        IsOfficialLauncher = true,
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<OverlayLayout>))]
public enum OverlayLayout
{
    FpsOnly,
    Compact,
    Full,
    Graph,
}

/// <summary>Per-game profile. Only settings the app can safely control live here.</summary>
public sealed class GameProfile
{
    /// <summary>Overlay layout for this game; null = use the global setting.</summary>
    public OverlayLayout? OverlayLayout { get; set; }

    /// <summary>Show the overlay automatically while this game runs (null = global setting).</summary>
    public bool? ShowOverlay { get; set; }

    /// <summary>Start PresentMon frame capture automatically when the game starts.</summary>
    public bool AutoCaptureFps { get; set; } = true;

    /// <summary>Track playtime sessions for this game.</summary>
    public bool TrackSessions { get; set; } = true;

    /// <summary>Keep the raw per-frame CSV from PresentMon after the session (off by default to save disk space).</summary>
    public bool KeepFrameLog { get; set; }

    /// <summary>Metrics to show in the overlay for this game (null = layout defaults).</summary>
    public List<OverlayMetric>? OverlayMetrics { get; set; }

    public string? TroubleshootingNotes { get; set; }

    public GameProfile Clone()
    {
        var copy = (GameProfile)MemberwiseClone();
        copy.OverlayMetrics = OverlayMetrics is null ? null : new List<OverlayMetric>(OverlayMetrics);
        return copy;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<OverlayMetric>))]
public enum OverlayMetric
{
    Fps,
    AverageFps,
    OnePercentLow,
    FrameTime,
    Cpu,
    Gpu,
    Ram,
    GpuTemperature,
    VramUsage,
    SessionTime,
}
