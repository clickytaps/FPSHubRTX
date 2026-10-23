using System.Text.Json;
using System.Text.Json.Serialization;
using FPSHubRTX.Core.Diagnostics;

namespace FPSHubRTX.Core.Persistence;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Compact options for large files (benchmark frame samples).</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}

/// <summary>
/// Crash-safe JSON persistence: writes go to a temp file which then atomically replaces the target,
/// and the previous version is kept as ".bak". A corrupt file is moved aside instead of crashing the app.
/// </summary>
public sealed class JsonFileStore<T> where T : class
{
    private readonly string _path;
    private readonly Func<T> _factory;
    private readonly JsonSerializerOptions _options;
    private readonly ILog _log;
    private readonly object _ioLock = new();

    public JsonFileStore(string path, Func<T> factory, ILog? log = null, JsonSerializerOptions? options = null)
    {
        _path = path;
        _factory = factory;
        _log = log ?? NullLog.Instance;
        _options = options ?? JsonDefaults.Options;
    }

    public string FilePath => _path;

    public T Load()
    {
        lock (_ioLock)
        {
            var value = TryRead(_path, out var primaryError);
            if (value is not null) return value;

            if (File.Exists(_path))
            {
                // Primary exists but is unreadable: keep a copy for diagnosis and try the backup.
                _log.Warn($"Could not read {Path.GetFileName(_path)}: {primaryError}. Trying backup.");
                MoveAside(_path);
            }

            var backup = TryRead(_path + ".bak", out _);
            if (backup is not null)
            {
                _log.Info($"Restored {Path.GetFileName(_path)} from backup.");
                return backup;
            }

            return _factory();
        }
    }

    public void Save(T value)
    {
        lock (_ioLock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, _options);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                try
                {
                    File.Copy(_path, _path + ".bak", overwrite: true);
                }
                catch (IOException ex)
                {
                    _log.Warn($"Could not refresh backup for {Path.GetFileName(_path)}: {ex.Message}");
                }
            }

            File.Move(tmp, _path, overwrite: true);
        }
    }

    private T? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0)
            {
                error = "file is empty";
                return null;
            }
            return JsonSerializer.Deserialize<T>(stream, _options);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return null;
        }
        catch (NotSupportedException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void MoveAside(string path)
    {
        try
        {
            var aside = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, aside, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not move corrupt file aside: {ex.Message}");
        }
    }
}
