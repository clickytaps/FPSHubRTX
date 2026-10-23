using FPSHubRTX.Core.Diagnostics;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Persistence;

namespace FPSHubRTX.Core.Services;

public sealed class SessionsFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<GameSession> Sessions { get; set; } = new();
}

/// <summary>Stores completed play sessions locally (sessions.json).</summary>
public sealed class SessionStore
{
    private readonly JsonFileStore<SessionsFile> _store;
    private readonly object _gate = new();
    private readonly ILog _log;
    private SessionsFile _data;

    public SessionStore(string path, ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
        _store = new JsonFileStore<SessionsFile>(path, () => new SessionsFile(), _log);
        _data = _store.Load();
        _data.Sessions = _data.Sessions.Where(s => s is not null && s.EndUtc >= s.StartUtc).ToList();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<GameSession> GetAll()
    {
        lock (_gate) return _data.Sessions.ToList();
    }

    public void Add(GameSession session)
    {
        lock (_gate)
        {
            _data.Sessions.Add(session);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes all activity history (user request). Library playtime totals are reset by the caller.</summary>
    public void DeleteAll()
    {
        lock (_gate)
        {
            _data.Sessions.Clear();
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public int DeleteForGame(string gameId)
    {
        int removed;
        lock (_gate)
        {
            removed = _data.Sessions.RemoveAll(s => s.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase));
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public void ReplaceAll(IEnumerable<GameSession> sessions)
    {
        lock (_gate)
        {
            _data.Sessions = sessions.ToList();
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        try
        {
            _store.Save(_data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Could not save sessions", ex);
        }
    }
}
