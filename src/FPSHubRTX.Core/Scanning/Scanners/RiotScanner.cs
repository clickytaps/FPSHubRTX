using System.Text.Json;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning.Scanners;

/// <summary>
/// Riot Client: %ProgramData%\Riot Games\Metadata\{product}.{patchline}\{product}.{patchline}.product_settings.yaml
/// holds each product's install path; RiotClientInstalls.json points at RiotClientServices.exe.
/// Games launch with RiotClientServices.exe --launch-product=… --launch-patchline=… (Vanguard/anti-cheat untouched).
/// </summary>
public sealed class RiotScanner : IGameScanner
{
    private static readonly Dictionary<string, string> ProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["valorant"] = "VALORANT",
        ["league_of_legends"] = "League of Legends",
        ["bacon"] = "Legends of Runeterra",
    };

    public LauncherKind Kind => LauncherKind.Riot;
    public string DisplayName => "Riot Client";

    public ScannerResult Scan(ScanContext ctx)
    {
        var result = new ScannerResult(Kind);
        if (string.IsNullOrEmpty(ctx.Environment.ProgramData))
        {
            result.Completed = true;
            return result;
        }

        var riotData = Path.Combine(ctx.Environment.ProgramData, "Riot Games");
        var clientExe = FindRiotClient(riotData);
        var metadata = Path.Combine(riotData, "Metadata");
        result.LauncherDetected = clientExe is not null || PathUtil.DirectoryExistsSafe(metadata);
        ctx.ReportLocation?.Invoke(metadata);

        foreach (var productDir in PathUtil.EnumerateDirectoriesSafe(metadata))
        {
            ctx.ThrowIfCancelled();
            var folder = Path.GetFileName(productDir); // e.g. "valorant.live"
            var dot = folder.IndexOf('.');
            if (dot <= 0) continue;
            var product = folder[..dot];
            var patchline = folder[(dot + 1)..];
            if (product.Equals("riot_client", StringComparison.OrdinalIgnoreCase)) continue;
            if (!product.All(c => char.IsLetterOrDigit(c) || c == '_') || !patchline.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')) continue;

            var yaml = PathUtil.EnumerateFilesSafe(productDir, "*.product_settings.yaml").FirstOrDefault();
            var settings = yaml is null ? null : ParseSimpleYaml(PathUtil.ReadTextSafe(yaml, 512 * 1024) ?? "");
            var installPath = settings?.GetValueOrDefault("product_install_full_path") ?? settings?.GetValueOrDefault("product_install_root");
            if (string.IsNullOrWhiteSpace(installPath)) continue;
            installPath = ScannerHelpers.TrimPath(installPath);
            if (!PathUtil.IsSafeAbsolutePath(installPath) || ctx.IsExcluded(installPath)) continue;

            var name = ProductNames.TryGetValue(product, out var n) ? n : PrettyName(product);
            if (!patchline.Equals("live", StringComparison.OrdinalIgnoreCase)) name += $" ({patchline.ToUpperInvariant()})";

            var game = ScannerHelpers.NewGame(LauncherKind.Riot, folder, name, installPath);
            game.Metadata["RiotProduct"] = product;
            game.Metadata["RiotPatchline"] = patchline;
            if (clientExe is not null)
            {
                game.LaunchMethods.Add(LaunchMethod.Launcher(clientExe, $"--launch-product={product} --launch-patchline={patchline}", "Riot Client (required — handles sign-in and anti-cheat)"));
            }
            ScannerHelpers.ApplyInstallStatus(game);
            if (game.Status == InstallStatus.Installed) ScannerHelpers.DetectExecutable(game, ctx);
            if (game.LaunchMethods.Count == 0) game.StatusDetail = "Riot Client not found — reinstall the Riot Client to launch this game";
            result.Games.Add(game);
        }

        result.Completed = true;
        return result;
    }

    private static string? FindRiotClient(string riotData)
    {
        var installs = Path.Combine(riotData, "RiotClientInstalls.json");
        var text = PathUtil.ReadTextSafe(installs, 256 * 1024);
        if (text is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var key in new[] { "rc_default", "rc_live" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        var path = ScannerHelpers.TrimPath(v.GetString()!);
                        if (PathUtil.IsSafeAbsolutePath(path) && PathUtil.FileExistsSafe(path)) return path;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    /// <summary>Reads top-level "key: value" pairs from a simple YAML file (quotes stripped).</summary>
    internal static Dictionary<string, string> ParseSimpleYaml(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || char.IsWhiteSpace(line[0])) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
            if (value.Length > 0) map[key] = value;
        }
        return map;
    }

    private static string PrettyName(string product)
        => string.Join(' ', product.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
