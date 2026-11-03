namespace Quadrant.RulesEngine;

/// <summary>
/// A hex cell on the combat grid, stored in axial coordinates (q, r).
/// Axial coordinates are orientation-agnostic: the same math works for both
/// pointy-top and flat-top rendering — only the pixel projection differs.
/// </summary>
public readonly record struct Hex(int Q, int R)
{
    /// <summary>
    /// The six neighbor directions in axial coordinates (pointy-top), ordered
    /// clockwise starting from North-West: NW, NE, E, SE, SW, W. This ordering
    /// is shared with <see cref="Facing"/> — a ship's facing index is the
    /// direction it points "forward".
    /// </summary>
    public static readonly (int DQ, int DR)[] Directions =
    [
        (0, -1),   // 0: North-West
        (+1, -1),  // 1: North-East
        (+1, 0),   // 2: East
        (0, +1),   // 3: South-East
        (-1, +1),  // 4: South-West
        (-1, 0)    // 5: West
    ];

    /// <summary>Number of discrete facing/movement directions.</summary>
    public const int DirectionCount = 6;

    /// <summary>Returns the neighbor in the given direction index (0-5).</summary>
    public Hex Neighbor(int direction)
    {
        var (dq, dr) = Directions[direction % DirectionCount];
        return new Hex(Q + dq, R + dr);
    }

    /// <summary>
    /// Returns the neighbor in the given <see cref="Facing"/> direction.
    /// </summary>
    public Hex Neighbor(Facing facing) => Neighbor((int)facing);

    /// <summary>
    /// Returns the clockwise direction index (0-5) from this hex to another,
    /// or -1 if they are the same hex. Used to determine which firing arc a
    /// target falls into relative to a ship's facing.
    /// </summary>
    public int DirectionTo(Hex other)
    {
        // Cube coordinates: x = q, z = r, y = -x - z
        int x = other.Q - Q;
        int z = other.R - R;
        int y = -x - z;

        // Match the cube direction against each of the six axial directions.
        for (int i = 0; i < DirectionCount; i++)
        {
            var (dq, dr) = Directions[i];
            int dy = -dq - dr;
            if (x == dq && y == dy && z == dr)
                return i;
        }

        // Not an adjacent hex — fall back to the nearest direction by angle.
        // Convert axial delta to pixel coordinates (pointy-top) and find the
        // closest of the six direction angles.
        double px = Math.Sqrt(3) * (x + z / 2.0);
        double py = 1.5 * z;
        double angleDeg = Math.Atan2(py, px) * 180.0 / Math.PI;

        // Direction angles: NW=-120, NE=-60, E=0, SE=60, SW=120, W=180
        double[] dirAngles = { -120, -60, 0, 60, 120, 180 };
        int bestDir = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < 6; i++)
        {
            double diff = Math.Abs(angleDeg - dirAngles[i]);
            if (diff > 180) diff = 360 - diff;
            if (diff < bestDist)
            {
                bestDist = diff;
                bestDir = i;
            }
        }
        return bestDir;
    }

    /// <summary>
    /// Hex distance (number of edges) between two hexes, computed via cube coordinates.
    /// </summary>
    public int DistanceTo(Hex other)
    {
        // Convert axial to cube: x = q, z = r, y = -x - z
        int dx = Q - other.Q;
        int dz = R - other.R;
        int dy = -dx - dz;
        return (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz)) / 2;
    }

    /// <summary>
    /// Moves this hex a number of steps in the given direction.
    /// </summary>
    public Hex Step(int direction, int steps)
    {
        var (dq, dr) = Directions[direction % DirectionCount];
        return new Hex(Q + dq * steps, R + dr * steps);
    }

    public override string ToString() => $"({Q},{R})";
}
