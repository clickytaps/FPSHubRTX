using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// Ubisoft Connect: HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\{id}\InstallDir lists installs.
/// Names come from the matching "Uplay Install {id}" uninstall entry. Launch via uplay://launch/{id}/0.
/// </summary>
public sealed class UbisoftScanner : IGameScanner
{
    private const string InstallsKey = @"SOFTWARE\Ubisoft\Launcher\Installs";

    public LauncherKind Kind => LauncherKind.Ubisoft;
    public string DisplayName => "Ubisoft Connect";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind);
        var launcherDir = ctx.Registry.GetString(RegHive.LocalMachine32, @"SOFTWARE\Ubisoft\Launcher", "InstallDir");
        var ids = ctx.Registry.GetSubKeyNames(RegHive.LocalMachine32, InstallsKey);
        result.LauncherDetected = !string.IsNullOrWhiteSpace(launcherDir) || ids.Count > 0;

        foreach (var id in ids)
        {
            ctx.ThrowIfCancelled();
            if (!id.All(char.IsDigit)) continue;
            var dir = ctx.Registry.GetString(RegHive.LocalMachine32, InstallsKey + "\\" + id, "InstallDir");
            if (string.IsNullOrWhiteSpace(dir)) continue;
            dir = ScannerHelpers.TrimPath(dir);
            if (!PathUtil.IsSafeAbsolutePath(dir) || ctx.IsExcluded(dir)) continue;
            ctx.ReportLocation?.Invoke(dir);

            var name = ctx.Registry.GetString(RegHive.LocalMachine32, $@"{ScannerHelpers.UninstallKey}\Uplay Install {id}", "DisplayName")
                       ?? ctx.Registry.GetString(RegHive.LocalMachine, $@"{ScannerHelpers.UninstallKey}\Uplay Install {id}", "DisplayName")
                       ?? Path.GetFileName(dir);

            var game = ScannerHelpers.NewGame(LauncherKind.Ubisoft, id, name, dir);
            game.Metadata["UbisoftId"] = id;
            game.LaunchMethods.Add(LaunchMethod.ProtocolUri($"uplay://launch/{id}/0", "Ubisoft Connect (required for online checks)"));
            game.LauncherPageUri = "uplay://";

            ScannerHelpers.ApplyInstallStatus(game);
            if (game.Status == InstallStatus.Installed) ScannerHelpers.DetectExecutable(game, ctx);
            result.Games.Add(game);
        }

        result.Completed = true;
        return result;
    }
}
