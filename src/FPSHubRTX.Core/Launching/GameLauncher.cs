using System.ComponentModel;
using System.Diagnostics;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;

namespace FPSHubRTX.Core.Launching;

public sealed class LaunchResult
{
    public bool Started { get; init; }
    public LaunchPlan? Plan { get; init; }
    public int? ProcessId { get; init; }
    public string? Error { get; init; }
    /// <summary>Sanitised technical details suitable for the log and the diagnostics report.</summary>
    public string? Diagnostic { get; init; }
    /// <summary>The game needs administrator rights; the user may explicitly retry elevated.</summary>
    public bool RequiresElevation { get; init; }
}

public interface IProcessStarter
{
    /// <summary>Starts the plan and returns the new process id when the OS reports one.</summary>
    int? Start(LaunchPlan plan, bool runElevated);
}

public sealed class SystemProcessStarter : IProcessStarter
{
    public int? Start(LaunchPlan plan, bool runElevated)
    {
        var psi = new ProcessStartInfo(plan.FileName)
        {
            Arguments = plan.Arguments,
            UseShellExecute = plan.UseShellExecute || runElevated,
        };
        if (!string.IsNullOrWhiteSpace(plan.WorkingDirectory)) psi.WorkingDirectory = plan.WorkingDirectory;
        if (runElevated) psi.Verb = "runas"; // only ever after the user explicitly chose "Run as administrator"
        using var process = Process.Start(psi);
        try
        {
            return process?.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Starts games. Never starts anything on its own: launching only happens when the user presses PLAY.
/// </summary>
public sealed class GameLauncher
{
    private readonly IProcessStarter _starter;
    private readonly RunningGamesMonitor _monitor;
    private readonly ILog _log;

    public GameLauncher(IProcessStarter starter, RunningGamesMonitor monitor, ILog log)
    {
        _starter = starter;
        _monitor = monitor;
        _log = log;
    }

    public LaunchResult Launch(Game game, bool runElevated = false)
    {
        var resolution = LaunchResolver.Resolve(game);
        if (!resolution.Success)
        {
            _log.Warn($"Launch of {game.DisplayName} not possible: {resolution.Error}");
            return new LaunchResult { Error = resolution.Error, Diagnostic = $"Resolution failed for {game.Id}: {resolution.Error}" };
        }

        var plan = resolution.Plan!;
        try
        {
            _monitor.ExpectLaunch(game.Id, plan.DetectionTimeout);
            var pid = _starter.Start(plan, runElevated);
            if (pid is not null) _monitor.AddPidHint(game.Id, pid.Value);
            _log.Info($"Launched {game.DisplayName} via {plan.Kind} ({plan.Description}){(pid is null ? "" : $", pid {pid}")}");
            foreach (var w in plan.Warnings) _log.Info($"Launch note for {game.DisplayName}: {w}");
            return new LaunchResult { Started = true, Plan = plan, ProcessId = pid };
        }
        catch (Win32Exception ex)
        {
            _monitor.CancelExpectedLaunch(game.Id);
            var (message, elevation) = Describe(ex, plan);
            var diag = LogSanitizer.Sanitize($"Win32 error {ex.NativeErrorCode} starting {plan.Kind} '{plan.FileName}' args='{plan.Arguments}' wd='{plan.WorkingDirectory}': {ex.Message}");
            _log.Error($"Launch failed for {game.DisplayName}: {diag}");
            return new LaunchResult { Error = message, Plan = plan, Diagnostic = diag, RequiresElevation = elevation };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _monitor.CancelExpectedLaunch(game.Id);
            var diag = LogSanitizer.Sanitize($"{ex.GetType().Name} starting {plan.Kind} '{plan.FileName}': {ex.Message}");
            _log.Error($"Launch failed for {game.DisplayName}: {diag}");
            return new LaunchResult { Error = "Windows could not start the game: " + ex.Message, Plan = plan, Diagnostic = diag };
        }
    }

    internal static (string Message, bool Elevation) Describe(Win32Exception ex, LaunchPlan plan) => ex.NativeErrorCode switch
    {
        2 or 3 => (plan.Kind == LaunchMethodKind.Uri
            ? "The launcher for this game doesn't seem to be installed."
            : "The game executable was not found. It may have been moved or uninstalled — try 'Locate installation' or rescan.", false),
        5 => ("Access was denied when starting the game. Check that your antivirus isn't blocking it.", false),
        740 => ("This game asks for administrator rights. You can retry with 'Run as administrator' — FPS Hub never elevates automatically.", true),
        1155 => ("No app is registered to open this launcher link. Install or repair the game's launcher.", false),
        1223 => ("The launch was cancelled at the Windows permission prompt.", false),
        193 or 216 => ("The selected file isn't a valid Windows program for this PC.", false),
        _ => ($"Windows could not start the game (error {ex.NativeErrorCode}: {ex.Message}).", false),
    };
}
