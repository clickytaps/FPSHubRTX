using System.Diagnostics;
using System.Text;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Launching;

public sealed record ProcessSnapshot(int Pid, string Name, string? ImagePath);

/// <summary>Lists running processes with their executable paths, without elevated rights.</summary>
public interface IProcessInspector
{
    IReadOnlyList<ProcessSnapshot> GetProcesses();
    bool IsRunning(int pid);
    int? GetForegroundProcessId();
}

public sealed class SystemProcessInspector : IProcessInspector
{
    public IReadOnlyList<ProcessSnapshot> GetProcesses()
    {
        var list = new List<ProcessSnapshot>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return list;
        }

        foreach (var p in processes)
        {
            using (p)
            {
                try
                {
                    if (p.Id <= 4) continue; // Idle/System
                    list.Add(new ProcessSnapshot(p.Id, p.ProcessName, GetImagePath(p)));
                }
                catch (InvalidOperationException)
                {
                    // exited while enumerating
                }
            }
        }
        return list;
    }

    public bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // exists but access denied (e.g. protected process)
        }
    }

    public int? GetForegroundProcessId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    /// <summary>
    /// QueryFullProcessImageName with PROCESS_QUERY_LIMITED_INFORMATION works for other users' and most
    /// protected processes without admin rights; anti-cheat-protected processes simply return null.
    /// </summary>
    private static string? GetImagePath(Process p)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                var size = (uint)sb.Capacity;
                return NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        try
        {
            return p.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
