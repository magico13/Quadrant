namespace Quadrant.RulesEngine;

/// <summary>
/// The result of resolving a single weapon attack.
/// </summary>
public sealed record AttackResult(
    bool IsHit,
    int DamageDealt,
    int ShieldDamage,
    int HullDamage,
    bool CriticalScored,
    ShipSystem? CriticalSystem,
    SystemCondition? NewCriticalCondition,
    string Description)
{
    public static AttackResult Miss(int checkTotal, int targetValue, string description)
        => new(false, 0, 0, 0, false, null, null, description);

    public static AttackResult Success(int checkTotal, int damage, int shieldDmg, int hullDmg, bool crit, ShipSystem? system, SystemCondition? condition, string description)
        => new(true, damage, shieldDmg, hullDmg, crit, system, condition, description);
}

/// <summary>
/// Resolves attacks and applies damage during the Gunnery phase. This is where
/// gunnery checks are made, AC/TL compared, shields depleted, hull damaged,
/// and critical damage scored.
/// </summary>
public static class CombatResolver
{
    /// <summary>
    /// Resolves a single weapon attack from attacker to target.
    /// Returns the result and mutates the target's state (shields, hull, systems).
    /// </summary>
    public static AttackResult ResolveAttack(ShipInstance attacker, ShipInstance target, Weapon weapon, IDiceRoller dice)
    {
        int range = attacker.Position.DistanceTo(target.Position);
        double angle = attacker.AngleFromFacing(target.Position);

        // Check if the weapon can target this ship.
        if (!weapon.CanTarget(range, angle))
            return AttackResult.Miss(0, 0, $"Cannot target: out of range or wrong arc.");

        // Determine what we're checking against: AC for direct fire, TL for tracking.
        int defenseValue = weapon.Type switch
        {
            WeaponType.DirectFire => target.CurrentAC,
            WeaponType.Tracking => target.CurrentTL,
            _ => target.CurrentAC
        };
        string defenseName = weapon.Type == WeaponType.Tracking ? "TL" : "AC";

        // Make the gunnery check.
        int roll = dice.RollD20();
        int bonus = ComputeGunnerBonus(attacker);
        if (bonus == int.MinValue)
            return AttackResult.Miss(0, 0, "Weapons array is wrecked — attack auto-fails.");

        int checkTotal = roll + bonus;

        if (checkTotal < defenseValue)
            return AttackResult.Miss(checkTotal, defenseValue,
                $"Gunnery {roll}+{bonus}={checkTotal} vs {defenseName} {defenseValue} — MISS.");

        // Hit! Roll damage using the shared dice roller.
        int rawDamage = weapon.Damage.Roll(dice);

        // Determine which quadrant of the target is hit.
        Quadrant quadrant = target.QuadrantOf(attacker.Position);

        // Apply damage: shields first, then hull.
        int shieldDamage = 0;
        int hullDamage = 0;

        int shieldOverflow = target.Shields.Reduce(quadrant, rawDamage);
        shieldDamage = rawDamage - shieldOverflow;

        // Hull damage with Damage Threshold check.
        if (shieldOverflow > 0)
        {
            if (shieldOverflow <= target.Definition.DamageThreshold)
            {
                hullDamage = 0; // absorbed by damage threshold
            }
            else
            {
                hullDamage = shieldOverflow;
            }
        }

        // Apply hull damage.
        target.HullPoints -= hullDamage;
        target.TotalHullDamageTaken += hullDamage;

        // Check for destruction (damage > 2× max hull).
        if (target.TotalHullDamageTaken > 2 * target.Definition.HullPoints)
            target.IsDestroyed = true;
        else if (target.HullPoints <= 0)
            target.IsDisabled = true;

        // Check for critical damage.
        bool naturalCrit = roll == 20;
        bool thresholdCrossed = false;
        int critThreshold = target.Definition.CriticalThreshold;
        if (critThreshold > 0 && hullDamage > 0)
        {
            int prevMultiple = (target.TotalHullDamageTaken - hullDamage) / critThreshold;
            int newMultiple = target.TotalHullDamageTaken / critThreshold;
            thresholdCrossed = newMultiple > prevMultiple;
        }

        // Critical only if damage reached the hull (not just shields).
        bool criticalScored = (naturalCrit || thresholdCrossed) && hullDamage > 0;

        ShipSystem? critSystem = null;
        SystemCondition? newCondition = null;

        if (criticalScored)
        {
            // Roll d% to determine which system is hit.
            int percentRoll = dice.RollPercent();
            var systemHit = SystemFromPercent(percentRoll, target);
            critSystem = systemHit;
            newCondition = target.Systems.ApplyCritical(systemHit);
        }

        string desc = criticalScored
            ? $"Gunnery {roll}+{bonus}={checkTotal} vs {defenseName} {defenseValue} — HIT! {rawDamage} dmg ({shieldDamage} shield, {hullDamage} hull). CRITICAL: {critSystem} → {newCondition}!"
            : $"Gunnery {roll}+{bonus}={checkTotal} vs {defenseName} {defenseValue} — HIT! {rawDamage} dmg ({shieldDamage} shield, {hullDamage} hull).";

        return AttackResult.Success(checkTotal, rawDamage, shieldDamage, hullDamage, criticalScored, critSystem, newCondition, desc);
    }

    /// <summary>
    /// Computes the attacker's gunnery bonus (base attack + Dexterity + system
    /// condition penalties). Returns <see cref="int.MinValue"/> when the weapons
    /// array is wrecked and the attack auto-fails.
    /// </summary>
    public static int ComputeGunnerBonus(ShipInstance attacker)
    {
        // A wrecked weapons array auto-fails all gunnery actions. Check the sentinel
        // before adding it to the base bonus, or integer overflow would mask it.
        if (attacker.Systems.GetActionPenalty(ShipSystem.WeaponsArray) == int.MinValue)
            return int.MinValue;

        int bonus = attacker.Definition.GunnerBaseAttack
                  + attacker.Definition.DexterityModifier
                  + attacker.Systems.GetActionPenalty(ShipSystem.WeaponsArray);

        // Power core global penalty.
        bonus += attacker.Systems.PowerCoreGlobalPenalty;
        return bonus;
    }

    /// <summary>
    /// Computes the probability (0-100, rounded to nearest integer) that a natural
    /// d20 roll plus the gunner bonus meets or exceeds the target's defense value.
    /// A wrecked weapons array (bonus == int.MinValue) yields 0%.
    /// </summary>
    public static int HitChancePercent(int bonus, int defenseValue)
    {
        if (bonus == int.MinValue) return 0;
        int need = defenseValue - bonus; // smallest natural roll that hits
        if (need <= 1) return 100;
        if (need > 20) return 0;
        return (int)Math.Round((21.0 - need) / 20.0 * 100);
    }

    /// <summary>
    /// Maps a d% roll to the affected system per the Starfinder critical damage table.
    /// 1-10: Life Support, 11-30: Sensors, 31-60: Weapons Array, 61-80: Engines, 81-100: Power Core.
    /// If a system is already wrecked, cascades to the next one down.
    /// </summary>
    public static ShipSystem SystemFromPercent(int percent, ShipInstance target)
    {
        var order = new[]
        {
            (Max: 10, System: ShipSystem.LifeSupport),
            (Max: 30, System: ShipSystem.Sensors),
            (Max: 60, System: ShipSystem.WeaponsArray),
            (Max: 80, System: ShipSystem.Engines),
            (Max: 100, System: ShipSystem.PowerCore)
        };

        foreach (var entry in order)
        {
            if (percent <= entry.Max)
            {
                // If this system is already wrecked, cascade to next.
                if (target.Systems.Get(entry.System) != SystemCondition.Wrecked)
                    return entry.System;
            }
        }

        // All systems wrecked — hit the power core (or crew damage in full rules).
        return ShipSystem.PowerCore;
    }
}
