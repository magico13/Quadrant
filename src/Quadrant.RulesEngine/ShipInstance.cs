using System.Text.Json.Serialization;

namespace Quadrant.RulesEngine;

/// <summary>
/// The runtime state of a single starship during combat. Created from a
/// <see cref="ShipDefinition"/> via <see cref="ShipDefinition.Instantiate"/>.
/// Mutable: position, facing, hull, shields, and system conditions change as
/// the battle progresses.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class ShipInstance
{
    /// <summary>Unique ID for this ship instance within a game (e.g. "ship-1").</summary>
    public required string InstanceId { get; init; }

    /// <summary>The static blueprint this ship was built from.</summary>
    public required ShipDefinition Definition { get; init; }

    /// <summary>
    /// Team/side identifier, independent of player identity.
    /// Player and controller assignments are separate future concerns.
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

    /// <summary>Facing when disabled, used as the direction of automatic drift.</summary>
    public Facing? DriftFacing { get; set; }
    /// <summary>Round in which this ship last drifted, including a blocked drift.</summary>
    public int? LastDriftRound { get; set; }

    // --- Critical damage tracking ---

    /// <summary>Total cumulative hull damage taken (for critical threshold checks).</summary>
    public int TotalHullDamageTaken { get; set; }

    /// <summary>Critical conditions on each system, if any.</summary>
    public SystemConditions Systems { get; } = new();

    /// <summary>Damaged systems as a serializable list (for API responses).</summary>
    public List<SystemDamageInfo> DamagedSystems =>
        Systems.DamagedSystems().Select(d => new SystemDamageInfo(d.System, d.Condition)).ToList();

    /// <summary>Received outcomes since Gunnery last began; also retained for disabled/destroyed ships.</summary>
    public List<ReceivedDamageEvent> DamageReport { get; } = [];

    // --- Per-round transient state (reset each round) ---

    /// <summary>AC/TL modifier from stunts or actions this round (e.g. Evade +2).</summary>
    public int RoundAcTlModifier { get; set; }

    /// <summary>Whether the pilot chose to turn in place this round (affects AC if clumsy/poor).</summary>
    public bool TurnedInPlaceThisRound { get; set; }

    /// <summary>A successful Barrel Roll, awaiting or active during Gunnery.</summary>
    public bool BarrelRolledThisRound { get; set; }
    public bool BarrelRollActive { get; set; }
    public string? FlybyTargetId { get; set; }
    public bool FlybySucceeded { get; set; }
    public bool FlybyCrossed { get; set; }
    public FiringArc? FlybyFiringArc { get; set; }

    public bool CanBarrelRoll => Definition.Size?.ToLowerInvariant() is "tiny" or "small" or "medium" or "large";

    // --- Convenience accessors ---

    public string Name => Definition.Name;
    public int MaxHullPoints => Definition.HullPoints;
    public bool DivertEngines { get; set; }
    public bool DivertWeapons { get; set; }
    public bool DivertScience { get; set; }
    public HashSet<string> EngineeringActionsUsed { get; } = [];
    public int Speed => Definition.Speed + (DivertEngines ? 2 : 0);
    public int SpeedPilotingAdjustment => ShipDefinition.PilotingModifierForSpeed(Speed) - Definition.SpeedPilotingModifier;
    public Maneuverability Maneuverability => Definition.Maneuverability;
    [JsonIgnore]
    public IReadOnlyList<Weapon> Weapons => BarrelRollActive
        ? Definition.Weapons.Select(w => w.Arc switch
        {
            FiringArc.Port => w with { Arc = FiringArc.Starboard },
            FiringArc.Starboard => w with { Arc = FiringArc.Port },
            _ => w
        }).ToList()
        : Definition.Weapons;

    public static readonly FiringArc[] StandardWeaponArcs = [FiringArc.Forward, FiringArc.Starboard, FiringArc.Aft, FiringArc.Port];

    /// <summary>Stored arcs follow the installed mounts, including during a Barrel Roll.</summary>
    public FiringArc InstalledWeaponArc(FiringArc visibleArc) => BarrelRollActive ? visibleArc switch
    {
        FiringArc.Port => FiringArc.Starboard,
        FiringArc.Starboard => FiringArc.Port,
        _ => visibleArc
    } : visibleArc;

    public IEnumerable<FiringArc> OccupiedWeaponArcs => StandardWeaponArcs.Where(arc =>
        Definition.Weapons.Any(w => w.Arc == arc || w.Arc == FiringArc.AllArcs));

    public FiringArc WeaponConditionArc(Weapon weapon, Hex target, FiringArc? firingArc = null)
    {
        var visibleArc = weapon.Arc != FiringArc.AllArcs ? weapon.Arc : firingArc ?? PhysicalQuadrantOf(target) switch
        {
            Quadrant.Forward => FiringArc.Forward,
            Quadrant.Starboard => FiringArc.Starboard,
            Quadrant.Aft => FiringArc.Aft,
            _ => FiringArc.Port
        };
        return InstalledWeaponArc(visibleArc);
    }

    public int WeaponActionPenalty(Weapon weapon, Hex target, FiringArc? firingArc = null)
        => Systems.GetWeaponActionPenalty(WeaponConditionArc(weapon, target, firingArc));

    /// <summary>Current roles in the same order as the ship's crew definitions.</summary>
    public List<string> CrewRoles { get; } = [];
    public HashSet<int> CrewActionsUsed { get; } = [];
    public HashSet<int> CrewMinorActionsUsed { get; } = [];
    public bool GunnerActionTaken { get; set; }
    /// <summary>Unspent Fire at Will shots keyed by the gunner who activated them.</summary>
    public Dictionary<int, int> FireAtWillShots { get; } = new();
    /// <summary>Null before a Maneuver attempt; otherwise its result for this round.</summary>
    public bool? ManeuverSucceeded { get; set; }

    public int AvailableGunner()
    {
        InitializeCrewRoles();
        for (int i = 0; i < CrewRoles.Count; i++)
            if (CrewRoles[i] == "Gunner" && !CrewActionsUsed.Contains(i)) return i;
        return -1;
    }

    public int RemainingGunneryActions()
    {
        InitializeCrewRoles();
        if (Definition.Crew.Count == 0) return Weapons.Count;
        int gunners = CrewRoles.Select((role, i) => (role, i)).Count(x => x.role == "Gunner" && !CrewActionsUsed.Contains(x.i));
        int shots = gunners + FireAtWillShots.Values.Sum();
        return shots > 0 ? shots : !GunnerActionTaken && AvailableMinorCrew() >= 0 ? 1 : 0;
    }

    public int AvailableMinorCrew()
    {
        InitializeCrewRoles();
        for (int i = 0; i < CrewRoles.Count; i++)
            if (!CrewMinorActionsUsed.Contains(i)) return i;
        return -1;
    }

    /// <summary>Glide is available without a pilot or when wrecked engines prevent Fly.</summary>
    public int AvailableGlideCrew()
    {
        InitializeCrewRoles();
        int pilot = CrewRoles.IndexOf("Pilot");
        if (pilot >= 0)
        {
            if (Systems.GetEffective(ShipSystem.Engines) != SystemCondition.Wrecked || CrewActionsUsed.Contains(pilot)) return -1;
            if (!CrewMinorActionsUsed.Contains(pilot)) return pilot;
        }
        return AvailableMinorCrew();
    }

    public int CrewBonus(int index, string skill, string role)
    {
        var crew = Definition.Crew[index];
        if (crew.Skills.TryGetValue(skill, out int own)) return own;
        var peer = Definition.Crew.FirstOrDefault(c => c.Role == role && c.Skills.ContainsKey(skill));
        if (peer is not null) return peer.Skills[skill];
        return skill == "gunnery" ? crew.Ranks + 3 : 4 + (3 * crew.Ranks / 2);
    }

    public void InitializeCrewRoles()
    {
        if (CrewRoles.Count == 0)
            CrewRoles.AddRange(Definition.Crew.Select(c => c.Role));
    }

    /// <summary>Remaining Limited Fire uses per mounted weapon; null means unlimited.</summary>
    public List<int?> WeaponUsesRemaining { get; } = [];

    public void InitializeWeaponUses()
    {
        if (WeaponUsesRemaining.Count == Weapons.Count) return;
        WeaponUsesRemaining.Clear();
        WeaponUsesRemaining.AddRange(Weapons.Select(w => w.LimitedFireUses > 0 ? w.LimitedFireUses : (int?)null));
    }

    public bool HasWeaponUses(int weaponIndex)
    {
        InitializeWeaponUses();
        return weaponIndex >= 0 && weaponIndex < WeaponUsesRemaining.Count && WeaponUsesRemaining[weaponIndex] != 0;
    }

    public bool ConsumeWeaponUse(int weaponIndex)
    {
        if (!HasWeaponUses(weaponIndex)) return false;
        if (WeaponUsesRemaining[weaponIndex] is int remaining)
            WeaponUsesRemaining[weaponIndex] = remaining - 1;
        return true;
    }

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
        => ShieldQuadrantFor(PhysicalQuadrantOf(targetPosition));

    public Quadrant ShieldQuadrantFor(Quadrant quadrant)
    {
        return BarrelRollActive ? quadrant switch
        {
            Quadrant.Port => Quadrant.Starboard,
            Quadrant.Starboard => Quadrant.Port,
            _ => quadrant
        } : quadrant;
    }

    /// <summary>The spatial quadrant, independent of which shield protects it.</summary>
    public Quadrant PhysicalQuadrantOf(Hex targetPosition)
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
        CrewActionsUsed.Clear();
        ManeuverSucceeded = null;
        CrewMinorActionsUsed.Clear();
        GunnerActionTaken = false;
        FireAtWillShots.Clear();
        DivertEngines = DivertWeapons = DivertScience = false;
        EngineeringActionsUsed.Clear();
        Systems.HeldSystem = null;
        Systems.HeldWeaponArc = null;
        RoundAcTlModifier = 0;
        TurnedInPlaceThisRound = false;
        BarrelRolledThisRound = BarrelRollActive = false;
        FlybyTargetId = null;
        FlybySucceeded = FlybyCrossed = false;
        FlybyFiringArc = null;
    }
}

/// <summary>A serializable snapshot of one damaged system (for API responses).</summary>
public sealed record SystemDamageInfo(ShipSystem System, SystemCondition Condition);
