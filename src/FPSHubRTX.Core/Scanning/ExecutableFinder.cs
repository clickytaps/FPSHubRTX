using System.Text.RegularExpressions;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Scanning;

/// <summary>
/// Picks the most likely main game executable inside an install folder using simple, explainable
/// heuristics (name similarity, engine layout, size, depth) and an exclusion list for installers,
/// crash reporters, redistributables and anti-cheat services. Users can always override the choice.
/// </summary>
public static partial class ExecutableFinder
{
    private static readonly string[] ExcludedNameFragments =
    {
        "unins", "uninstall", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "dxwebsetup",
        "directx", "crash", "reporter", "errorreport", "bugreport", "helper", "updater", "update", "patcher",
        "prereq", "easyanticheat", "anticheat", "battleye", "beservice", "be_service", "eac_", "eaclauncher",
        "dotnet", "ndp4", "cefprocess", "cefsharp", "qtwebengine", "webhelper", "unitycrashhandler", "crashpad",
        "touchup", "cleanup", "physx", "oalinst", "steamerrorreporter", "steamservice", "vrserver", "nvidia",
        "dxsetup", "dotnetfx", "xnafx", "openal", "register", "activation", "repair", "benchmarktool", "config",
        "settings", "editor", "modkit", "dedicated", "server", "rungame", "uplaywebcore", "ue4prereq", "ue5prereq",
    };

    private static readonly string[] ExcludedDirectoryFragments =
    {
        "_commonredist", "commonredist", "redist", "redistributables", "__installer", "directx", "dotnet",
        "vcredist", "support", "prerequisites", "_prereq", "easyanticheat", "battleye", "thirdparty",
        "crashreportclient", "installers", "uninstall", "tools", "sdk", "documentation",
    };

    /// <summary>Returns the best candidate executable or null when nothing plausible exists.</summary>
    public static string? FindMainExecutable(string installDir, string? gameName, int maxDepth = 4, CancellationToken ct = default)
    {
        var candidates = FindCandidates(installDir, gameName, maxDepth, ct);
        return candidates.Count == 0 ? null : candidates[0].Path;
    }

    public sealed record Candidate(string Path, double Score, long SizeBytes, int Depth);

    public static List<Candidate> FindCandidates(string installDir, string? gameName, int maxDepth = 4, CancellationToken ct = default)
    {
        var results = new List<Candidate>();
        if (!PathUtil.DirectoryExistsSafe(installDir)) return results;

        var folderName = Path.GetFileName(installDir.TrimEnd('\\', '/'));
        var nameKeys = new[] { Simplify(gameName), Simplify(folderName) }.Where(k => k.Length >= 2).Distinct().ToArray();
        var visited = 0;

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth || visited > 5000) return;
            ct.ThrowIfCancellationRequested();
            visited++;

            foreach (var exe in PathUtil.EnumerateFilesSafe(dir, "*.exe"))
            {
                var score = ScoreExecutable(exe, installDir, nameKeys, depth, out var size);
                if (score > double.MinValue) results.Add(new Candidate(exe, score, size, depth));
            }

            foreach (var sub in PathUtil.EnumerateDirectoriesSafe(dir))
            {
                var subName = Path.GetFileName(sub).ToLowerInvariant();
                if (ExcludedDirectoryFragments.Any(f => subName == f || subName.StartsWith(f, StringComparison.Ordinal))) continue;
                Walk(sub, depth + 1);
            }
        }

        Walk(installDir, 0);
        return results.OrderByDescending(c => c.Score).ThenBy(c => c.Depth).ToList();
    }

    public static bool IsExcludedExecutableName(string fileName)
    {
        var lower = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return ExcludedNameFragments.Any(f => lower.Contains(f, StringComparison.Ordinal));
    }

    private static double ScoreExecutable(string exePath, string installDir, string[] nameKeys, int depth, out long size)
    {
        size = 0;
        var fileName = Path.GetFileNameWithoutExtension(exePath);
        if (IsExcludedExecutableName(fileName))
        {
            // "Launcher" executables are excluded unless they are the only option (scored very low below).
            return double.MinValue;
        }

        try
        {
            size = new FileInfo(exePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            size = 0;
        }

        double score = 0;
        var simple = Simplify(fileName);

        foreach (var key in nameKeys)
        {
            if (simple == key) score += 60;
            else if (simple.Length >= 3 && (key.Contains(simple, StringComparison.Ordinal) || simple.Contains(key, StringComparison.Ordinal))) score += 35;
        }

        var lowerPath = exePath.Replace('/', '\\').ToLowerInvariant();
        if (lowerPath.Contains(@"\binaries\win64\", StringComparison.Ordinal)) score += 15;
        if (fileName.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) score += 25;
        if (lowerPath.Contains(@"\bin\x64\", StringComparison.Ordinal) || lowerPath.Contains(@"\bin64\", StringComparison.Ordinal) || lowerPath.Contains(@"\x64\", StringComparison.Ordinal)) score += 8;
        if (lowerPath.Contains(@"\engine\binaries\", StringComparison.Ordinal)) score -= 40; // UE engine helpers
        if (fileName.Contains("launcher", StringComparison.OrdinalIgnoreCase)) score -= 20;
        if (fileName.Contains("x86", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith("32", StringComparison.Ordinal)) score -= 5;

        // A Unity game has "<Name>_Data" next to "<Name>.exe".
        var dir = Path.GetDirectoryName(exePath)!;
        if (PathUtil.DirectoryExistsSafe(Path.Combine(dir, fileName + "_Data"))) score += 40;

        // Larger executables are more likely to be the game; log scale keeps this a tie-breaker.
        if (size > 0) score += Math.Min(20, Math.Log2(Math.Max(1, size / 65536.0)) * 2);
        score -= depth * 4;
        return score;
    }

    /// <summary>Lower-case alphanumerics only ("Counter-Strike 2" → "counterstrike2").</summary>
    public static string Simplify(string? text)
        => string.IsNullOrWhiteSpace(text) ? "" : NonAlnum().Replace(text.ToLowerInvariant(), "");

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    /// <summary>Signals that a folder contains a game (engine files, store markers).</summary>
    public static bool LooksLikeGameFolder(string dir)
    {
        string[] markerFiles =
        {
            "UnityPlayer.dll", "GameAssembly.dll", "steam_api.dll", "steam_api64.dll", "galaxy.dll", "galaxy64.dll",
            "EOSSDK-Win64-Shipping.dll", "bink2w64.dll", "binkw32.dll", "fmod.dll", "fmodex.dll", "d3dx9_43.dll",
            "uplay_r1_loader64.dll", "nvngx_dlss.dll", "amd_fidelityfx_dx12.dll", "libxess.dll", "MicrosoftGame.config",
        };
        foreach (var m in markerFiles)
        {
            if (PathUtil.FileExistsSafe(Path.Combine(dir, m))) return true;
        }
        if (PathUtil.EnumerateFilesSafe(dir, "goggame-*.info").Any()) return true;
        if (PathUtil.EnumerateFilesSafe(dir, "*.pak").Any()) return true;
        if (PathUtil.DirectoryExistsSafe(Path.Combine(dir, "Engine", "Binaries"))) return true;
        if (PathUtil.EnumerateDirectoriesSafe(dir).Any(d => d.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }
}
