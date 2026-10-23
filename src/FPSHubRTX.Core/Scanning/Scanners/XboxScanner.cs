using System.Text;
using System.Xml;
using System.Xml.Linq;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// Xbox app / Microsoft Store PC games (GDK titles). The Xbox app writes a ".GamingRoot" file at the root
/// of each drive listing its game folders (usually "XboxGames"). Each game folder contains
/// Content\MicrosoftGame.config with the package identity and executables. Games are launched by
/// activating the installed package (shell:AppsFolder\PFN!AppId) so Windows handles licensing.
/// </summary>
public sealed class XboxScanner : IGameScanner
{
    public LauncherKind Kind => LauncherKind.Xbox;
    public string DisplayName => "Xbox app / Microsoft Store";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind);
        IReadOnlyList<InstalledPackage> packages;
        try
        {
            packages = ctx.Packages.GetPackages();
        }
        catch (Exception ex)
        {
            packages = Array.Empty<InstalledPackage>();
            result.Warnings.Add("Could not list installed Store packages: " + ex.Message);
        }

        foreach (var root in GetGamingRoots(ctx))
        {
            ctx.ThrowIfCancelled();
            if (ctx.IsExcluded(root)) continue;
            ctx.ReportLocation?.Invoke(root);
            result.LauncherDetected = true;

            foreach (var gameDir in PathUtil.EnumerateDirectoriesSafe(root))
            {
                ctx.ThrowIfCancelled();
                if (ctx.IsExcluded(gameDir)) continue;
                var config = new[] { Path.Combine(gameDir, "Content", "MicrosoftGame.config"), Path.Combine(gameDir, "MicrosoftGame.config") }
                    .FirstOrDefault(PathUtil.FileExistsSafe);
                if (config is null) continue;

                var info = ReadMicrosoftGameConfig(config);
                if (info is null)
                {
                    result.Warnings.Add($"Unreadable MicrosoftGame.config in {Path.GetFileName(gameDir)}");
                    continue;
                }

                var contentDir = Path.GetDirectoryName(config)!;
                var name = info.DisplayName is { Length: > 0 } dn && !dn.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? dn : Path.GetFileName(gameDir);
                var game = ScannerHelpers.NewGame(LauncherKind.Xbox, info.IdentityName ?? PathUtil.StableHash(PathUtil.NormalizeKey(gameDir)), name, gameDir);
                if (info.IdentityName is not null) game.Metadata["PackageName"] = info.IdentityName;

                var package = info.IdentityName is null ? null : packages.FirstOrDefault(p => p.Name.Equals(info.IdentityName, StringComparison.OrdinalIgnoreCase));
                if (package is not null)
                {
                    var appId = info.Executables.FirstOrDefault().Id ?? package.AppIds.FirstOrDefault() ?? "Game";
                    game.Metadata["PackageFamilyName"] = package.FamilyName;
                    game.LaunchMethods.Add(LaunchMethod.Package($"{package.FamilyName}!{appId}", "Xbox app / Microsoft Store (handles licensing)"));
                    game.LauncherPageUri = $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(package.FamilyName)}";
                }

                var exeName = info.Executables.FirstOrDefault().Name;
                if (!string.IsNullOrWhiteSpace(exeName))
                {
                    var exe = PathUtil.SafeCombine(contentDir, exeName!);
                    if (exe is not null && PathUtil.FileExistsSafe(exe)) game.ExecutablePath = exe;
                }
                if (info.LogoRelative is not null)
                {
                    var logo = PathUtil.SafeCombine(contentDir, info.LogoRelative);
                    if (logo is not null && PathUtil.FileExistsSafe(logo)) game.CoverImagePath = logo;
                }

                ScannerHelpers.ApplyInstallStatus(game);
                if (package is null)
                {
                    game.StatusDetail = "Package registration not found — launch it once from the Xbox app";
                    var direct = ScannerHelpers.DirectExe(game.ExecutablePath, description: "Run executable directly (may fail for protected Store games)");
                    if (direct is not null) game.LaunchMethods.Add(direct);
                }
                result.Games.Add(game);
            }
        }

        result.Completed = true;
        return result;
    }

    internal static IEnumerable<string> GetGamingRoots(ScanContext ctx)
    {
        var roots = new List<string>();
        foreach (var drive in ctx.ScanDrives)
        {
            var marker = Path.Combine(drive.RootPath, ".GamingRoot");
            foreach (var rel in ReadGamingRootFile(marker))
            {
                var full = Path.IsPathRooted(rel) && !rel.StartsWith('\\') ? rel : Path.Combine(drive.RootPath, rel.TrimStart('\\', '/'));
                if (PathUtil.DirectoryExistsSafe(full) && !roots.Any(r => PathUtil.NormalizeKey(r) == PathUtil.NormalizeKey(full))) roots.Add(full);
            }
            if (ctx.Settings.ScanCommonGameFolders)
            {
                var xbox = Path.Combine(drive.RootPath, "XboxGames");
                if (PathUtil.DirectoryExistsSafe(xbox) && !roots.Any(r => PathUtil.NormalizeKey(r) == PathUtil.NormalizeKey(xbox))) roots.Add(xbox);
            }
        }
        return roots;
    }

    /// <summary>.GamingRoot = "RGBX" magic, a 32-bit count, then NUL-terminated UTF-16 folder paths.</summary>
    internal static IReadOnlyList<string> ReadGamingRootFile(string path)
    {
        try
        {
            if (!PathUtil.FileExistsSafe(path)) return Array.Empty<string>();
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 10 || bytes.Length > 64 * 1024) return Array.Empty<string>();
            if (Encoding.ASCII.GetString(bytes, 0, 4) != "RGBX") return Array.Empty<string>();
            var text = Encoding.Unicode.GetString(bytes, 8, (bytes.Length - 8) & ~1);
            return text.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0 && !s.Any(char.IsControl))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    internal sealed record GameConfigInfo(string? IdentityName, string? DisplayName, IReadOnlyList<(string? Name, string? Id)> Executables, string? LogoRelative);

    internal static GameConfigInfo? ReadMicrosoftGameConfig(string path)
    {
        var text = PathUtil.ReadTextSafe(path, 2 * 1024 * 1024);
        if (text is null) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var doc = XDocument.Load(reader);
            var root = doc.Root;
            if (root is null) return null;
            XElement? El(string local) => root.Descendants().FirstOrDefault(e => e.Name.LocalName == local);

            var identity = El("Identity");
            var visuals = El("ShellVisuals");
            var exes = root.Descendants().Where(e => e.Name.LocalName == "Executable")
                .Select(e => ((string?)e.Attribute("Name"), (string?)e.Attribute("Id")))
                .ToList();
            var logo = (string?)visuals?.Attribute("Square480x480Logo") ?? (string?)visuals?.Attribute("Square150x150Logo") ?? (string?)visuals?.Attribute("StoreLogo");
            return new GameConfigInfo((string?)identity?.Attribute("Name"), (string?)visuals?.Attribute("DefaultDisplayName"), exes, logo);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
