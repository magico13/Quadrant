namespace Quadrant.RulesEngine;

/// <summary>
/// The six discrete directions a starship can face on a pointy-top hex grid.
/// Ordered clockwise starting from North-West (index 0), matching
/// <see cref="Hex.Directions"/>. A ship's facing determines its forward arc,
/// shield quadrants, and movement direction.
/// </summary>
public enum Facing
{
    NorthWest = 0,
    NorthEast = 1,
    East = 2,
    SouthEast = 3,
    SouthWest = 4,
    West = 5
}

/// <summary>
/// A starship's maneuverability rating. Determines the minimum number of hexes
/// it must travel between turns, and its modifier to Piloting checks.
/// </summary>
public enum Maneuverability
{
    Clumsy,   // 4 hexes between turns, -2 Piloting
    Poor,     // 3 hexes between turns, -1 Piloting
    Average,  // 2 hexes between turns,  0 Piloting
    Good,     // 1 hex between turns,   +1 Piloting
    Perfect   // 0 hexes between turns, +2 Piloting
}

/// <summary>
/// Firing arcs. A weapon's arc determines which directions it can fire from.
/// </summary>
public enum FiringArc
{
    Forward,
    Port,
    Starboard,
    Aft,
    AllArcs   // e.g. turrets
}
