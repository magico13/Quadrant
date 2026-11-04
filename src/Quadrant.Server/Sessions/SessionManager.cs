using System.Text.Json;
using Microsoft.Data.Sqlite;
using Quadrant.RulesEngine;

namespace Quadrant.Server.Sessions;

/// <summary>Owns active games and persists their authoritative state in SQLite.</summary>
public sealed class SessionManager
{
    private readonly Dictionary<string, GameSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly Func<IDiceRoller> _diceFactory;
    private static readonly JsonSerializerOptions JsonOptions = new();
    private sealed record SessionSnapshot(GameState State, CombatEngineSnapshot Engine);

    static SessionManager() => SQLitePCL.Batteries_V2.Init();

    public SessionManager(string databasePath, TimeProvider? clock = null, Func<IDiceRoller>? diceFactory = null)
    {
        _clock = clock ?? TimeProvider.System;
        _diceFactory = diceFactory ?? (() => new DiceRoller());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS games (room_code TEXT PRIMARY KEY COLLATE NOCASE, created_ticks INTEGER NOT NULL, last_action_ticks INTEGER NOT NULL, phase INTEGER NOT NULL, snapshot TEXT NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT created_ticks, last_action_ticks, snapshot FROM games";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(reader.GetString(2), JsonOptions)
                ?? throw new InvalidDataException("Saved game snapshot is empty.");
            var engine = new CombatEngine(snapshot.State, _diceFactory());
            engine.RestoreSnapshot(snapshot.Engine);
            var session = new GameSession { State = snapshot.State, Engine = engine,
                CreatedAtUtc = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                LastActionAtUtc = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero) };
            _sessions.Add(session.RoomCode, session);
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    /// <summary>Prunes expired games and creates a unique new room.</summary>
    public GameSession Create(string gameId, IReadOnlyList<ShipInstance> ships)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            var expired = _sessions.Values.Where(s => s.State.IsComplete ||
                (s.State.Phase == CombatPhase.Setup ? now - s.CreatedAtUtc >= TimeSpan.FromMinutes(5)
                    : now - s.LastActionAtUtc >= TimeSpan.FromDays(30))).Select(s => s.RoomCode).ToList();
            foreach (var room in expired)
            {
                var candidate = _sessions[room];
                if (!candidate.Gate.Wait(0)) continue;
                try { RemoveLocked(room); }
                finally { candidate.Gate.Release(); }
            }
            GameSession session;
            do { session = GameSession.Create(gameId, ships, _diceFactory()); } while (_sessions.ContainsKey(session.RoomCode));
            session.CreatedAtUtc = session.LastActionAtUtc = now;
            SaveLocked(session);
            _sessions.Add(session.RoomCode, session);
            return session;
        }
    }

    public GameSession? Get(string roomCode)
    {
        lock (_lock) return _sessions.TryGetValue(roomCode.Trim(), out var session) ? session : null;
    }

    /// <summary>Save a game after a request; only player actions extend its lifetime.</summary>
    public void Save(GameSession session, bool playerAction)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(session.RoomCode, out var current) || !ReferenceEquals(current, session)) return;
            if (playerAction) session.LastActionAtUtc = _clock.GetUtcNow();
            SaveLocked(session);
        }
    }

    private void SaveLocked(GameSession session)
    {
        var json = JsonSerializer.Serialize(new SessionSnapshot(session.State, session.Engine.CaptureSnapshot()), JsonOptions);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO games (room_code, created_ticks, last_action_ticks, phase, snapshot) VALUES ($room, $created, $last, $phase, $snapshot) ON CONFLICT(room_code) DO UPDATE SET last_action_ticks=excluded.last_action_ticks, phase=excluded.phase, snapshot=excluded.snapshot";
        command.Parameters.AddWithValue("$room", session.RoomCode);
        command.Parameters.AddWithValue("$created", session.CreatedAtUtc.UtcTicks);
        command.Parameters.AddWithValue("$last", session.LastActionAtUtc.UtcTicks);
        command.Parameters.AddWithValue("$phase", (int)session.State.Phase);
        command.Parameters.AddWithValue("$snapshot", json);
        command.ExecuteNonQuery();
    }

    public GameSession? Remove(string roomCode)
    {
        lock (_lock) return RemoveLocked(roomCode.Trim());
    }

    private GameSession? RemoveLocked(string roomCode)
    {
        if (!_sessions.Remove(roomCode, out var session)) return null;
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM games WHERE room_code = $room";
        command.Parameters.AddWithValue("$room", roomCode);
        command.ExecuteNonQuery();
        return session;
    }

    public int Count { get { lock (_lock) return _sessions.Count; } }
}
