using Quadrant.RulesEngine;

namespace Quadrant.Server.Sessions;

/// <summary>
/// A single active game session: wraps the authoritative game state and combat
/// engine for one room. One instance per 6-char room code.
/// </summary>
public sealed class GameSession
{
    public required GameState State { get; init; }
    public required CombatEngine Engine { get; init; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActionAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>The 6-character room code.</summary>
    public string RoomCode => State.RoomCode;

    /// <summary>Connected player identifiers (SignalR connection IDs).</summary>
    public HashSet<string> ConnectedPlayers { get; } = [];

    /// <summary>
    /// Creates a new session with a generated room code and the given ships.
    /// </summary>
    public static GameSession Create(string gameId, IReadOnlyList<ShipInstance> ships, IDiceRoller? dice = null)
    {
        var state = new GameState
        {
            GameId = gameId,
            RoomCode = GenerateRoomCode()
        };
        state.Ships.AddRange(ships);

        var engine = new CombatEngine(state, dice ?? new DiceRoller());
        return new GameSession { State = state, Engine = engine };
    }

    /// <summary>
    /// Generates a 6-character room code from a confusion-free alphabet
    /// (no O/0, I/1, L/1). ~33^6 ≈ 1.3 billion combinations.
    /// </summary>
    public static string GenerateRoomCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var rng = Random.Shared;
        return new string(Enumerable.Range(0, 6).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
    }
}
