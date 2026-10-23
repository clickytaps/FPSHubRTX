using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// Battle.net: Blizzard titles register Windows uninstall entries (publisher "Blizzard Entertainment").
/// Launching goes through Battle.net.exe --exec="launch CODE" for titles whose product code is known;
/// other titles are launched via their executable, which hands off to Battle.net for sign-in.
/// </summary>
public sealed class BattleNetScanner : IGameScanner
{
    // Display-name prefix → Battle.net product code. Only well-established codes are listed.
    private static readonly (string Prefix, string Code)[] ProductCodes =
    {
        ("World of Warcraft Classic", "WoWC"),
        ("World of Warcraft", "WoW"),
        ("Overwatch", "Pro"),
        ("Diablo IV", "Fen"),
        ("Diablo III", "D3"),
        ("Diablo II: Resurrected", "OSI"),
        ("Diablo Immortal", "ANBS"),
        ("Hearthstone", "WTCG"),
        ("Heroes of the Storm", "Hero"),
        ("StarCraft II", "S2"),
        ("StarCraft", "S1"),
        ("Warcraft III", "W3"),
    };

    public LauncherKind Kind => LauncherKind.BattleNet;
    public string DisplayName => "Battle.net";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind);
        var bnetExe = FindBattleNet(ctx);
        result.LauncherDetected = bnetExe is not null;

        foreach (var entry in ScannerHelpers.UninstallEntries(ctx.Registry, p => p is not null && p.Contains("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase)))
        {
            ctx.ThrowIfCancelled();
            if (entry.DisplayName.StartsWith("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(entry.InstallLocation)) continue;
            var dir = ScannerHelpers.TrimPath(entry.InstallLocation!);
            if (!PathUtil.IsSafeAbsolutePath(dir) || ctx.IsExcluded(dir)) continue;
            ctx.ReportLocation?.Invoke(dir);
            result.LauncherDetected = true;

            var code = ProductCode(entry.DisplayName);
            var game = ScannerHelpers.NewGame(LauncherKind.BattleNet, code ?? entry.Key, entry.DisplayName, dir);
            if (code is not null) game.Metadata["BattleNetProductCode"] = code;

            ScannerHelpers.ApplyInstallStatus(game);
            if (game.Status == InstallStatus.Installed) ScannerHelpers.DetectExecutable(game, ctx);

            if (bnetExe is not null && code is not null)
            {
                game.LaunchMethods.Add(LaunchMethod.Launcher(bnetExe, $"--exec=\"launch {code}\"", "Battle.net"));
                game.LauncherPageUri = $"battlenet://{code}";
            }
            var exe = ScannerHelpers.DirectExe(game.ExecutablePath, description: "Run executable (Battle.net opens for sign-in)");
            if (exe is not null)
            {
                exe.IsOfficialLauncher = true;
                game.LaunchMethods.Add(exe);
            }
            result.Games.Add(game);
        }

        result.Completed = true;
        return result;
    }

    internal static string? ProductCode(string displayName)
        => ProductCodes.FirstOrDefault(p => displayName.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).Code;

    private static string? FindBattleNet(ScanContext ctx)
    {
        var candidates = new List<string>();
        var installPath = ctx.Registry.GetString(RegHive.LocalMachine32, $@"{ScannerHelpers.UninstallKey}\Battle.net", "InstallLocation");
        if (!string.IsNullOrWhiteSpace(installPath)) candidates.Add(Path.Combine(ScannerHelpers.TrimPath(installPath), "Battle.net.exe"));
        if (!string.IsNullOrEmpty(ctx.Environment.ProgramFilesX86)) candidates.Add(Path.Combine(ctx.Environment.ProgramFilesX86, "Battle.net", "Battle.net.exe"));
        return candidates.FirstOrDefault(PathUtil.FileExistsSafe);
    }
}
