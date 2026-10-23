using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// Roblox: the website installer puts versioned folders in %LOCALAPPDATA%\Roblox\Versions\version-*\
/// containing RobloxPlayerBeta.exe (older installs used Program Files (x86)). Running the player with no
/// experience link opens the Roblox app. The Microsoft Store build is detected as a package.
/// </summary>
public sealed class RobloxScanner : IGameScanner
{
    internal const string StorePackage = "ROBLOXCORPORATION.ROBLOX";

    public LauncherKind Kind => LauncherKind.Roblox;
    public string DisplayName => "Roblox";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind);
        var roots = new[] { ctx.Environment.LocalAppData, ctx.Environment.ProgramFilesX86 }
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => Path.Combine(r, "Roblox", "Versions"))
            .Distinct()
            .ToList();

        string? newestExe = null;
        DateTime newest = DateTime.MinValue;
        foreach (var root in roots)
        {
            ctx.ThrowIfCancelled();
            ctx.ReportLocation?.Invoke(root);
            foreach (var versionDir in PathUtil.EnumerateDirectoriesSafe(root))
            {
                var exe = Path.Combine(versionDir, "RobloxPlayerBeta.exe");
                if (!PathUtil.FileExistsSafe(exe)) continue;
                DateTime written;
                try { written = File.GetLastWriteTimeUtc(exe); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                if (written > newest)
                {
                    newest = written;
                    newestExe = exe;
                }
            }
        }

        if (newestExe is not null)
        {
            result.LauncherDetected = true;
            var installRoot = Path.GetDirectoryName(Path.GetDirectoryName(newestExe)!)!; // ...\Roblox\Versions
            var game = ScannerHelpers.NewGame(LauncherKind.Roblox, "player", "Roblox", installRoot);
            game.ExecutablePath = newestExe;
            var method = LaunchMethod.Exe(newestExe, null, Path.GetDirectoryName(newestExe), "Roblox app");
            method.IsOfficialLauncher = true;
            game.LaunchMethods.Add(method);
            game.Metadata["ProcessNameHint"] = "RobloxPlayerBeta.exe";
            ScannerHelpers.ApplyInstallStatus(game);
            result.Games.Add(game);
        }

        IReadOnlyList<InstalledPackage> packages;
        try { packages = ctx.Packages.GetPackages(); }
        catch (Exception) { packages = Array.Empty<InstalledPackage>(); }
        var pkg = packages.FirstOrDefault(p => p.Name.Equals(StorePackage, StringComparison.OrdinalIgnoreCase));
        if (pkg is not null)
        {
            result.LauncherDetected = true;
            var game = ScannerHelpers.NewGame(LauncherKind.Roblox, "store", "Roblox (Microsoft Store)", pkg.InstallLocation);
            game.LaunchMethods.Add(LaunchMethod.Package($"{pkg.FamilyName}!{pkg.AppIds.FirstOrDefault() ?? "App"}", "Microsoft Store package"));
            game.Metadata["PackageFamilyName"] = pkg.FamilyName;
            game.Metadata["ProcessNameHint"] = "Windows10Universal.exe";
            game.Status = InstallStatus.Installed;
            game.StatusDetail = "Store package (install folder managed by Windows)";
            result.Games.Add(game);
        }

        result.Completed = true;
        return result;
    }
}
