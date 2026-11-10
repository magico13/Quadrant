namespace Quadrant.RulesEngine;

/// <summary>A frozen damage state, independent of subsequent repairs or round resets.</summary>
public sealed record DamageState(int Hull, Dictionary<Quadrant, int> Shields,
    Dictionary<ShipSystem, SystemCondition> Systems, bool IsDisabled, bool IsDestroyed)
{
    public Dictionary<FiringArc, SystemCondition> WeaponArcs { get; init; } = new();
    public static DamageState Capture(ShipInstance ship) => new(ship.HullPoints,
        new() { [Quadrant.Forward] = ship.Shields.Forward, [Quadrant.Starboard] = ship.Shields.Starboard,
            [Quadrant.Aft] = ship.Shields.Aft, [Quadrant.Port] = ship.Shields.Port },
        Enum.GetValues<ShipSystem>().ToDictionary(s => s, ship.Systems.Get), ship.IsDisabled, ship.IsDestroyed)
        { WeaponArcs = ship.OccupiedWeaponArcs.ToDictionary(a => a, ship.Systems.GetWeaponArc) };
}

/// <summary>Received combat outcomes in resolution order, retained until the next Gunnery begins.</summary>
public sealed record ReceivedDamageEvent(int Round, CombatPhase Phase, string AttackerId, string AttackerName,
    string Weapon, string? ProjectileId, bool ShotDown, string? DefenseWeapon, Quadrant? Quadrant,
    int RawDamage, int ShieldDamage, int HullDamage, int StoppedDamage,
    DamageState Before, DamageState After, IReadOnlyList<CriticalDamageEffect> CriticalEffects)
{
    public FiringArc? DefenseWeaponArc { get; init; }
}
