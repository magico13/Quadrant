using System.Text.Json.Serialization;

namespace Quadrant.RulesEngine;

/// <summary>
/// A starship's static definition (its "blueprint"). This is the data-driven part
/// loaded from JSON / the database. It does NOT include runtime state like current
/// position or hull damage — those live on <see cref="ShipInstance"/>.
/// </summary>
public sealed record ShipDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Starship tier (e.g. 0.33, 1, 2, 3). Used for balance reference.</summary>
    public double Tier { get; init; } = 1;

    /// <summary>Speed in hexes per round.</summary>
    public int Speed { get; init; } = 8;

    /// <summary>Maneuverability rating (affects turns and Piloting checks).</summary>
    public Maneuverability Maneuverability { get; init; } = Maneuverability.Average;

    /// <summary>Total Hull Points.</summary>
    public int HullPoints { get; init; } = 20;

    /// <summary>
    /// Damage Threshold: attacks dealing damage at or below this value to the hull
    /// are absorbed (no hull damage). Higher-tier ships have larger thresholds.
    /// </summary>
    public int DamageThreshold { get; init; } = 3;

    /// <summary>
    /// Critical Threshold: cumulative hull damage crossing a multiple of this value
    /// scores critical damage. Typically HullPoints / 4.
    /// </summary>
    public int CriticalThreshold { get; init; } = 5;

    /// <summary>Armor bonus added to AC.</summary>
    public int ArmorBonus { get; init; } = 2;

    /// <summary>Size modifier to AC/TL (e.g. -1 for small, +2 for large).</summary>
    public int SizeModifier { get; init; } = 0;

    /// <summary>Bonus from defensive countermeasures (added to TL).</summary>
    public int Countermeasures { get; init; } = 0;

    /// <summary>Shield points per quadrant. Keyed by quadrant name.</summary>
    public required QuadrantShields Shields { get; init; }

    /// <summary>The weapons this ship carries.</summary>
    public IReadOnlyList<Weapon> Weapons { get; init; } = [];

    // --- Crew skill values (simplified for v1: flat numbers, not per-character) ---

    /// <summary>Pilot's ranks in Piloting (used for AC/TL and helm ordering).</summary>
    public int PilotingRanks { get; init; } = 3;

    /// <summary>Gunner's base attack bonus or Piloting ranks (for gunnery checks).</summary>
    public int GunnerBaseAttack { get; init; } = 2;

    /// <summary>Gunner's Dexterity modifier.</summary>
    public int DexterityModifier { get; init; } = 1;

    // --- Combat values (set directly from published stats) ---

    /// <summary>Base Armor Class. Set directly to match published ship stats.</summary>
    public int BaseAC { get; init; } = 12;

    /// <summary>Base Target Lock. Set directly to match published ship stats.</summary>
    public int BaseTL { get; init; } = 12;

    /// <summary>Piloting check modifier based on speed.</summary>
    public int SpeedPilotingModifier => Speed switch
    {
        <= 4 => 2,
        6 => 1,
        8 or 9 or 10 => 0,
        12 => -1,
        _ => -2
    };

    /// <summary>Piloting check modifier based on maneuverability.</summary>
    public int ManeuverabilityPilotingModifier => Maneuverability switch
    {
        Maneuverability.Clumsy => -2,
        Maneuverability.Poor => -1,
        Maneuverability.Average => 0,
        Maneuverability.Good => 1,
        Maneuverability.Perfect => 2,
        _ => 0
    };

    /// <summary>Minimum hexes that must be traveled between turns.</summary>
    public int DistanceBetweenTurns => Maneuverability switch
    {
        Maneuverability.Clumsy => 4,
        Maneuverability.Poor => 3,
        Maneuverability.Average => 2,
        Maneuverability.Good => 1,
        Maneuverability.Perfect => 0,
        _ => 2
    };

    /// <summary>Creates a fresh runtime instance of this ship at the given position.</summary>
    public ShipInstance Instantiate(string shipInstanceId, Hex position, Facing facing, int teamId)
    {
        return new ShipInstance
        {
            InstanceId = shipInstanceId,
            Definition = this,
            Position = position,
            Facing = facing,
            TeamId = teamId,
            HullPoints = HullPoints,
            Shields = Shields.Clone(),
            IsDestroyed = false,
            IsDisabled = false
        };
    }
}

/// <summary>
/// Shield points for each of the four quadrants. Mutable at runtime (a ship's
/// shields deplete and recharge during combat). Quadrants are relative to the
/// ship's facing: Forward (1 face), Starboard (2 faces), Aft (1 face), Port (2 faces).
/// </summary>
public sealed class QuadrantShields
{
    public int Forward { get; set; } = 4;
    public int Starboard { get; set; } = 4;
    public int Aft { get; set; } = 2;
    public int Port { get; set; } = 2;

    /// <summary>Gets shield points for a quadrant by name.</summary>
    public int Get(Quadrant quadrant) => quadrant switch
    {
        Quadrant.Forward => Forward,
        Quadrant.Starboard => Starboard,
        Quadrant.Aft => Aft,
        Quadrant.Port => Port,
        _ => 0
    };

    /// <summary>
    /// Reduces shield points in a quadrant (floor at 0). Returns the overflow
    /// damage that punches through to the hull.
    /// </summary>
    public int Reduce(Quadrant quadrant, int amount)
    {
        int current = Get(quadrant);
        int absorbed = Math.Min(current, amount);
        Set(quadrant, current - absorbed);
        return amount - absorbed;
    }

    /// <summary>Restores shield points in a quadrant (capped at the given max).</summary>
    public void Restore(Quadrant quadrant, int amount, int max)
        => Set(quadrant, Math.Min(max, Get(quadrant) + amount));

    private void Set(Quadrant quadrant, int value)
    {
        switch (quadrant)
        {
            case Quadrant.Forward: Forward = value; break;
            case Quadrant.Starboard: Starboard = value; break;
            case Quadrant.Aft: Aft = value; break;
            case Quadrant.Port: Port = value; break;
        }
    }

    public QuadrantShields Clone() => new()
    {
        Forward = Forward,
        Starboard = Starboard,
        Aft = Aft,
        Port = Port
    };
}

/// <summary>The four shield quadrants of a starship.</summary>
public enum Quadrant
{
    Forward,
    Starboard,
    Aft,
    Port
}
