using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning;

/// <summary>Everything a scanner needs. Scanners must only READ — they never modify game files.</summary>
public sealed class ScanContext
{
    public required ScanEnvironment Environment { get; init; }
    public required IRegistryReader Registry { get; init; }
    public required IDriveProvider Drives { get; init; }
    public IPackageCatalog Packages { get; init; } = EmptyPackageCatalog.Instance;
    public required ScanSettings Settings { get; init; }
    public ILog Log { get; init; } = NullLog.Instance;
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Optional callback a scanner uses to report the folder it is looking at.</summary>
    public Action<string>? ReportLocation { get; init; }

    private IReadOnlyList<DriveDescriptor>? _drives;

    /// <summary>Ready drives that the scan scope allows (fixed always; removable only when enabled).</summary>
    public IReadOnlyList<DriveDescriptor> ScanDrives
    {
        get
        {
            _drives ??= Drives.GetDrives()
                .Where(d => d.IsReady && (d.Kind == DriveKind.Fixed || (d.Kind == DriveKind.Removable && Settings.IncludeRemovableDrives)))
                .ToList();
            return _drives;
        }
    }

    /// <summary>True when the path is inside one of the user's excluded directories.</summary>
    public bool IsExcluded(string? path)
        => !string.IsNullOrWhiteSpace(path) && Settings.ExcludedDirectories.Any(x => !string.IsNullOrWhiteSpace(x) && PathUtil.IsUnder(path!, x));

    public void ThrowIfCancelled() => CancellationToken.ThrowIfCancellationRequested();
}

public interface IGameScanner
{
    LauncherKind Kind { get; }
    string DisplayName { get; }
    ScannerResult Scan(ScanContext context);
}

public sealed class ScannerResult
{
    public ScannerResult(LauncherKind kind) => Kind = kind;

    public LauncherKind Kind { get; }

    /// <summary>True when the launcher itself (or its data) was found on this PC.</summary>
    public bool LauncherDetected { get; set; }

    /// <summary>True when the scanner ran to the end without a fatal error (used to decide "missing" games).</summary>
    public bool Completed { get; set; }

    public List<Game> Games { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
}

public enum ScanStage
{
    Starting,
    Scanning,
    Validating,
    Completed,
    Cancelled,
}

public sealed record ScanProgress(
    ScanStage Stage,
    string CurrentSource,
    string? CurrentLocation,
    int StepIndex,
    int StepCount,
    int GamesFound)
{
    /// <summary>Fraction complete in [0,1], measured as completed scanner steps.</summary>
    public double Fraction => StepCount <= 0 ? 0 : Math.Clamp((double)StepIndex / StepCount, 0, 1);
}

public sealed class ScanSummary
{
    public List<Game> Games { get; } = new();
    public List<ScannerResult> SourceResults { get; } = new();
    public bool Cancelled { get; set; }
    public TimeSpan Duration { get; set; }
    public int DuplicatesRemoved { get; set; }

    public IEnumerable<LauncherKind> CompletedSources => SourceResults.Where(r => r.Completed).Select(r => r.Kind);
}
