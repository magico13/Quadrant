namespace Quadrant.RulesEngine;

/// <summary>
/// Result of validating a move action. If valid, contains the final position and facing.
/// If invalid, contains a human-readable reason.
/// </summary>
public sealed record MoveValidationResult(bool IsValid, Hex? FinalPosition, Facing? FinalFacing, string? Error)
{
    public static MoveValidationResult Ok(Hex pos, Facing facing) => new(true, pos, facing, null);
    public static MoveValidationResult Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// Validates and resolves movement for a ship during the Helm phase. Enforces:
/// - Total movement cannot exceed the ship's speed.
/// - Each turn requires traveling at least DistanceBetweenTurns hexes since the last turn.
/// - The ship cannot end on an occupied hex (but can pass through).
/// </summary>
public static class MovementValidator
{
    /// <summary>
    /// Validates a move action for a ship and returns the resulting position/facing.
    /// </summary>
    public static MoveValidationResult Validate(ShipInstance ship, MoveAction action, GameState state)
    {
        if (action.Segments.Count == 0)
            return MoveValidationResult.Ok(ship.Position, ship.Facing);

        // Each segment must move at least one hex.
        foreach (var seg in action.Segments)
        {
            if (seg.HexesToMove < 1)
                return MoveValidationResult.Fail("Each movement segment must be at least 1 hex.");
        }

        // Check total movement against speed.
        int totalHexes = action.TotalHexes;
        if (totalHexes > ship.Speed)
            return MoveValidationResult.Fail($"Movement of {totalHexes} exceeds speed of {ship.Speed}.");

        int minBetweenTurns = ship.Definition.DistanceBetweenTurns;
        Hex currentPos = ship.Position;
        Facing currentFacing = ship.Facing;
        int hexesSinceLastTurn = 0;

        foreach (var segment in action.Segments)
        {
            // Move the specified number of hexes in the current facing direction.
            for (int i = 0; i < segment.HexesToMove; i++)
                currentPos = currentPos.Neighbor((int)currentFacing);

            hexesSinceLastTurn += segment.HexesToMove;

            // Apply turn if specified.
            if (segment.NewFacing.HasValue)
            {
                var newFacing = segment.NewFacing.Value;
                if (newFacing != currentFacing)
                {
                    // Check distance between turns requirement.
                    if (hexesSinceLastTurn < minBetweenTurns && minBetweenTurns > 0)
                        return MoveValidationResult.Fail(
                            $"Must move at least {minBetweenTurns} hexes before turning " +
                            $"(only moved {hexesSinceLastTurn}).");

                    currentFacing = newFacing;
                    hexesSinceLastTurn = 0; // reset after a turn
                }
            }
        }

        // Check that the final position isn't occupied by another ship.
        var occupant = state.Ships.FirstOrDefault(s => s.InstanceId != ship.InstanceId && s.Position == currentPos);
        if (occupant is not null)
            return MoveValidationResult.Fail($"Cannot end movement on {occupant.Name} at {currentPos}.");

        return MoveValidationResult.Ok(currentPos, currentFacing);
    }
}
