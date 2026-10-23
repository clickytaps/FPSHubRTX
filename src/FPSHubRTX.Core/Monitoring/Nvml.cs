using System.Runtime.InteropServices;
using System.Text;

namespace FPSHubRTX.Core.Monitoring;

/// <summary>
/// NVIDIA Management Library (nvml.dll, installed with the NVIDIA driver) — the documented way to read
/// NVIDIA GPU temperature and memory. Loaded dynamically; on PCs without an NVIDIA GPU it is simply unavailable.
/// </summary>
internal sealed class Nvml : IDisposable
{
    private IntPtr _lib;
    private readonly List<IntPtr> _devices = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ShutdownDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CountDelegate(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleDelegate(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TempDelegate(IntPtr device, int sensor, out uint temp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MemDelegate(IntPtr device, out NvmlMemory mem);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NameDelegate(IntPtr device, [Out] byte[] name, uint length);

    private ShutdownDelegate? _shutdown;
    private TempDelegate? _temp;
    private MemDelegate? _mem;
    private NameDelegate? _name;

    public bool IsAvailable => _devices.Count > 0;

    public static Nvml? TryLoad()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var nvml = new Nvml();
        try
        {
            if (!NativeLibrary.TryLoad("nvml.dll", typeof(Nvml).Assembly, DllImportSearchPath.System32, out nvml._lib))
            {
                var nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
                if (!File.Exists(nvsmi) || !NativeLibrary.TryLoad(nvsmi, out nvml._lib)) return null;
            }

            T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(nvml._lib, name));
            var init = Fn<InitDelegate>("nvmlInit_v2");
            if (init() != 0)
            {
                nvml.Dispose();
                return null;
            }
            nvml._shutdown = Fn<ShutdownDelegate>("nvmlShutdown");
            var count = Fn<CountDelegate>("nvmlDeviceGetCount_v2");
            var handle = Fn<HandleDelegate>("nvmlDeviceGetHandleByIndex_v2");
            nvml._temp = Fn<TempDelegate>("nvmlDeviceGetTemperature");
            nvml._mem = Fn<MemDelegate>("nvmlDeviceGetMemoryInfo");
            nvml._name = Fn<NameDelegate>("nvmlDeviceGetName");

            if (count(out var n) == 0)
            {
                for (uint i = 0; i < n; i++)
                {
                    if (handle(i, out var dev) == 0) nvml._devices.Add(dev);
                }
            }
            if (nvml._devices.Count == 0)
            {
                nvml.Dispose();
                return null;
            }
            return nvml;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException or MarshalDirectiveException)
        {
            nvml.Dispose();
            return null;
        }
    }

    /// <summary>Hottest GPU core temperature in °C.</summary>
    public double? GetMaxTemperature()
    {
        if (_temp is null) return null;
        double? max = null;
        foreach (var d in _devices)
        {
            if (_temp(d, 0 /* NVML_TEMPERATURE_GPU */, out var t) == 0) max = Math.Max(max ?? 0, t);
        }
        return max;
    }

    public (ulong Used, ulong Total)? GetMemory()
    {
        if (_mem is null) return null;
        ulong used = 0, total = 0;
        var ok = false;
        foreach (var d in _devices)
        {
            if (_mem(d, out var m) == 0)
            {
                used += m.Used;
                total += m.Total;
                ok = true;
            }
        }
        return ok ? (used, total) : null;
    }

    public IReadOnlyList<string> GetNames()
    {
        var names = new List<string>();
        if (_name is null) return names;
        foreach (var d in _devices)
        {
            var buf = new byte[96];
            if (_name(d, buf, (uint)buf.Length) == 0)
            {
                var len = Array.IndexOf(buf, (byte)0);
                names.Add(Encoding.ASCII.GetString(buf, 0, len < 0 ? buf.Length : len));
            }
        }
        return names;
    }

    public void Dispose()
    {
        try { _shutdown?.Invoke(); } catch (Exception) { }
        _shutdown = null;
        if (_lib != IntPtr.Zero)
        {
            NativeLibrary.Free(_lib);
            _lib = IntPtr.Zero;
        }
    }
}
