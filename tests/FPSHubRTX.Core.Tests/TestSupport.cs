using System.Text;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Launching;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Scanning;
using FPSHubRTX.Core.Telemetry;

namespace FPSHubRTX.Core.Tests;

/// <summary>A throw-away folder tree for scanner/persistence fixtures.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "fpshub-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Fixture paths are written with '/' or '\'; use the OS separator consistently.</summary>
    private static string Norm(string relativePath)
        => relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    public string Dir(params string[] parts)
    {
        var p = Path.Combine(new[] { Root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relativePath, string content = "")
    {
        var p = Path.Combine(Root, Norm(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public string Bytes(string relativePath, byte[] content)
    {
        var p = Path.Combine(Root, Norm(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, content);
        return p;
    }

    /// <summary>Creates a fake executable of the given size (scanners only look at names and sizes).</summary>
    public string Exe(string relativePath, int sizeBytes = 200_000) => Bytes(relativePath, new byte[sizeBytes]);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class Ctx
{
    public static ScanEnvironment Env(TempDir t) => new()
    {
        ProgramData = t.Dir("ProgramData"),
        ProgramFiles = t.Dir("Program Files"),
        ProgramFilesX86 = t.Dir("Program Files (x86)"),
        LocalAppData = t.Dir("Local"),
        RoamingAppData = t.Dir("Roaming"),
    };

    public static ScanContext Make(TempDir t, IRegistryReader? reg = null, ScanSettings? settings = null, IPackageCatalog? packages = null,
        IDriveProvider? drives = null, CancellationToken ct = default)
        => new()
        {
            Environment = Env(t),
            Registry = reg ?? new InMemoryRegistry(),
            Drives = drives ?? new FixedDriveProvider(),
            Packages = packages ?? EmptyPackageCatalog.Instance,
            Settings = settings ?? new ScanSettings { ScanCommonGameFolders = false },
            Log = new MemoryLog(),
            CancellationToken = ct,
        };
}

public sealed class FakeInspector : IProcessInspector
{
    public List<ProcessSnapshot> Processes { get; } = new();
    public IReadOnlyList<ProcessSnapshot> GetProcesses() => Processes.ToList();
    public bool IsRunning(int pid) => Processes.Any(p => p.Pid == pid);
    public int? GetForegroundProcessId() => null;
}

public sealed class FakeStarter : IProcessStarter
{
    public List<LaunchPlan> Started { get; } = new();
    public Exception? Throw { get; set; }
    public int? Pid { get; set; } = 4_100_001;

    public int? Start(LaunchPlan plan, bool runElevated)
    {
        if (Throw is not null) throw Throw;
        Started.Add(plan);
        return Pid;
    }
}

public sealed class FakeCapture : IFrameCapture
{
    public event Action<IReadOnlyList<FrameRecord>>? FramesReceived;
    public event Action<CaptureProcessState>? ProcessEnded;
    public CaptureProcessState State { get; set; } = CaptureProcessState.NotStarted;
    public string? LastError { get; set; }
    public string? OutputFile { get; private set; }
    public IReadOnlyCollection<string>? StartedWith { get; private set; }
    public bool Stopped { get; private set; }
    public bool ThrowOnStart { get; set; }

    public void Start(IReadOnlyCollection<string> processNames, string outputFile, bool elevated)
    {
        if (ThrowOnStart) throw new InvalidOperationException("boom");
        StartedWith = processNames.ToList();
        OutputFile = outputFile;
        State = CaptureProcessState.Running;
    }

    public void Stop(bool allowElevatedStop)
    {
        Stopped = true;
        State = CaptureProcessState.Exited;
    }

    public void Push(IReadOnlyList<FrameRecord> frames) => FramesReceived?.Invoke(frames);
    public void End(CaptureProcessState state) { State = state; ProcessEnded?.Invoke(state); }
    public void Dispose() { }
}

public static class Frames
{
    /// <summary>Builds consecutive frames for one process/swap chain with the given frame times.</summary>
    public static List<FrameRecord> Make(int pid, string app, IEnumerable<double> frameTimes, double startMs = 0, string swap = "0x1")
    {
        var list = new List<FrameRecord>();
        var t = startMs;
        foreach (var ft in frameTimes)
        {
            t += ft;
            list.Add(new FrameRecord(pid, app, swap, "DXGI", "Hardware: Independent Flip", t, ft, ft, "Application"));
        }
        return list;
    }

    public static IEnumerable<double> Repeat(double ft, int count) => Enumerable.Repeat(ft, count);
}

public sealed class ManualClock
{
    public DateTime Now { get; set; } = new(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);
    public Func<DateTime> Func => () => Now;
    public void Advance(TimeSpan t) => Now += t;
}

public static class VdfText
{
    public static string Escape(string path) => path.Replace("\\", "\\\\");

    public static string LibraryFolders(params string[] libraries)
    {
        var sb = new StringBuilder("\"libraryfolders\"\n{\n");
        for (var i = 0; i < libraries.Length; i++)
        {
            sb.Append($"\t\"{i}\"\n\t{{\n\t\t\"path\"\t\t\"{Escape(libraries[i])}\"\n\t\t\"label\"\t\t\"\"\n\t\t\"apps\"\n\t\t{{\n\t\t}}\n\t}}\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    public static string AppManifest(string appId, string name, string installDir, int stateFlags = 4)
        => $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"Universe\"\t\t\"1\"\n\t\"name\"\t\t\"{name}\"\n\t\"StateFlags\"\t\t\"{stateFlags}\"\n\t\"installdir\"\t\t\"{installDir}\"\n}}\n";
}
