using Quadrant.RulesEngine;

namespace Quadrant.Server.Sessions;

/// <summary>
/// Static ship definitions based on published Starfinder Society / Pact Worlds ships.
/// In a future version these would be loaded from a database or JSON files.
/// </summary>
public static class ShipCatalog
{
    /// <summary>Tiny racer — fast, fragile drone/explorer (Pact Worlds pg. 154).</summary>
    public static readonly ShipDefinition CompentAev = new()
    {
        Id = "compent-aev",
        Name = "CompEnt AEV",
        Tier = 2,
        Speed = 8,
        Maneuverability = Maneuverability.Perfect,
        HullPoints = 20,
        DamageThreshold = 0,
        CriticalThreshold = 4,
        BaseAC = 15,
        BaseTL = 12,
        Shields = new QuadrantShields { Forward = 3, Starboard = 2, Aft = 3, Port = 2 },
        Weapons = new List<Weapon>
        {
            new() { Name = "Mining Laser", Arc = FiringArc.Forward, MinRange = 1, RangeIncrement = 5, Damage = DamageExpression.From("2d6"), Type = WeaponType.DirectFire }
        }
    };

    /// <summary>Medium explorer — balanced all-rounder (Society Guide v7.02 pg. 5).</summary>
    public static readonly ShipDefinition Pegasus = new()
    {
        Id = "pegasus",
        Name = "Pegasus",
        Tier = 2,
        Speed = 10,
        Maneuverability = Maneuverability.Good,
        HullPoints = 55,
        DamageThreshold = 0,
        CriticalThreshold = 11,
        BaseAC = 12,
        BaseTL = 12,
        Shields = new QuadrantShields { Forward = 10, Starboard = 10, Aft = 10, Port = 10 },
        Weapons = new List<Weapon>
        {
            new() { Name = "Light Laser Cannon", Arc = FiringArc.Forward, MinRange = 1, RangeIncrement = 5, Damage = DamageExpression.From("2d4"), Type = WeaponType.DirectFire },
            new() { Name = "Light Laser Cannon", Arc = FiringArc.Port, MinRange = 1, RangeIncrement = 5, Damage = DamageExpression.From("2d4"), Type = WeaponType.DirectFire },
            new() { Name = "Light Laser Cannon", Arc = FiringArc.Starboard, MinRange = 1, RangeIncrement = 5, Damage = DamageExpression.From("2d4"), Type = WeaponType.DirectFire },
            new() { Name = "Coilgun", Arc = FiringArc.AllArcs, MinRange = 1, RangeIncrement = 20, Damage = DamageExpression.From("4d4"), Type = WeaponType.DirectFire }
        }
    };

    /// <summary>Large destroyer — slow, heavily armored, big guns (Society Guide v7.02 pg. 8).</summary>
    public static readonly ShipDefinition Gorgon = new()
    {
        Id = "gorgon",
        Name = "Gorgon",
        Tier = 2,
        Speed = 4,
        Maneuverability = Maneuverability.Average,
        HullPoints = 150,
        DamageThreshold = 0,
        CriticalThreshold = 30,
        BaseAC = 10,
        BaseTL = 10,
        Shields = new QuadrantShields { Forward = 5, Starboard = 5, Aft = 5, Port = 5 },
        Weapons = new List<Weapon>
        {
            new() { Name = "Heavy Laser Cannon", Arc = FiringArc.Forward, MinRange = 1, RangeIncrement = 10, Damage = DamageExpression.From("4d8"), Type = WeaponType.DirectFire }
        }
    };

    /// <summary>All available ship definitions for the roster.</summary>
    public static IReadOnlyList<ShipDefinition> All => [CompentAev, Pegasus, Gorgon];

    /// <summary>
    /// Creates the default hotseat game:
    /// Team 0 (blue): Pegasus + AEV facing East.
    /// Team 1 (orange): Gorgon facing West.
    /// </summary>
    public static List<ShipInstance> CreateDefaultShips()
    {
        return new List<ShipInstance>
        {
            Pegasus.Instantiate("p1-1", new Hex(-4, -1), Facing.East, teamId: 0),
            CompentAev.Instantiate("p1-2", new Hex(-3, 1), Facing.East, teamId: 0),
            Gorgon.Instantiate("p2-1", new Hex(4, 0), Facing.West, teamId: 1)
        };
    }
}
