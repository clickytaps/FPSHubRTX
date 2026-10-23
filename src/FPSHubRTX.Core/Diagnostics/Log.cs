using System.Collections.Concurrent;
using System.Text;

namespace FPSHubRTX.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public interface ILog
{
    void Write(LogLevel level, string message, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message) => log.Write(LogLevel.Debug, message);
    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);
    public static void Warn(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Warn, message, ex);
    public static void Error(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Error, message, ex);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string message, Exception? exception = null) { }
}

public sealed record LogEntry(DateTime TimestampUtc, LogLevel Level, string Message);

/// <summary>
/// Small rolling file logger. Every line is passed through <see cref="LogSanitizer"/> so the user's
/// profile name and anything that looks like a token never reaches disk.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private const long MaxBytes = 1_000_000;
    private const int MaxFiles = 3;
    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 5000);
    private readonly Thread _writer;
    private readonly ConcurrentQueue<LogEntry> _recent = new();

    public FileLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "FPSHubRTX log writer" };
        _writer.Start();
    }

    public string CurrentFile => Path.Combine(_directory, "fpshub.log");

    public IReadOnlyList<LogEntry> RecentEntries => _recent.ToArray();

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        var text = exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}";
        text = LogSanitizer.Sanitize(text);
        var entry = new LogEntry(DateTime.UtcNow, level, text);
        _recent.Enqueue(entry);
        while (_recent.Count > 200 && _recent.TryDequeue(out _)) { }
        if (_queue.IsAddingCompleted) return; // logging after shutdown is silently dropped
        try
        {
            _queue.TryAdd($"{entry.TimestampUtc:O} [{level.ToString().ToUpperInvariant()}] {text}");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void WriterLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                Roll();
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never crash the app.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Roll()
    {
        var info = new FileInfo(CurrentFile);
        if (!info.Exists || info.Length < MaxBytes) return;
        for (var i = MaxFiles - 1; i >= 1; i--)
        {
            var src = i == 1 ? CurrentFile : Path.Combine(_directory, $"fpshub.{i - 1}.log");
            var dst = Path.Combine(_directory, $"fpshub.{i}.log");
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
    }

    public void Dispose()
    {
        if (_queue.IsAddingCompleted) return;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }
}

/// <summary>In-memory log, useful for tests and for showing scan details in the UI.</summary>
public sealed class MemoryLog : ILog
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();
    public void Write(LogLevel level, string message, Exception? exception = null)
        => _entries.Enqueue(new LogEntry(DateTime.UtcNow, level, exception is null ? message : $"{message} | {exception.Message}"));
}
