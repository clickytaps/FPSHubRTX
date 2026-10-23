using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Launching;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Persistence;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.Core.Services;

/// <summary>Opens folders and allow-listed launcher links. Paths/URIs are validated first.</summary>
public static class ShellService
{
    public static bool OpenFolder(string? path, out string? error)
    {
        error = null;
        if (!PathUtil.IsSafeAbsolutePath(path) || !PathUtil.DirectoryExistsSafe(path))
        {
            error = "The folder doesn't exist (the drive may be disconnected).";
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(path!);
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Opens a launcher URI (allow-listed schemes only) or an https link.</summary>
    public static bool OpenUri(string? uri, out string? error)
    {
        error = null;
        var isHttps = uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
        if (!isHttps && !LaunchResolver.IsAllowedUri(uri, out _))
        {
            error = "This link type isn't allowed.";
            return false;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri!) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1155 }
                ? "The launcher that handles this link isn't installed."
                : ex.Message;
            return false;
        }
    }

    public static bool OpenFile(string? path, out string? error)
    {
        error = null;
        if (!PathUtil.IsSafeAbsolutePath(path) || !PathUtil.FileExistsSafe(path))
        {
            error = "File not found.";
            return false;
        }
        var ext = Path.GetExtension(path)!.ToLowerInvariant();
        if (ext is not (".txt" or ".csv" or ".json" or ".log" or ".md" or ".zip"))
        {
            error = "Only reports and exports can be opened from here.";
            return false;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path!) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>HKCU\...\Run registration for "Start with Windows" (per-user, no admin rights).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FPSHubRTX";

    public static void Apply(bool enabled, bool startMinimized, string exePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, $"\"{exePath}\"{(startMinimized ? " --minimized" : "")}");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Parses hotkeys like "Ctrl+Shift+F10" into Win32 RegisterHotKey modifiers and a virtual-key code.</summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey, string Text)
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint mods = 0;
        uint? vk = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": mods |= ModControl; continue;
                case "SHIFT": mods |= ModShift; continue;
                case "ALT": mods |= ModAlt; continue;
                case "WIN" or "WINDOWS": mods |= ModWin; continue;
            }
            if (vk is not null) return false; // two non-modifier keys
            vk = KeyCode(part);
            if (vk is null) return false;
        }
        if (vk is null || mods == 0) return false; // require a modifier so games' own keys aren't stolen
        gesture = new HotkeyGesture(mods, vk.Value, Normalize(mods, text));
        return true;
    }

    private static uint? KeyCode(string k)
    {
        if (k.Length == 1 && k[0] is >= 'A' and <= 'Z') return k[0];
        if (k.Length == 1 && k[0] is >= '0' and <= '9') return k[0];
        if (k.StartsWith('F') && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return k switch
        {
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "SCROLLLOCK" => 0x91,
            "PAUSE" => 0x13,
            "NUMPAD0" => 0x60,
            "NUMPAD1" => 0x61,
            "NUMPAD2" => 0x62,
            "NUMPAD3" => 0x63,
            "NUMPAD4" => 0x64,
            "NUMPAD5" => 0x65,
            "NUMPAD6" => 0x66,
            "NUMPAD7" => 0x67,
            "NUMPAD8" => 0x68,
            "NUMPAD9" => 0x69,
            _ => null,
        };
    }

    private static string Normalize(uint mods, string original)
    {
        var parts = new List<string>();
        if ((mods & ModControl) != 0) parts.Add("Ctrl");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModWin) != 0) parts.Add("Win");
        var key = original.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Last(p => p.ToUpperInvariant() is not ("CTRL" or "CONTROL" or "SHIFT" or "ALT" or "WIN" or "WINDOWS"));
        parts.Add(key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..]);
        return string.Join('+', parts);
    }
}

public sealed record UpdateInfo(string Version, string Url, string? Notes);

/// <summary>
/// Optional update check against a user-configured HTTPS JSON manifest. Never downloads or installs
/// anything by itself; it only reports that a newer version exists and where to get it.
/// </summary>
public static class UpdateChecker
{
    private sealed class Manifest
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    public static Version CurrentVersion => typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static async Task<(UpdateInfo? Update, string Message)> CheckAsync(string? feedUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
            return (null, "No update feed is configured. Check the project's releases page for new versions.");
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (null, "The update feed must be an https:// URL.");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"FPSHubRTX/{CurrentVersion}");
            var manifest = await http.GetFromJsonAsync<Manifest>(uri, ct).ConfigureAwait(false);
            if (manifest?.Version is null || !Version.TryParse(manifest.Version, out var latest))
                return (null, "The update feed returned an unexpected response.");
            if (latest <= CurrentVersion) return (null, $"You're up to date (version {CurrentVersion.ToString(3)}).");
            var url = manifest.Url is { } u && Uri.TryCreate(u, UriKind.Absolute, out var dl) && dl.Scheme == Uri.UriSchemeHttps ? u : feedUrl;
            return (new UpdateInfo(latest.ToString(), url, manifest.Notes), $"Version {latest} is available.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return (null, "Could not check for updates: " + ex.Message);
        }
    }
}

/// <summary>Builds a diagnostic report that is safe to share: no user names, tokens or (by default) game titles.</summary>
public static class DiagnosticReport
{
    public static string Build(AppPaths paths, AppSettings settings, IReadOnlyList<Game> games, IReadOnlyList<HealthIssue> health,
        string? presentMonPath, FpsSnapshot? fps, IReadOnlyList<LogEntry> recentLog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FPS Hub RTX diagnostic report");
        sb.AppendLine($"Generated (UTC): {DateTime.UtcNow:O}");
        sb.AppendLine($"App version:     {UpdateChecker.CurrentVersion}");
        sb.AppendLine($"OS:              {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET:            {Environment.Version}");
        sb.AppendLine($"Elevated:        {IsElevated()}");
        sb.AppendLine($"Portable mode:   {File.Exists(Path.Combine(paths.ApplicationDirectory, "portable.txt"))}");
        sb.AppendLine($"PresentMon:      {(presentMonPath is null ? "NOT FOUND" : LogSanitizer.Sanitize(presentMonPath))}");
        sb.AppendLine($"FPS state:       {fps?.State.ToString() ?? "n/a"} {LogSanitizer.Sanitize(fps?.StateDetail)}");
        sb.AppendLine();
        sb.AppendLine("Library");
        sb.AppendLine($"  Games: {games.Count}");
        foreach (var grp in games.GroupBy(g => g.Launcher).OrderBy(g => g.Key))
            sb.AppendLine($"  {grp.Key,-12} {grp.Count(),4} (missing {grp.Count(g => g.Status == InstallStatus.Missing)}, inaccessible {grp.Count(g => g.Status == InstallStatus.Inaccessible)})");
        sb.AppendLine();
        sb.AppendLine("Settings (summary)");
        sb.AppendLine($"  Theme={settings.Theme}, Overlay={settings.Overlay.Enabled}/{settings.Overlay.Layout}, AutoFps={settings.Fps.AutoCaptureOnGameStart}, Interval={settings.Monitoring.SampleIntervalMs}ms, Minimal={settings.Monitoring.MinimalResourceMode}");
        sb.AppendLine($"  Launchers enabled: {string.Join(", ", settings.Scan.EnabledLaunchers.OrderBy(x => x))}");
        sb.AppendLine($"  Custom scan folders: {settings.Scan.CustomFolders.Count}, excluded: {settings.Scan.ExcludedDirectories.Count}, removable drives: {settings.Scan.IncludeRemovableDrives}");
        sb.AppendLine();
        sb.AppendLine($"Health issues: {health.Count}");
        foreach (var h in health.Take(100))
        {
            var name = settings.Privacy.IncludeGameTitlesInDiagnostics ? h.GameName : "(title hidden)";
            sb.AppendLine($"  [{h.Severity}] {name}: {LogSanitizer.Sanitize(h.Issue)}");
        }
        sb.AppendLine();
        sb.AppendLine("Recent log (sanitised)");
        foreach (var e in recentLog.TakeLast(80))
            sb.AppendLine($"  {e.TimestampUtc:HH:mm:ss} {e.Level,-5} {LogSanitizer.Sanitize(e.Message)}");
        return sb.ToString();
    }

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when the current user is in "Performance Log Users" (SID S-1-5-32-559), which PresentMon needs without admin rights.</summary>
    public static bool IsInPerformanceLogUsers()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var sid = new System.Security.Principal.SecurityIdentifier("S-1-5-32-559");
            return identity.Groups?.Any(g => g.Equals(sid)) == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds the signed-in user to "Performance Log Users" through an elevated PowerShell (one UAC prompt,
    /// only when the user clicks the button). Takes effect after signing out and back in.
    /// </summary>
    public static bool RequestPerformanceLogUsersMembership(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "Windows only.";
            return false;
        }
        try
        {
            string user;
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent()) user = identity.User!.Value; // SID, so it works for any account name
            var script = $"Add-LocalGroupMember -SID 'S-1-5-32-559' -Member '{user}' -ErrorAction Stop";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(30_000);
            if (p is { HasExited: true, ExitCode: not 0 })
            {
                error = "Windows reported an error adding you to the group (you may already be a member).";
                return false;
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "Cancelled at the Windows permission prompt.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Optional cover art download from Steam's public CDN for Steam games without local art.
/// Only runs when the user enabled it in Settings → Privacy, because it reveals installed app ids to Steam's CDN.
/// </summary>
public static class CoverArtService
{
    public static async Task<int> DownloadMissingSteamCoversAsync(LibraryService library, AppPaths paths, ILog log, CancellationToken ct)
    {
        var count = 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        foreach (var g in library.GetAll().Where(g => g.Launcher == LauncherKind.Steam && string.IsNullOrEmpty(g.EffectiveCoverPath)))
        {
            ct.ThrowIfCancellationRequested();
            if (!g.Metadata.TryGetValue("SteamAppId", out var appId) || !appId.All(char.IsDigit)) continue;
            var target = Path.Combine(paths.CoversDirectory, $"steam-{appId}.jpg");
            if (!File.Exists(target))
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg", ct).ConfigureAwait(false);
                    if (bytes.Length < 1024 || bytes.Length > 10 * 1024 * 1024 || bytes[0] != 0xFF || bytes[1] != 0xD8) continue; // must be a JPEG
                    await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    continue; // no art for this app id
                }
            }
            library.Update(g.Id, x => x.CoverImagePath = target);
            count++;
        }
        log.Info($"Downloaded {count} cover image(s) from Steam CDN");
        return count;
    }
}
