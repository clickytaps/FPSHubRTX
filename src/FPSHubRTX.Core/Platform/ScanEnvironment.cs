namespace FPSHubRTX.Core.Platform;

/// <summary>
/// Well-known folders the scanners look in. Production code uses <see cref="FromSystem"/>;
/// tests point these at temporary fixture folders.
/// </summary>
public sealed class ScanEnvironment
{
    public string ProgramData { get; init; } = "";
    public string ProgramFiles { get; init; } = "";
    public string ProgramFilesX86 { get; init; } = "";
    public string LocalAppData { get; init; } = "";
    public string RoamingAppData { get; init; } = "";

    public static ScanEnvironment FromSystem()
    {
        static string Get(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); } catch { return ""; }
        }

        var pf = Get(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Get(Environment.SpecialFolder.ProgramFilesX86);
        return new ScanEnvironment
        {
            ProgramData = Get(Environment.SpecialFolder.CommonApplicationData),
            ProgramFiles = pf,
            ProgramFilesX86 = string.IsNullOrEmpty(pf86) ? pf : pf86,
            LocalAppData = Get(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = Get(Environment.SpecialFolder.ApplicationData),
        };
    }
}

public enum DriveKind
{
    Fixed,
    Removable,
    Network,
    Other,
}

public sealed record DriveDescriptor(string RootPath, DriveKind Kind, bool IsReady, string? Label = null, long? FreeBytes = null, long? TotalBytes = null);

public interface IDriveProvider
{
    IReadOnlyList<DriveDescriptor> GetDrives();
}

public sealed class SystemDriveProvider : IDriveProvider
{
    public IReadOnlyList<DriveDescriptor> GetDrives()
    {
        var list = new List<DriveDescriptor>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return list;
        }

        foreach (var d in drives)
        {
            try
            {
                var kind = d.DriveType switch
                {
                    DriveType.Fixed => DriveKind.Fixed,
                    DriveType.Removable => DriveKind.Removable,
                    DriveType.Network => DriveKind.Network,
                    _ => DriveKind.Other,
                };
                // On Linux DriveInfo lists many pseudo file systems; keep only real-looking roots there.
                if (!OperatingSystem.IsWindows() && kind == DriveKind.Other) continue;

                var ready = d.IsReady; // Missing/disconnected drives report false instead of throwing.
                list.Add(new DriveDescriptor(
                    d.RootDirectory.FullName,
                    kind,
                    ready,
                    ready ? SafeLabel(d) : null,
                    ready ? d.AvailableFreeSpace : null,
                    ready ? d.TotalSize : null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that disappears mid-enumeration is simply skipped.
            }
        }
        return list;
    }

    private static string? SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; } catch { return null; }
    }
}

public sealed class FixedDriveProvider : IDriveProvider
{
    private readonly IReadOnlyList<DriveDescriptor> _drives;
    public FixedDriveProvider(params DriveDescriptor[] drives) => _drives = drives;
    public IReadOnlyList<DriveDescriptor> GetDrives() => _drives;
}

/// <summary>An installed MSIX/Store package as reported by Windows' PackageManager.</summary>
public sealed record InstalledPackage(string Name, string FamilyName, string? InstallLocation, string? DisplayName, IReadOnlyList<string> AppIds);

/// <summary>Lists installed Store/MSIX packages. Implemented in the WPF app with the WinRT PackageManager API.</summary>
public interface IPackageCatalog
{
    IReadOnlyList<InstalledPackage> GetPackages();
}

public sealed class EmptyPackageCatalog : IPackageCatalog
{
    public static readonly EmptyPackageCatalog Instance = new();
    public IReadOnlyList<InstalledPackage> GetPackages() => Array.Empty<InstalledPackage>();
}

public sealed class FixedPackageCatalog : IPackageCatalog
{
    private readonly IReadOnlyList<InstalledPackage> _packages;
    public FixedPackageCatalog(params InstalledPackage[] packages) => _packages = packages;
    public IReadOnlyList<InstalledPackage> GetPackages() => _packages;
}
