namespace Quadrant.Server.Sessions;

/// <summary>
/// Tracks all active game sessions, keyed by room code. Thread-safe for concurrent
/// SignalR calls from multiple clients.
/// </summary>
public sealed class SessionManager
{
    private readonly Dictionary<string, GameSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Creates a new session and registers it. Returns the session.</summary>
    public GameSession Create(string gameId, IReadOnlyList<Quadrant.RulesEngine.ShipInstance> ships)
    {
        lock (_lock)
        {
            var session = GameSession.Create(gameId, ships);
            _sessions[session.RoomCode] = session;
            return session;
        }
    }

    /// <summary>Looks up a session by room code, or null if not found.</summary>
    public GameSession? Get(string roomCode)
    {
        lock (_lock)
            return _sessions.TryGetValue(roomCode.Trim(), out var s) ? s : null;
    }

    /// <summary>Removes and returns a session (when combat ends or is abandoned).</summary>
    public GameSession? Remove(string roomCode)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(roomCode.Trim(), out var s))
            {
                _sessions.Remove(roomCode);
                return s;
            }
            return null;
        }
    }

    /// <summary>Number of active sessions (for diagnostics).</summary>
    public int Count
    {
        get { lock (_lock) return _sessions.Count; }
    }
}
