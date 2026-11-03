namespace Quadrant.RulesEngine;

/// <summary>
/// A starship weapon mount. Each weapon can be fired once per round (with rare
/// exceptions for linked weapons, deferred to a later version).
/// </summary>
public sealed record Weapon
{
    /// <summary>Display name, e.g. "Autocannon".</summary>
    public required string Name { get; init; }

    /// <summary>Which arc this weapon fires from.</summary>
    public required FiringArc Arc { get; init; }

    /// <summary>Minimum range in hexes (inclusive).</summary>
    public int MinRange { get; init; } = 1;

    /// <summary>Maximum range in hexes (inclusive).</summary>
    public int MaxRange { get; init; } = 6;

    /// <summary>The damage expression rolled on a hit.</summary>
    public required DamageExpression Damage { get; init; }

    /// <summary>Whether this is a direct-fire or tracking weapon.</summary>
    public WeaponType Type { get; init; } = WeaponType.DirectFire;

    /// <summary>
    /// Whether this weapon can fire at close range (range 1). Most weapons can,
    /// but some (like certain missiles) have a minimum range of 2.
    /// </summary>
    public bool CanFireAtCloseRange => MinRange <= 1;

    /// <summary>
    /// Checks whether this weapon can target a ship at the given range and arc offset.
    /// </summary>
    public bool CanTarget(int range, int arcOffset)
    {
        if (range < MinRange || range > MaxRange) return false;
        return ArcCovers(arcOffset);
    }

    /// <summary>
    /// Determines whether this weapon's arc covers the given angular offset
    /// from the ship's facing. Offset 0 = directly forward, ±1 = adjacent arc,
    /// ±2 = next, ±3 = directly aft.
    /// </summary>
    private bool ArcCovers(int offset)
    {
        int o = ((offset % 6) + 6) % 6; // normalize to 0-5
        return Arc switch
        {
            FiringArc.AllArcs => true,
            FiringArc.Forward => o is 0 or 1 or 5,       // forward + adjacent (3 hexes wide)
            FiringArc.Port => o is 4 or 5,               // port side (2 hexes)
            FiringArc.Starboard => o is 1 or 2,          // starboard side (2 hexes)
            FiringArc.Aft => o is 2 or 3 or 4,           // aft + adjacent (3 hexes wide)
            _ => false
        };
    }
}

/// <summary>
/// The type of weapon, determining how its attack is resolved.
/// </summary>
public enum WeaponType
{
    /// <summary>Resolved against target AC. Hits immediately.</summary>
    DirectFire,

    /// <summary>Resolved against target TL. Projectile homes each round (deferred).</summary>
    Tracking
}
