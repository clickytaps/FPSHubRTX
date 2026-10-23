using System.Globalization;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Statistics;

namespace FPSHubRTX.ViewModels;

/// <summary>Display formatting. Missing data is always shown as an explicit dash, never as a made-up number.</summary>
public static class Format
{
    public const string NoData = "—";

    public static string Fps(double? v) => v is null || !double.IsFinite(v.Value) ? NoData : v.Value.ToString(v.Value < 100 ? "0.0" : "0", CultureInfo.CurrentCulture);
    public static string Ms(double? v) => v is null || !double.IsFinite(v.Value) ? NoData : v.Value.ToString("0.00", CultureInfo.CurrentCulture) + " ms";
    public static string Percent(double? v) => v is null ? NoData : v.Value.ToString("0", CultureInfo.CurrentCulture) + "%";
    public static string Temp(double? v) => v is null ? NoData : v.Value.ToString("0", CultureInfo.CurrentCulture) + " °C";

    public static string Bytes(double? bytes)
    {
        if (bytes is null) return NoData;
        var b = bytes.Value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (b >= 1024 && i < units.Length - 1)
        {
            b /= 1024;
            i++;
        }
        return b.ToString(i >= 3 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[i];
    }

    public static string Rate(double? bytesPerSecond) => bytesPerSecond is null ? NoData : Bytes(bytesPerSecond) + "/s";

    public static string Duration(TimeSpan t) => PlaytimeStatistics.FormatDuration(t);

    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";

    public static string Relative(DateTime? utc)
    {
        if (utc is null) return "Never";
        var local = utc.Value.ToLocalTime();
        var days = (DateTime.Now.Date - local.Date).Days;
        return days switch
        {
            0 => "Today " + local.ToString("t", CultureInfo.CurrentCulture),
            1 => "Yesterday",
            < 7 => $"{days} days ago",
            _ => local.ToString("d", CultureInfo.CurrentCulture),
        };
    }

    public static string Launcher(LauncherKind k) => k switch
    {
        LauncherKind.Steam => "Steam",
        LauncherKind.Epic => "Epic Games",
        LauncherKind.Xbox => "Xbox / Store",
        LauncherKind.Gog => "GOG",
        LauncherKind.Ubisoft => "Ubisoft Connect",
        LauncherKind.EA => "EA app",
        LauncherKind.BattleNet => "Battle.net",
        LauncherKind.Riot => "Riot Client",
        LauncherKind.Minecraft => "Minecraft",
        LauncherKind.Roblox => "Roblox",
        LauncherKind.Standalone => "Standalone",
        LauncherKind.Manual => "Added manually",
        _ => k.ToString(),
    };

    public static string Status(InstallStatus s) => s switch
    {
        InstallStatus.Installed => "Installed",
        InstallStatus.Incomplete => "Updating / incomplete",
        InstallStatus.Missing => "Missing",
        InstallStatus.Inaccessible => "Inaccessible",
        _ => "Unknown",
    };

    public static string Initials(string name)
    {
        var words = name.Split(new[] { ' ', '-', ':', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).ToList();
        if (words.Count == 0) return "?";
        if (words.Count == 1) return words[0].Length >= 2 ? words[0][..2].ToUpperInvariant() : words[0].ToUpperInvariant();
        return (char.ToUpperInvariant(words[0][0]).ToString() + char.ToUpperInvariant(words[1][0])).ToString();
    }

    /// <summary>A stable colour per title for placeholder covers (not data — purely decorative).</summary>
    public static string PlaceholderColor(string seed)
    {
        string[] palette = { "#7C3AED", "#2563EB", "#0891B2", "#9333EA", "#4F46E5", "#0EA5E9", "#C026D3", "#6D28D9" };
        var hash = 0;
        foreach (var c in seed) hash = unchecked(hash * 31 + c);
        return palette[Math.Abs(hash % palette.Length)];
    }
}
