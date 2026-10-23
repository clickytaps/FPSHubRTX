using FPSHubRTX.Core.Models;

namespace FPSHubRTX.Core.Statistics;

public sealed record GamePlaytime(string GameId, string GameName, TimeSpan Total, int Sessions, DateTime? LastPlayedUtc);

public sealed record DailyPlaytime(DateOnly Day, TimeSpan Total);

public sealed record GameFpsHistory(string GameId, string GameName, IReadOnlyList<(DateTime StartUtc, double AverageFps, double? OnePercentLow)> Points, double? BestAverageFps);

public sealed class PlaytimeReport
{
    public TimeSpan Today { get; init; }
    public TimeSpan ThisWeek { get; init; }
    public TimeSpan ThisMonth { get; init; }
    public TimeSpan AllTime { get; init; }
    public int SessionCount { get; init; }
    public TimeSpan AverageSessionLength { get; init; }
    public IReadOnlyList<GamePlaytime> MostPlayed { get; init; } = Array.Empty<GamePlaytime>();
    public IReadOnlyList<GamePlaytime> Recent { get; init; } = Array.Empty<GamePlaytime>();
    public IReadOnlyList<DailyPlaytime> Last30Days { get; init; } = Array.Empty<DailyPlaytime>();
    /// <summary>Mean of the per-session measured average FPS over the last 30 days; null when nothing was measured.</summary>
    public double? AverageMeasuredFps { get; init; }
    public int MeasuredSessionCount { get; init; }
}

/// <summary>
/// Playtime and FPS statistics from recorded sessions. Sessions crossing midnight / week / month
/// boundaries are split by overlap, using the local time zone supplied.
/// </summary>
public static class PlaytimeStatistics
{
    public static PlaytimeReport Build(IReadOnlyList<GameSession> sessions, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var todayStart = nowLocal.Date;
        var diff = ((int)todayStart.DayOfWeek + 6) % 7; // Monday-based weeks
        var weekStart = todayStart.AddDays(-diff);
        var monthStart = new DateTime(nowLocal.Year, nowLocal.Month, 1);

        TimeSpan Overlap(DateTime fromLocal, DateTime toLocal)
        {
            var total = TimeSpan.Zero;
            foreach (var s in sessions)
            {
                var a = ToLocal(s.StartUtc, zone);
                var b = ToLocal(s.EndUtc, zone);
                var start = a > fromLocal ? a : fromLocal;
                var end = b < toLocal ? b : toLocal;
                if (end > start) total += end - start;
            }
            return total;
        }

        var endOfToday = todayStart.AddDays(1);
        var perGame = sessions
            .GroupBy(s => s.GameId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GamePlaytime(
                g.Key,
                g.OrderByDescending(s => s.EndUtc).First().GameName,
                TimeSpan.FromSeconds(g.Sum(s => s.Duration.TotalSeconds)),
                g.Count(),
                g.Max(s => s.EndUtc)))
            .ToList();

        var days = new List<DailyPlaytime>();
        for (var i = 29; i >= 0; i--)
        {
            var dayStart = todayStart.AddDays(-i);
            days.Add(new DailyPlaytime(DateOnly.FromDateTime(dayStart), Overlap(dayStart, dayStart.AddDays(1))));
        }

        var cutoff = nowUtc.AddDays(-30);
        var measured = sessions.Where(s => s.EndUtc >= cutoff && s.FpsAvailability == FpsAvailability.Measured && s.Fps is { FrameCount: > 0 }).ToList();
        var all = TimeSpan.FromSeconds(sessions.Sum(s => s.Duration.TotalSeconds));

        return new PlaytimeReport
        {
            Today = Overlap(todayStart, endOfToday),
            ThisWeek = Overlap(weekStart, weekStart.AddDays(7)),
            ThisMonth = Overlap(monthStart, monthStart.AddMonths(1)),
            AllTime = all,
            SessionCount = sessions.Count,
            AverageSessionLength = sessions.Count == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(all.TotalSeconds / sessions.Count),
            MostPlayed = perGame.OrderByDescending(g => g.Total).Take(10).ToList(),
            Recent = perGame.OrderByDescending(g => g.LastPlayedUtc).Take(10).ToList(),
            Last30Days = days,
            AverageMeasuredFps = measured.Count == 0 ? null : measured.Average(s => s.Fps!.AverageFps),
            MeasuredSessionCount = measured.Count,
        };
    }

    public static IReadOnlyList<GameFpsHistory> FpsHistoryByGame(IReadOnlyList<GameSession> sessions)
    {
        return sessions
            .Where(s => s.FpsAvailability == FpsAvailability.Measured && s.Fps is { FrameCount: > 0 })
            .GroupBy(s => s.GameId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderBy(s => s.StartUtc).ToList();
                var points = ordered.Select(s => (s.StartUtc, s.Fps!.AverageFps, s.Fps.OnePercentLowFps)).ToList();
                return new GameFpsHistory(g.Key, ordered[^1].GameName, points, points.Max(p => p.AverageFps));
            })
            .OrderBy(h => h.GameName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static DateTime ToLocal(DateTime utc, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return t.TotalSeconds < 1 ? "0m" : $"{(int)t.TotalSeconds}s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m";
        return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
    }
}
