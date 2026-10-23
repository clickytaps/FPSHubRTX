using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// Minecraft: the current Minecraft Launcher (Store/MSIX package Microsoft.4297127D64EC6), Minecraft for Windows
/// / Bedrock (Microsoft.MinecraftUWP) and the legacy MSI launcher (Program Files (x86)\Minecraft Launcher).
/// </summary>
public sealed class MinecraftScanner : IGameScanner
{
    internal const string LauncherPackage = "Microsoft.4297127D64EC6";
    internal const string BedrockPackage = "Microsoft.MinecraftUWP";

    public LauncherKind Kind => LauncherKind.Minecraft;
    public string DisplayName => "Minecraft Launcher";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind);
        IReadOnlyList<InstalledPackage> packages;
        try { packages = ctx.Packages.GetPackages(); }
        catch (Exception ex)
        {
            packages = Array.Empty<InstalledPackage>();
            result.Warnings.Add("Could not list Store packages: " + ex.Message);
        }

        AddPackage(result, packages, LauncherPackage, "Minecraft Launcher", "launcher", ctx);
        AddPackage(result, packages, BedrockPackage, "Minecraft for Windows (Bedrock)", "bedrock", ctx);

        // Legacy MSI launcher.
        foreach (var pf in new[] { ctx.Environment.ProgramFilesX86, ctx.Environment.ProgramFiles }.Where(p => !string.IsNullOrEmpty(p)).Distinct())
        {
            var exe = Path.Combine(pf, "Minecraft Launcher", "MinecraftLauncher.exe");
            ctx.ReportLocation?.Invoke(Path.GetDirectoryName(exe)!);
            if (!PathUtil.FileExistsSafe(exe) || result.Games.Any(g => g.SourceId == "launcher")) continue;
            var game = ScannerHelpers.NewGame(LauncherKind.Minecraft, "launcher-legacy", "Minecraft Launcher", Path.GetDirectoryName(exe));
            game.ExecutablePath = exe;
            game.LaunchMethods.Add(LaunchMethod.Exe(exe, null, Path.GetDirectoryName(exe), "Minecraft Launcher"));
            game.LaunchMethods[0].IsOfficialLauncher = true;
            ScannerHelpers.ApplyInstallStatus(game);
            result.Games.Add(game);
            result.LauncherDetected = true;
        }

        result.Completed = true;
        return result;
    }

    private static void AddPackage(ScannerResult result, IReadOnlyList<InstalledPackage> packages, string packageName, string displayName, string sourceId, ScanContext ctx)
    {
        var pkg = packages.FirstOrDefault(p => p.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase));
        if (pkg is null) return;
        result.LauncherDetected = true;
        var location = pkg.InstallLocation is not null && PathUtil.IsSafeAbsolutePath(pkg.InstallLocation) ? pkg.InstallLocation : null;
        var game = ScannerHelpers.NewGame(LauncherKind.Minecraft, sourceId, displayName, location);
        var appId = pkg.AppIds.FirstOrDefault() ?? (sourceId == "bedrock" ? "App" : "Minecraft");
        game.LaunchMethods.Add(LaunchMethod.Package($"{pkg.FamilyName}!{appId}", "Microsoft Store package"));
        game.Metadata["PackageFamilyName"] = pkg.FamilyName;
        game.LauncherPageUri = $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(pkg.FamilyName)}";
        if (location is not null)
        {
            // Store packages live under WindowsApps, which a normal user can't list — that's expected.
            game.Status = InstallStatus.Installed;
        }
        else
        {
            game.Status = InstallStatus.Installed;
            game.StatusDetail = "Store package (install folder managed by Windows)";
        }
        if (sourceId == "bedrock") game.Metadata["ProcessNameHint"] = "Minecraft.Windows.exe";
        if (sourceId == "launcher") game.Metadata["ProcessNameHint"] = "javaw.exe";
        result.Games.Add(game);
    }
}
