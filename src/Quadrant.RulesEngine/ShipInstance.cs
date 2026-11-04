namespace Quadrant.RulesEngine;

/// <summary>
/// The runtime state of a single starship during combat. Created from a
/// <see cref="ShipDefinition"/> via <see cref="ShipDefinition.Instantiate"/>.
/// Mutable: position, facing, hull, shields, and system conditions change as
/// the battle progresses.
/// </summary>
public sealed class ShipInstance
{
    /// <summary>Unique ID for this ship instance within a game (e.g. "ship-1").</summary>
    public required string InstanceId { get; init; }

    /// <summary>The static blueprint this ship was built from.</summary>
    public required ShipDefinition Definition { get; init; }

    /// <summary>
    /// Team/side identifier. In hotseat: 0 = Player 1, 1 = Player 2.
    /// In multiplayer: maps to a player or AI controller.
    /// </summary>
    public int TeamId { get; set; }

    // --- Position & orientation ---
    public Hex Position { get; set; }
    public Facing Facing { get; set; }

    // --- Health ---
    public int HullPoints { get; set; }
    public QuadrantShields Shields { get; set; } = new();

    /// <summary>True when hull reaches 0 (ship is disabled, drifting).</summary>
    public bool IsDisabled { get; set; }

    /// <summary>True when damage exceeded 2× max hull (ship is destroyed).</summary>
    public bool IsDestroyed { get; set; }

    // --- Critical damage tracking ---

    /// <summary>Total cumulative hull damage taken (for critical threshold checks).</summary>
    public int TotalHullDamageTaken { get; set; }

    /// <summary>Critical conditions on each system, if any.</summary>
    public SystemConditions Systems { get; } = new();

    /// <summary>Damaged systems as a serializable list (for API responses).</summary>
    public List<SystemDamageInfo> DamagedSystems =>
        Systems.DamagedSystems().Select(d => new SystemDamageInfo(d.System, d.Condition)).ToList();

    // --- Per-round transient state (reset each round) ---

    /// <summary>AC/TL modifier from stunts or actions this round (e.g. Evade +2).</summary>
    public int RoundAcTlModifier { get; set; }

    /// <summary>Whether the pilot chose to turn in place this round (affects AC if clumsy/poor).</summary>
    public bool TurnedInPlaceThisRound { get; set; }

    // --- Convenience accessors ---

    public string Name => Definition.Name;
    public int MaxHullPoints => Definition.HullPoints;
    public int Speed => Definition.Speed;
    public Maneuverability Maneuverability => Definition.Maneuverability;
    public IReadOnlyList<Weapon> Weapons => Definition.Weapons;

    /// <summary>
    /// Current Armor Class including round modifiers and critical condition penalties.
    /// </summary>
    public int CurrentAC
    {
        get
        {
            int ac = Definition.BaseAC + RoundAcTlModifier;
            // Turn in place penalty for clumsy/poor maneuverability.
            if (TurnedInPlaceThisRound)
                ac += Maneuverability switch
                {
                    Maneuverability.Clumsy => -4,
                    Maneuverability.Poor => -2,
                    _ => 0
                };
            return ac;
        }
    }

    /// <summary>
    /// Current Target Lock including round modifiers and critical condition penalties.
    /// </summary>
    public int CurrentTL
    {
        get
        {
            int tl = Definition.BaseTL + RoundAcTlModifier;
            if (TurnedInPlaceThisRound)
                tl += Maneuverability switch
                {
                    Maneuverability.Clumsy => -4,
                    Maneuverability.Poor => -2,
                    _ => 0
                };
            return tl;
        }
    }

    /// <summary>
    /// Determines which quadrant of this ship a target at the given position is in,
    /// relative to this ship's facing. Used for shield damage and weapon arc checks.
    ///
    /// The four quadrants partition the six directions (offset from facing):
    ///   Forward:   offset 0 (the facing edge)
    ///   Starboard: offsets 1, 2 (right side faces)
    ///   Aft:       offset 3 (the opposite edge)
    ///   Port:      offsets 4, 5 (left side faces)
    /// </summary>
    public Quadrant QuadrantOf(Hex targetPosition)
    {
        int offset = ArcOffsetTo(targetPosition);
        return offset switch
        {
            0 => Quadrant.Forward,
            1 or 2 => Quadrant.Starboard,
            3 => Quadrant.Aft,
            4 or 5 => Quadrant.Port,
            _ => Quadrant.Forward
        };
    }

    /// <summary>
    /// The angular offset (0-5) from this ship's facing to the given target position.
    /// 0 = directly ahead, 1 = one step clockwise, etc. Used for weapon arc checks.
    /// </summary>
    public int ArcOffsetTo(Hex targetPosition)
    {
        int dirToTarget = Position.DirectionTo(targetPosition);
        return ((dirToTarget - (int)Facing) % 6 + 6) % 6;
    }

    /// <summary>
    /// The continuous angle in degrees from this ship's facing to the target,
    /// normalized to [-180, 180]. 0 = directly ahead, positive = clockwise (starboard),
    /// negative = counter-clockwise (port). Used for inclusive arc boundary checks.
    /// </summary>
    public double AngleFromFacing(Hex targetPosition)
    {
        double? angleToTarget = Position.AngleTo(targetPosition);
        if (angleToTarget is null) return 0;

        // Facing direction angles: NW=-120, NE=-60, E=0, SE=60, SW=120, W=180
        double[] facingAngles = { -120, -60, 0, 60, 120, 180 };
        double facingAngle = facingAngles[(int)Facing];

        double diff = angleToTarget.Value - facingAngle;
        // Normalize to [-180, 180]
        while (diff > 180) diff -= 360;
        while (diff < -180) diff += 360;
        return diff;
    }

    /// <summary>Resets per-round transient state at the start of a new round.</summary>
    public void BeginRound()
    {
        RoundAcTlModifier = 0;
        TurnedInPlaceThisRound = false;
    }
}

/// <summary>A serializable snapshot of one damaged system (for API responses).</summary>
public sealed record SystemDamageInfo(ShipSystem System, SystemCondition Condition);
