namespace Quadrant.RulesEngine;

/// <summary>
/// A turn in a ship's movement path: travel N hexes, then optionally change facing.
/// The engine validates that each segment meets the maneuverability distance-between-turns.
/// </summary>
public sealed record TurnSegment(int HexesToMove, Facing? NewFacing)
{
    /// <summary>A segment with no turn (just straight movement).</summary>
    public static TurnSegment Straight(int hexes) => new(hexes, null);

    /// <summary>A segment that moves then turns to the given facing.</summary>
    public static TurnSegment MoveAndTurn(int hexes, Facing facing) => new(hexes, facing);
}

/// <summary>
/// A player's intent for the Helm phase: how they want their ship to move this round.
/// In v1 this is always a standard movement (no stunts). The segments describe the
/// path as a sequence of straight-line moves with optional turns between them.
/// </summary>
public sealed record MoveAction(IReadOnlyList<TurnSegment> Segments)
{
    /// <summary>Total hexes this move covers.</summary>
    public int TotalHexes => Segments.Sum(s => s.HexesToMove);

    /// <summary>The final facing after all segments are applied (or the original if no turns).</summary>
    public Facing? FinalFacing => Segments.LastOrDefault(s => s.NewFacing.HasValue)?.NewFacing;
}

/// <summary>
/// A player's intent for the Gunnery phase: fire a specific weapon at a target.
/// </summary>
public sealed record FireAction(int WeaponIndex, string TargetShipId, Quadrant? TargetQuadrant = null);

/// <summary>
/// A player chooses to take no action this phase (e.g. skip firing, or a ship
/// with no weapons).
/// </summary>
public sealed record SkipAction;
