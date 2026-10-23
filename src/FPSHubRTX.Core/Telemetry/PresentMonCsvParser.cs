using System.Globalization;

namespace FPSHubRTX.Core.Telemetry;

/// <summary>One presented frame as reported by PresentMon.</summary>
public readonly record struct FrameRecord(
    int ProcessId,
    string Application,
    string? SwapChain,
    string? Runtime,
    string? PresentMode,
    double TimeMs,
    double? MsBetweenPresents,
    double? MsBetweenDisplayChange,
    string? FrameType);

/// <summary>
/// Streaming parser for PresentMon CSV output (2.x default metrics, --v2_metrics and --v1_metrics).
/// The header row decides which columns exist, so the parser survives column re-ordering between versions.
/// </summary>
public sealed class PresentMonCsvParser
{
    private Dictionary<string, int>? _columns;
    private int _app = -1, _pid = -1, _swap = -1, _runtime = -1, _mode = -1, _time = -1, _between = -1, _display = -1, _frameType = -1;
    private bool _timeIsSeconds;

    public bool HasHeader => _columns is not null;

    /// <summary>Name of the per-frame interval column in use ("MsBetweenPresents", "FrameTime", …).</summary>
    public string? FrameTimeColumn { get; private set; }

    public IReadOnlyCollection<string> Columns => _columns?.Keys ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>Parses one line. Returns null for the header, blank lines and unparseable rows.</summary>
    public FrameRecord? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var fields = Split(line);

        if (_columns is null || (fields.Count > 0 && fields[0] == "Application"))
        {
            ReadHeader(fields);
            return null;
        }

        if (_pid < 0 || _between < 0) return null;
        if (fields.Count <= Math.Max(_pid, _between)) return null;

        if (!int.TryParse(fields[_pid], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
        var time = Number(Get(fields, _time)) ?? 0;
        if (_timeIsSeconds) time *= 1000.0;

        return new FrameRecord(
            pid,
            Get(fields, _app) ?? "",
            Get(fields, _swap),
            Get(fields, _runtime),
            Get(fields, _mode),
            time,
            Number(Get(fields, _between)),
            Number(Get(fields, _display)),
            Get(fields, _frameType));
    }

    private void ReadHeader(List<string> fields)
    {
        _columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fields.Count; i++) _columns.TryAdd(fields[i].Trim(), i);
        int Col(params string[] names)
        {
            foreach (var n in names)
                if (_columns!.TryGetValue(n, out var idx)) return idx;
            return -1;
        }

        _app = Col("Application");
        _pid = Col("ProcessID");
        _swap = Col("SwapChainAddress");
        _runtime = Col("PresentRuntime", "Runtime");
        _mode = Col("PresentMode");
        _frameType = Col("FrameType");
        _time = Col("CPUStartTime", "CPUStartQPCTime", "TimeInMs", "TimeInSeconds");
        _timeIsSeconds = _time >= 0 && fields[_time].Trim().Equals("TimeInSeconds", StringComparison.OrdinalIgnoreCase);
        _between = Col("MsBetweenPresents", "FrameTime", "MsBetweenAppStart");
        FrameTimeColumn = _between >= 0 ? fields[_between].Trim() : null;
        _display = Col("MsBetweenDisplayChange", "DisplayedTime");
    }

    private static string? Get(List<string> fields, int index)
        => index >= 0 && index < fields.Count ? fields[index] : null;

    private static double? Number(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Equals("NA", StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    /// <summary>Minimal CSV splitter supporting quoted fields.</summary>
    internal static List<string> Split(string line)
    {
        var result = new List<string>(48);
        var i = 0;
        while (i <= line.Length)
        {
            if (i < line.Length && line[i] == '"')
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(line[i++]);
                }
                result.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++;
                else if (i >= line.Length) break;
                continue;
            }
            var comma = line.IndexOf(',', i);
            if (comma < 0)
            {
                result.Add(line[i..].TrimEnd('\r'));
                break;
            }
            result.Add(line[i..comma]);
            i = comma + 1;
            if (i == line.Length)
            {
                result.Add("");
                break;
            }
        }
        return result;
    }
}

/// <summary>
/// A process can own several swap chains (game + launcher overlay + video). Mixing their intervals would
/// corrupt the frame times, so only the most active swap chain per process is used — the same approach
/// PresentMon-based tools use.
/// </summary>
public sealed class SwapChainSelector
{
    private readonly Dictionary<(int, string), int> _counts = new();
    private int _sinceReset;
    private (int Pid, string Swap)? _primary;

    public bool Accept(in FrameRecord frame)
    {
        var key = (frame.ProcessId, frame.SwapChain ?? "");
        _counts[key] = _counts.TryGetValue(key, out var c) ? c + 1 : 1;
        if (++_sinceReset >= 240)
        {
            // Re-evaluate periodically (e.g. game switched from launcher window to its main window).
            _primary = _counts.MaxBy(kv => kv.Value).Key;
            _counts.Clear();
            _sinceReset = 0;
        }
        else if (_primary is null && _sinceReset >= 30)
        {
            _primary = _counts.MaxBy(kv => kv.Value).Key;
        }
        return _primary is null || _primary.Value == key;
    }

    public void Reset()
    {
        _counts.Clear();
        _primary = null;
        _sinceReset = 0;
    }
}
