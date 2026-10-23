using System.Xml;
using System.Xml.Linq;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// EA app: EA games register a Windows uninstall entry (publisher "Electronic Arts") and ship
/// __Installer\installerdata.xml describing the title, content ids and runtime executable.
/// Launching the game executable hands control to the EA app, which performs sign-in and entitlement checks.
/// </summary>
public sealed class EaScanner : IGameScanner
{
    public LauncherKind Kind => LauncherKind.EA;
    public string DisplayName => "EA app";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind);
        var eaAppExe = FindEaApp(ctx);
        result.LauncherDetected = eaAppExe is not null;

        foreach (var entry in ScannerHelpers.UninstallEntries(ctx.Registry, p => p is not null && p.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase)))
        {
            ctx.ThrowIfCancelled();
            if (entry.DisplayName.Equals("EA app", StringComparison.OrdinalIgnoreCase) || entry.DisplayName.StartsWith("Origin", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(entry.InstallLocation)) continue;
            var dir = ScannerHelpers.TrimPath(entry.InstallLocation!);
            if (!PathUtil.IsSafeAbsolutePath(dir) || ctx.IsExcluded(dir)) continue;
            ctx.ReportLocation?.Invoke(dir);
            result.LauncherDetected = true;

            var installerData = Path.Combine(dir, "__Installer", "installerdata.xml");
            var info = PathUtil.FileExistsSafe(installerData) ? ReadInstallerData(installerData, dir) : null;

            var sourceId = info?.ContentId ?? PathUtil.StableHash(PathUtil.NormalizeKey(dir));
            var game = ScannerHelpers.NewGame(LauncherKind.EA, sourceId, info?.Title ?? entry.DisplayName, dir);
            if (info?.ContentId is not null) game.Metadata["EaContentId"] = info.ContentId;
            if (info?.ExecutablePath is not null && PathUtil.FileExistsSafe(info.ExecutablePath)) game.ExecutablePath = info.ExecutablePath;

            ScannerHelpers.ApplyInstallStatus(game);
            if (game.Status == InstallStatus.Installed) ScannerHelpers.DetectExecutable(game, ctx);

            var exeMethod = ScannerHelpers.DirectExe(game.ExecutablePath, description: "Run executable (the EA app opens for sign-in)");
            if (exeMethod is not null)
            {
                exeMethod.IsOfficialLauncher = true; // EA titles hand off to the EA app themselves.
                game.LaunchMethods.Add(exeMethod);
            }
            if (info?.ContentId is not null)
            {
                game.LaunchMethods.Add(LaunchMethod.ProtocolUri($"origin2://game/launch?offerIds={Uri.EscapeDataString(info.ContentId)}", "EA app protocol"));
            }
            result.Games.Add(game);
        }

        result.Completed = true;
        return result;
    }

    private static string? FindEaApp(ScanContext ctx)
    {
        var path = ctx.Registry.GetString(RegHive.LocalMachine, @"SOFTWARE\Electronic Arts\EA Desktop", "LauncherAppPath")
                   ?? ctx.Registry.GetString(RegHive.LocalMachine32, @"SOFTWARE\Electronic Arts\EA Desktop", "LauncherAppPath");
        if (!string.IsNullOrWhiteSpace(path) && PathUtil.FileExistsSafe(ScannerHelpers.TrimPath(path))) return ScannerHelpers.TrimPath(path);
        if (!string.IsNullOrEmpty(ctx.Environment.ProgramFiles))
        {
            var fallback = Path.Combine(ctx.Environment.ProgramFiles, "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe");
            if (PathUtil.FileExistsSafe(fallback)) return fallback;
        }
        return null;
    }

    internal sealed record EaInstallInfo(string? Title, string? ContentId, string? ExecutablePath);

    internal static EaInstallInfo? ReadInstallerData(string file, string installDir)
    {
        var text = PathUtil.ReadTextSafe(file, 4 * 1024 * 1024);
        if (text is null) return null;
        try
        {
            // DTD processing disabled: the file is untrusted input.
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var doc = XDocument.Load(reader);
            var root = doc.Root;
            if (root is null) return null;

            var titles = root.Descendants().Where(e => e.Name.LocalName == "gameTitle").ToList();
            var title = titles.FirstOrDefault(t => (string?)t.Attribute("locale") == "en_US")?.Value ?? titles.FirstOrDefault()?.Value;
            var contentId = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "contentID")?.Value?.Trim();

            string? exe = null;
            var filePath = root.Descendants().Where(e => e.Name.LocalName == "filePath").Select(e => e.Value.Trim()).FirstOrDefault(v => v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (filePath is not null)
            {
                // Typically "[HKEY_LOCAL_MACHINE\SOFTWARE\...\Install Dir]Game.exe" — the part after "]" is relative to the install dir.
                var rel = filePath.Contains(']') ? filePath[(filePath.LastIndexOf(']') + 1)..] : filePath;
                rel = rel.Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                exe = Path.IsPathRooted(rel) ? (PathUtil.IsSafeAbsolutePath(rel) ? rel : null) : PathUtil.SafeCombine(installDir, rel);
            }
            return new EaInstallInfo(string.IsNullOrWhiteSpace(title) ? null : title.Trim(), string.IsNullOrWhiteSpace(contentId) ? null : contentId, exe);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
