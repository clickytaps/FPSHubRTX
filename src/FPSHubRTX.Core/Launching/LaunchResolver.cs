using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Launching;

/// <summary>A fully resolved, validated way to start a game.</summary>
public sealed class LaunchPlan
{
    public required LaunchMethodKind Kind { get; init; }
    /// <summary>File passed to the OS: an exe path, a protocol URI, or "explorer.exe" for packaged apps.</summary>
    public required string FileName { get; init; }
    public string Arguments { get; init; } = "";
    public string? WorkingDirectory { get; init; }
    /// <summary>URIs and packaged apps go through the shell; executables are started directly.</summary>
    public bool UseShellExecute { get; init; }
    public required string Description { get; init; }
    public bool ViaOfficialLauncher { get; init; }
    public List<string> Warnings { get; } = new();

    /// <summary>How long to wait for the game process to appear (launchers may need to update/sign in first).</summary>
    public TimeSpan DetectionTimeout { get; init; } = TimeSpan.FromSeconds(45);
}

public sealed class LaunchResolution
{
    public LaunchPlan? Plan { get; init; }
    public string? Error { get; init; }
    public bool Success => Plan is not null;

    public static LaunchResolution Fail(string error) => new() { Error = error };
    public static LaunchResolution Ok(LaunchPlan plan) => new() { Plan = plan };
}

/// <summary>
/// Turns a library entry into a launch plan according to the user's preference, validating every
/// path and URI first (all scanner data is untrusted). Never bypasses a launcher: if the store requires
/// its launcher, the official method is used.
/// </summary>
public static class LaunchResolver
{
    /// <summary>Only these protocol schemes may be opened. Prevents a tampered library file from opening arbitrary handlers.</summary>
    public static readonly IReadOnlySet<string> AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "com.epicgames.launcher", "uplay", "goggalaxy", "origin2", "link2ea", "battlenet",
        "roblox", "roblox-player", "minecraft", "ms-windows-store", "xbox", "ms-xbl-",
    };

    public static LaunchResolution Resolve(Game game, Func<string, bool>? fileExists = null)
    {
        fileExists ??= PathUtil.FileExistsSafe;

        if (game.Status == InstallStatus.Missing && game.LaunchMethods.All(m => m.Kind == LaunchMethodKind.Executable) && string.IsNullOrWhiteSpace(game.CustomExecutablePath))
        {
            return LaunchResolution.Fail("This game's install folder or executable is missing. Use 'Locate installation' if you moved it, or rescan.");
        }

        var candidates = OrderedCandidates(game).ToList();
        if (candidates.Count == 0)
        {
            return LaunchResolution.Fail("No launch method is known for this game. Choose its executable manually from the game's settings.");
        }

        var errors = new List<string>();
        foreach (var method in candidates)
        {
            var plan = TryBuild(game, method, fileExists, out var error);
            if (plan is not null) return LaunchResolution.Ok(plan);
            if (error is not null) errors.Add(error);
        }
        return LaunchResolution.Fail(string.Join(" ", errors.Distinct()));
    }

    private static IEnumerable<LaunchMethod> OrderedCandidates(Game game)
    {
        LaunchMethod? custom = null;
        if (!string.IsNullOrWhiteSpace(game.CustomExecutablePath))
        {
            custom = LaunchMethod.Exe(game.CustomExecutablePath!, null, Path.GetDirectoryName(game.CustomExecutablePath), "Custom executable");
        }

        var detected = game.LaunchMethods;
        switch (game.LaunchPreference)
        {
            case LaunchPreference.OfficialLauncher:
                foreach (var m in detected.Where(m => m.IsOfficialLauncher)) yield return m;
                // Standalone/manual games have no launcher: fall back to what we have.
                if (!detected.Any(m => m.IsOfficialLauncher))
                {
                    if (custom is not null) yield return custom;
                    foreach (var m in detected) yield return m;
                }
                break;

            case LaunchPreference.DirectExecutable:
                if (custom is not null) yield return custom;
                foreach (var m in detected.Where(m => m.Kind == LaunchMethodKind.Executable)) yield return m;
                if (custom is null && detected.All(m => m.Kind != LaunchMethodKind.Executable) && !string.IsNullOrWhiteSpace(game.ExecutablePath))
                    yield return LaunchMethod.Exe(game.ExecutablePath!, null, Path.GetDirectoryName(game.ExecutablePath), "Detected executable");
                break;

            default:
                // A custom exe is an explicit user choice, so it wins in Automatic mode too.
                if (custom is not null) yield return custom;
                foreach (var m in detected) yield return m;
                break;
        }
    }

    private static LaunchPlan? TryBuild(Game game, LaunchMethod method, Func<string, bool> fileExists, out string? error)
    {
        error = null;
        var customArgs = string.IsNullOrWhiteSpace(game.CustomArguments) ? null : game.CustomArguments!.Trim();
        if (customArgs is not null && customArgs.Any(c => c is '\r' or '\n' or '\0'))
        {
            error = "Launch arguments contain invalid characters.";
            return null;
        }

        switch (method.Kind)
        {
            case LaunchMethodKind.Executable:
            case LaunchMethodKind.LauncherCommand:
            {
                if (!PathUtil.IsSafeAbsolutePath(method.Target) || !method.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Invalid executable path: {method.Target}";
                    return null;
                }
                if (!fileExists(method.Target))
                {
                    error = method.Kind == LaunchMethodKind.LauncherCommand
                        ? $"The launcher was not found at {method.Target}. Reinstall it or rescan."
                        : $"Executable not found: {method.Target}";
                    return null;
                }
                var workingDir = method.WorkingDirectory;
                if (string.IsNullOrWhiteSpace(workingDir) || !PathUtil.IsSafeAbsolutePath(workingDir)) workingDir = Path.GetDirectoryName(method.Target);
                var args = JoinArgs(method.Arguments, customArgs);
                return new LaunchPlan
                {
                    Kind = method.Kind,
                    FileName = method.Target,
                    Arguments = args,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    Description = method.Description,
                    ViaOfficialLauncher = method.IsOfficialLauncher,
                    DetectionTimeout = method.IsOfficialLauncher ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(45),
                };
            }

            case LaunchMethodKind.Uri:
            {
                if (!IsAllowedUri(method.Target, out var scheme))
                {
                    error = $"Launch link uses a scheme that isn't allowed ({scheme}).";
                    return null;
                }
                var uri = method.Target;
                var plan = new LaunchPlan
                {
                    Kind = LaunchMethodKind.Uri,
                    FileName = uri,
                    UseShellExecute = true,
                    Description = method.Description,
                    ViaOfficialLauncher = true,
                    DetectionTimeout = TimeSpan.FromSeconds(180),
                };
                if (customArgs is not null)
                {
                    if (scheme.Equals("steam", StringComparison.OrdinalIgnoreCase) && game.Metadata.TryGetValue("SteamAppId", out var appId))
                    {
                        // Steam supports arguments through steam://run/<appid>//<args>/ (Steam may ask the user to confirm).
                        plan = new LaunchPlan
                        {
                            Kind = LaunchMethodKind.Uri,
                            FileName = $"steam://run/{appId}//{Uri.EscapeDataString(customArgs)}/",
                            UseShellExecute = true,
                            Description = method.Description + " with custom arguments",
                            ViaOfficialLauncher = true,
                            DetectionTimeout = TimeSpan.FromSeconds(180),
                        };
                    }
                    else
                    {
                        plan.Warnings.Add("Custom launch arguments can't be passed through this launcher. Set them in the launcher itself or choose 'Run executable directly'.");
                    }
                }
                return plan;
            }

            case LaunchMethodKind.PackagedApp:
            {
                if (!IsValidAumid(method.Target))
                {
                    error = $"Invalid app id: {method.Target}";
                    return null;
                }
                var plan = new LaunchPlan
                {
                    Kind = LaunchMethodKind.PackagedApp,
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{method.Target}",
                    UseShellExecute = false,
                    Description = method.Description,
                    ViaOfficialLauncher = true,
                    DetectionTimeout = TimeSpan.FromSeconds(120),
                };
                if (customArgs is not null) plan.Warnings.Add("Store apps don't accept custom launch arguments.");
                return plan;
            }

            default:
                error = "Unknown launch method.";
                return null;
        }
    }

    public static bool IsAllowedUri(string? uri, out string scheme)
    {
        scheme = "";
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 2048 || uri.Any(char.IsControl) || uri.Contains(' ')) return false;
        var colon = uri.IndexOf(':');
        if (colon <= 0) return false;
        scheme = uri[..colon];
        if (!scheme.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '+')) return false;
        var s = scheme;
        return AllowedSchemes.Contains(s) || AllowedSchemes.Any(a => a.EndsWith('-') && s.StartsWith(a, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"PackageFamilyName!AppId" — letters, digits, '.', '_', '-' and exactly one '!'.</summary>
    public static bool IsValidAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid) || aumid.Length > 512) return false;
        var parts = aumid.Split('!');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;
        return aumid.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '!');
    }

    private static string JoinArgs(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a)) return b ?? "";
        if (string.IsNullOrWhiteSpace(b)) return a!;
        return a + " " + b;
    }
}
