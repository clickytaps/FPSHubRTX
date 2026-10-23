using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Platform;

namespace FPSHubRTX.Core.Telemetry;

/// <summary>
/// Finds the PresentMon console executable: the user's override, then the copy bundled under tools\PresentMon,
/// then any PresentMon*.exe placed next to the app.
/// </summary>
public static class PresentMonLocator
{
    public static string? Find(string applicationDirectory, string? userOverride)
    {
        if (!string.IsNullOrWhiteSpace(userOverride) && PathUtil.IsSafeAbsolutePath(userOverride) && PathUtil.FileExistsSafe(userOverride)
            && userOverride.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return userOverride;

        foreach (var dir in new[] { Path.Combine(applicationDirectory, "tools", "PresentMon"), Path.Combine(applicationDirectory, "tools"), applicationDirectory })
        {
            var exact = Path.Combine(dir, "PresentMon.exe");
            if (PathUtil.FileExistsSafe(exact)) return exact;
            var versioned = PathUtil.EnumerateFilesSafe(dir, "PresentMon*.exe")
                .Where(f => Path.GetFileName(f).Contains("x64", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("PresentMon.exe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (versioned is not null) return versioned;
        }
        return null;
    }
}

public enum CaptureProcessState
{
    NotStarted,
    Running,
    AccessDenied,
    Exited,
    Failed,
}

/// <summary>
/// Runs PresentMon (external ETW-based frame-presentation telemetry — no injection into the game)
/// writing CSV to a file, and tails that file. Works both when PresentMon runs as the current user
/// (requires membership of "Performance Log Users") and when the user explicitly chose an elevated capture.
/// </summary>
public sealed class PresentMonCapture : IDisposable
{
    public const string SessionName = "FPSHubRTX";

    private readonly string _presentMonExe;
    private readonly ILog _log;
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _tailTask;
    private bool _elevated;

    public PresentMonCapture(string presentMonExe, ILog log)
    {
        _presentMonExe = presentMonExe;
        _log = log;
    }

    public string? OutputFile { get; private set; }
    public CaptureProcessState State { get; private set; } = CaptureProcessState.NotStarted;
    public string? LastError { get; private set; }

    /// <summary>Raised on a background thread with each batch of parsed rows.</summary>
    public event Action<IReadOnlyList<FrameRecord>>? FramesReceived;

    /// <summary>Raised when the PresentMon process ends (state tells why).</summary>
    public event Action<CaptureProcessState>? ProcessEnded;

    public static string BuildArguments(IEnumerable<string> processNames, string outputFile)
    {
        var sb = new StringBuilder();
        foreach (var name in processNames.Where(IsSafeProcessName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("--process_name \"").Append(name).Append("\" ");
        }
        sb.Append("--output_file \"").Append(outputFile).Append("\" ");
        sb.Append("--no_console_stats --no_track_input ");
        sb.Append("--session_name ").Append(SessionName).Append(' ');
        sb.Append("--stop_existing_session --terminate_on_proc_exit");
        return sb.ToString();
    }

    internal static bool IsSafeProcessName(string name)
        => !string.IsNullOrWhiteSpace(name) && name.Length < 260 && name.IndexOfAny(new[] { '"', '\\', '/', ':', '*', '?', '<', '>', '|' }) < 0 && !name.Any(char.IsControl);

    public void Start(IReadOnlyCollection<string> processNames, string outputFile, bool elevated)
    {
        if (_process is not null) throw new InvalidOperationException("Capture already started.");
        if (processNames.Count == 0) throw new ArgumentException("No target processes.", nameof(processNames));

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        if (File.Exists(outputFile)) File.Delete(outputFile);
        OutputFile = outputFile;
        _elevated = elevated;

        var psi = new ProcessStartInfo(_presentMonExe, BuildArguments(processNames, outputFile))
        {
            WorkingDirectory = Path.GetDirectoryName(_presentMonExe)!,
            CreateNoWindow = true,
        };
        if (elevated)
        {
            // Only reached when the user explicitly clicked "Capture with administrator rights".
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
        }

        try
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("PresentMon did not start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            State = CaptureProcessState.Failed;
            LastError = "Administrator permission was declined, so frame capture didn't start.";
            throw new InvalidOperationException(LastError, ex);
        }

        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;
        if (!elevated)
        {
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) _stderr.AppendLine(e.Data); };
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) _stderr.AppendLine(e.Data); };
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
        }

        State = CaptureProcessState.Running;
        _cts = new CancellationTokenSource();
        _tailTask = Task.Run(() => TailLoop(outputFile, _cts.Token));
        _log.Info($"PresentMon started{(elevated ? " (elevated)" : "")} for {string.Join(", ", processNames)}");
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int? code = null;
        try { code = _process?.ExitCode; } catch (InvalidOperationException) { }
        string err;
        lock (_stderr) err = _stderr.ToString();

        if (err.Contains("access denied", StringComparison.OrdinalIgnoreCase) || err.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase))
        {
            State = CaptureProcessState.AccessDenied;
            LastError = "Windows denied access to frame-presentation events. Add your account to the \"Performance Log Users\" group (Settings → FPS) or capture with administrator rights.";
        }
        else if (State == CaptureProcessState.Running)
        {
            State = code is null or 0 ? CaptureProcessState.Exited : CaptureProcessState.Failed;
            if (State == CaptureProcessState.Failed)
                LastError = $"PresentMon stopped with exit code {code}. {LogSanitizer.Sanitize(FirstLines(err, 3))}".Trim();
        }
        _log.Info($"PresentMon exited (code {code?.ToString() ?? "?"}, state {State})");
        ProcessEnded?.Invoke(State);
    }

    private static string FirstLines(string text, int n)
        => string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(n));

    private async Task TailLoop(string file, CancellationToken ct)
    {
        var parser = new PresentMonCsvParser();
        var pending = new StringBuilder();
        var buffer = new byte[64 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder(); // stateful: multi-byte characters may span reads
        FileStream? stream = null;
        try
        {
            // Wait for PresentMon to create the file.
            while (!ct.IsCancellationRequested && stream is null)
            {
                try
                {
                    if (File.Exists(file)) stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (stream is null) await Task.Delay(250, ct).ConfigureAwait(false);
            }

            while (!ct.IsCancellationRequested && stream is not null)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    if (State != CaptureProcessState.Running && stream.Position >= stream.Length) break;
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    continue;
                }

                var charCount = decoder.GetChars(buffer, 0, read, chars, 0);
                pending.Append(chars, 0, charCount);
                var text = pending.ToString();
                var lastNewline = text.LastIndexOf('\n');
                if (lastNewline < 0) continue;
                pending.Clear();
                pending.Append(text, lastNewline + 1, text.Length - lastNewline - 1);

                var batch = new List<FrameRecord>();
                foreach (var line in text[..lastNewline].Split('\n'))
                {
                    var rec = parser.ParseLine(line.TrimEnd('\r'));
                    if (rec is not null) batch.Add(rec.Value);
                }
                if (batch.Count > 0) FramesReceived?.Invoke(batch);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Reading PresentMon output failed", ex);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>
    /// Stops the capture. A capture we started normally is simply terminated. An elevated PresentMon can only
    /// be stopped by an elevated call, so when <paramref name="allowElevatedStop"/> is true (user clicked Stop)
    /// we run "PresentMon --terminate_existing_session" elevated; otherwise it ends by itself when the game exits.
    /// </summary>
    public void Stop(bool allowElevatedStop)
    {
        _cts?.Cancel();
        var p = _process;
        if (p is not null)
        {
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                if (_elevated && allowElevatedStop) TryElevatedTerminate();
            }
        }
        try { _tailTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        if (State == CaptureProcessState.Running) State = CaptureProcessState.Exited;
    }

    private void TryElevatedTerminate()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(_presentMonExe, $"--session_name {SessionName} --terminate_existing_session")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.Warn("Could not stop elevated PresentMon session: " + ex.Message);
        }
    }

    public void Dispose()
    {
        Stop(allowElevatedStop: false);
        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }
        _cts?.Dispose();
    }
}
