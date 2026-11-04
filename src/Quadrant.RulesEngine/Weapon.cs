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

    /// <summary>
    /// Range increment in hexes. The weapon takes a –2 penalty per increment
    /// (or fraction thereof) beyond the first. Maximum fireable distance is
    /// 10 × this value.
    /// </summary>
    public int RangeIncrement { get; init; } = 6;

    /// <summary>Maximum fireable distance in hexes (10 range increments).</summary>
    public int MaxFireRange => RangeIncrement * 10;

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
    /// Checks whether this weapon can target a ship at the given range and angle.
    /// Angle is in degrees from the ship's facing: 0 = ahead, positive = starboard,
    /// negative = port. Boundaries are inclusive so hexes on an arc edge belong to both arcs.
    /// </summary>
    public bool CanTarget(int range, double angleFromFacing)
    {
        if (range < MinRange || range > MaxFireRange) return false;
        return ArcCovers(angleFromFacing);
    }

    /// <summary>
    /// Computes the cumulative –2 range penalty for firing at the given distance.
    /// The first range increment is free; each additional increment (or fraction
    /// thereof) adds –2. Example: with a 6-hex increment, range 7–12 → –2, 13–18 → –4.
    /// </summary>
    public int RangePenalty(int range)
    {
        if (range <= RangeIncrement) return 0;
        // Number of increments beyond the first (ceiling division)
        int extraIncrements = (int)Math.Ceiling((double)(range - RangeIncrement) / RangeIncrement);
        return -2 * extraIncrements;
    }

    /// <summary>
    /// Determines whether this weapon's arc covers the given angle from facing.
    /// Forward/Aft are 60° cones; Port/Starboard are 120° arcs.
    /// Boundaries are inclusive: a hex at exactly ±30° is in both adjacent arcs.
    /// A small epsilon guards against floating-point drift in atan2 results.
    /// </summary>
    private const double ArcEps = 0.01; // degrees

    private bool ArcCovers(double angle)
    {
        return Arc switch
        {
            FiringArc.AllArcs => true,
            FiringArc.Forward => angle >= -30 - ArcEps && angle <= 30 + ArcEps,
            FiringArc.Starboard => angle >= 30 - ArcEps && angle <= 150 + ArcEps,
            FiringArc.Aft => angle >= 150 - ArcEps || angle <= -150 + ArcEps,
            FiringArc.Port => angle >= -150 - ArcEps && angle <= -30 + ArcEps,
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
