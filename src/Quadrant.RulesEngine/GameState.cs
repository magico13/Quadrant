using System.Text.Json.Serialization;

namespace Quadrant.RulesEngine;

/// <summary>
/// The current phase of combat. A round cycles Engineering → Helm → Gunnery,
/// then repeats until one side is destroyed or flees.
/// </summary>
public enum CombatPhase
{
    /// <summary>Pre-combat: ships being placed on the grid.</summary>
    Setup,

    /// <summary>Crew choose their roles at the start of a round.</summary>
    CrewSetup,

    /// <summary>Engineers act (simultaneous). v1: placeholder / skip.</summary>
    Engineering,

    /// <summary>Pilots move ships in helm order; science officers act.</summary>
    Helm,

    /// <summary>Gunners fire in helm order; damage applied after all attacks.</summary>
    Gunnery,

    /// <summary>Combat is over (one side destroyed or surrendered).</summary>
    Complete
}

/// <summary>
/// The full authoritative state of a single combat. This is what the server owns
/// and broadcasts to clients. It is designed to be serializable so it can be sent
/// over SignalR and persisted for replay.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class GameState
{
    /// <summary>Unique identifier for this game session.</summary>
    public required string GameId { get; init; }

    /// <summary>The 6-character room code players use to join.</summary>
    public required string RoomCode { get; init; }

    /// <summary>Current phase of the combat loop.</summary>
    public CombatPhase Phase { get; set; } = CombatPhase.Setup;

    /// <summary>Current round number (1-based). Increments after Gunnery completes.</summary>
    public int Round { get; set; } = 0;

    /// <summary>All ships in the battle, keyed by instance ID.</summary>
    public List<ShipInstance> Ships { get; } = [];

    /// <summary>Tracking projectiles in flight; these never take ship turns or count for victory.</summary>
    public List<TrackingProjectile> Projectiles { get; } = [];

    /// <summary>
    /// The order in which ships act during Helm and Gunnery this round.
    /// Determined by Piloting check results (lowest first). Recomputed each round.
    /// </summary>
    public List<string> ActionOrder { get; set; } = [];

    /// <summary>Index into <see cref="ActionOrder"/> for the ship currently acting.</summary>
    public int CurrentActorIndex { get; set; } = 0;

    /// <summary>Whether combat has ended.</summary>
    public bool IsComplete => Phase == CombatPhase.Complete;

    /// <summary>Optional winner description (e.g. "Player 1" or "All ships destroyed").</summary>
    public string? Winner { get; set; }

    // --- Convenience ---

    /// <summary>Gets a ship by its instance ID, or null.</summary>
    public ShipInstance? GetShip(string instanceId)
        => Ships.FirstOrDefault(s => s.InstanceId == instanceId);

    /// <summary>The ship currently acting (if any).</summary>
    [JsonIgnore]
    public ShipInstance? CurrentActor
        => ActionOrder.Count > 0 && CurrentActorIndex < ActionOrder.Count
            ? GetShip(ActionOrder[CurrentActorIndex])
            : null;

    /// <summary>All ships that are still in the fight (not destroyed or disabled).</summary>
    public IEnumerable<ShipInstance> ActiveShips()
        => Ships.Where(s => !s.IsDestroyed && !s.IsDisabled);

    /// <summary>Checks if all ships on one side are destroyed/disabled.</summary>
    public bool IsSideDefeated(Func<ShipInstance, bool> isSide)
    {
        var sideShips = Ships.Where(isSide).ToList();
        return sideShips.Count > 0 && sideShips.All(s => s.IsDestroyed || s.IsDisabled);
    }
}
