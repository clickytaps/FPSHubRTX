using System.Runtime.InteropServices;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Monitoring;

/// <summary>
/// Thin wrapper over the Windows Performance Data Helper API (the same counters Task Manager and
/// Performance Monitor use). English counter paths are used so it works on every Windows language.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new(StringComparer.OrdinalIgnoreCase);

    public PdhQuery()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) != NativeMethods.ERROR_SUCCESS) _query = IntPtr.Zero;
    }

    public bool IsOpen => _query != IntPtr.Zero;

    /// <summary>Adds a counter (wildcards allowed). Returns false when the counter doesn't exist on this PC.</summary>
    public bool Add(string key, string englishPath)
    {
        if (!IsOpen) return false;
        if (NativeMethods.PdhAddEnglishCounter(_query, englishPath, IntPtr.Zero, out var counter) != NativeMethods.ERROR_SUCCESS) return false;
        _counters[key] = counter;
        return true;
    }

    public bool Has(string key) => _counters.ContainsKey(key);

    public bool Collect() => IsOpen && NativeMethods.PdhCollectQueryData(_query) == NativeMethods.ERROR_SUCCESS;

    public double? GetValue(string key)
    {
        if (!_counters.TryGetValue(key, out var counter)) return null;
        var status = NativeMethods.PdhGetFormattedCounterValue(counter, NativeMethods.PDH_FMT_DOUBLE | NativeMethods.PDH_FMT_NOCAP100, out _, out var value);
        if (status != NativeMethods.ERROR_SUCCESS || value.CStatus > 1) return null; // 0 = valid, 1 = new data
        return value.DoubleValue;
    }

    /// <summary>Values of every instance matched by a wildcard counter.</summary>
    public IReadOnlyList<(string Instance, double Value)> GetArray(string key)
    {
        var result = new List<(string, double)>();
        if (!_counters.TryGetValue(key, out var counter)) return result;

        uint size = 0;
        var status = NativeMethods.PdhGetFormattedCounterArray(counter, NativeMethods.PDH_FMT_DOUBLE | NativeMethods.PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (status != NativeMethods.PDH_MORE_DATA || size == 0) return result;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = NativeMethods.PdhGetFormattedCounterArray(counter, NativeMethods.PDH_FMT_DOUBLE | NativeMethods.PDH_FMT_NOCAP100, ref size, out var count, buffer);
            if (status != NativeMethods.ERROR_SUCCESS) return result;
            // PDH_FMT_COUNTERVALUE_ITEM_W = { LPWSTR szName; PDH_FMT_COUNTERVALUE FmtValue; }
            var itemSize = IntPtr.Size + Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE>();
            for (var i = 0; i < count; i++)
            {
                var itemPtr = buffer + i * itemSize;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(itemPtr)) ?? "";
                var value = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE>(itemPtr + IntPtr.Size);
                if (value.CStatus <= 1) result.Add((name, value.DoubleValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}

/// <summary>Aggregates "GPU Engine" counter instances the way Task Manager does.</summary>
public static class GpuEngineMath
{
    /// <summary>
    /// Instance names look like "pid_1234_luid_0x00000000_0x0000D1F2_phys_0_eng_3_engtype_3D".
    /// Utilisation per engine = sum over processes; per adapter = busiest engine; overall = busiest adapter.
    /// Optionally also returns the busiest-engine utilisation for one set of process ids.
    /// </summary>
    public static (double? Overall, double? ForProcesses) Aggregate(IEnumerable<(string Instance, double Value)> items, IReadOnlySet<int>? pids)
    {
        var perEngine = new Dictionary<string, double>(StringComparer.Ordinal);
        var perEngineForPids = new Dictionary<string, double>(StringComparer.Ordinal);
        var any = false;
        foreach (var (instance, value) in items)
        {
            if (!TryParse(instance, out var pid, out var engineKey)) continue;
            any = true;
            perEngine[engineKey] = perEngine.GetValueOrDefault(engineKey) + value;
            if (pids is not null && pids.Contains(pid))
                perEngineForPids[engineKey] = perEngineForPids.GetValueOrDefault(engineKey) + value;
        }
        if (!any) return (null, null);
        var overall = Math.Clamp(perEngine.Values.DefaultIfEmpty(0).Max(), 0, 100);
        double? game = pids is null ? null : Math.Clamp(perEngineForPids.Values.DefaultIfEmpty(0).Max(), 0, 100);
        return (overall, game);
    }

    internal static bool TryParse(string instance, out int pid, out string engineKey)
    {
        pid = 0;
        engineKey = "";
        var parts = instance.Split('_');
        // pid_<n>_luid_<hi>_<lo>_phys_<p>_eng_<e>_engtype_<type>
        var pidIdx = Array.IndexOf(parts, "pid");
        var luidIdx = Array.IndexOf(parts, "luid");
        var engIdx = Array.IndexOf(parts, "eng");
        if (pidIdx < 0 || luidIdx < 0 || engIdx < 0 || pidIdx + 1 >= parts.Length || luidIdx + 2 >= parts.Length || engIdx + 1 >= parts.Length) return false;
        if (!int.TryParse(parts[pidIdx + 1], out pid)) return false;
        var physIdx = Array.IndexOf(parts, "phys");
        var phys = physIdx >= 0 && physIdx + 1 < parts.Length ? parts[physIdx + 1] : "0";
        engineKey = $"{parts[luidIdx + 1]}_{parts[luidIdx + 2]}_{phys}_{parts[engIdx + 1]}";
        return true;
    }
}
