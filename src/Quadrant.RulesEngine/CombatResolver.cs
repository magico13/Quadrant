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

public sealed record AttackCheckResult(
    bool IsHit, int Roll, int Modifier, int CheckTotal, int DefenseValue,
    string DefenseName, int DamageRolled, string Description);

public sealed record CriticalDamageEffect(ShipSystem? System, SystemCondition? Condition);
public sealed record DamageResolution(int ShieldDamage, int HullDamage, bool CriticalScored,
    ShipSystem? CriticalSystem, SystemCondition? NewCriticalCondition, string Description)
{
    public IReadOnlyList<CriticalDamageEffect> CriticalEffects { get; init; } = [];
}

/// <summary>
/// Resolves attacks and applies damage during the Gunnery phase. This is where
/// gunnery checks are made, AC/TL compared, shields depleted, hull damaged,
/// and critical damage scored.
/// </summary>
public static class CombatResolver
{
    public static AttackResult ResolveProjectileImpact(TrackingProjectile projectile, ShipInstance target, IDiceRoller dice, int roll, int checkBonus, int defenseValue, Hex impactFrom, bool divertWeapons = false, ShipSystem? targetedSystem = null)
    {
        int rawDamage = projectile.Weapon.Damage.Roll(dice, divertWeapons);
        Quadrant quadrant = projectile.FlybyQuadrant is { } selected ? target.ShieldQuadrantFor(selected) : target.QuadrantOf(impactFrom);
        var damage = ApplyDamage(target, quadrant, rawDamage, roll, dice, targetedSystem);
        int total = roll + checkBonus;
        string desc = $"Tracking check {FormatCheck(roll, checkBonus, total)} vs TL {defenseValue} — IMPACT! {damage.Description}.";
        return AttackResult.Success(total, rawDamage, damage.ShieldDamage, damage.HullDamage,
            damage.CriticalScored, damage.CriticalSystem, damage.NewCriticalCondition, desc);
    }

    public static AttackCheckResult RollAttack(ShipInstance attacker, ShipInstance target, Weapon weapon, IDiceRoller dice,
        int? gunnerBonus = null, int? defense = null, bool rollDamage = true, bool flyby = false)
    {
        int range = flyby ? 1 : attacker.Position.DistanceTo(target.Position);
        double angle = flyby ? weapon.Arc switch
        {
            FiringArc.Port => -90,
            FiringArc.Starboard => 90,
            FiringArc.Aft => 180,
            _ => 0
        } : attacker.AngleFromFacing(target.Position);
        int defenseValue = defense ?? (weapon.Type == WeaponType.Tracking ? target.CurrentTL : target.CurrentAC);
        string defenseName = weapon.Type == WeaponType.Tracking ? "TL" : "AC";
        if (!weapon.CanTarget(range, angle))
            return new(false, 0, 0, 0, defenseValue, defenseName, 0, "Cannot target: out of range or wrong arc.");

        int bonus = gunnerBonus ?? ComputeGunnerBonus(attacker);
        if (bonus == int.MinValue)
            return new(false, 0, 0, 0, defenseValue, defenseName, 0, "Weapons array is wrecked — attack auto-fails.");
        bonus += weapon.RangePenalty(range) + weapon.ArcPenalty(angle);
        int roll = dice.RollD20();
        int total = roll + bonus;
        bool hit = roll == 20 || (roll != 1 && total >= defenseValue);
        if (!hit)
        {
            return new(false, roll, bonus, total, defenseValue, defenseName, 0,
                $"Gunnery {FormatCheck(roll, bonus, total)} vs {defenseName} {defenseValue} — MISS.");
        }

        bool shouldRollDamage = rollDamage && weapon.Type != WeaponType.Tracking;
        int damage = shouldRollDamage ? weapon.Damage.Roll(dice) : 0;
        string natural = roll == 20 && total < defenseValue ? " (natural 20 hits automatically)" : "";
        return new(true, roll, bonus, total, defenseValue, defenseName, damage,
            $"Gunnery {FormatCheck(roll, bonus, total)} vs {defenseName} {defenseValue} — HIT{natural}." + (shouldRollDamage ? $" {damage} damage rolled." : ""));
    }

    public static DamageResolution ApplyDamage(ShipInstance target, Quadrant quadrant, int rawDamage, int attackRoll, IDiceRoller dice, ShipSystem? targetedSystem = null)
    {
        int overflow = target.Shields.Reduce(quadrant, rawDamage);
        int shieldDamage = rawDamage - overflow;
        int hullDamage = overflow > target.Definition.DamageThreshold ? overflow : 0;
        target.HullPoints -= hullDamage;
        target.TotalHullDamageTaken += hullDamage;
        if (target.TotalHullDamageTaken > 2 * target.Definition.HullPoints) target.IsDestroyed = true;
        else if (target.HullPoints <= 0) target.IsDisabled = true;

        int ct = target.Definition.CriticalThreshold;
        int thresholdCrossings = 0;
        if (ct > 0 && hullDamage > 0)
        {
            int prior = Math.Max(0, target.TotalHullDamageTaken - hullDamage - 1) / ct;
            int current = Math.Max(0, target.TotalHullDamageTaken - 1) / ct;
            thresholdCrossings = current - prior;
        }
        int criticalCount = hullDamage > 0 ? thresholdCrossings + (attackRoll == 20 || (attackRoll == 19 && targetedSystem is not null) ? 1 : 0) : 0;
        var effects = new List<CriticalDamageEffect>();
        for (int i = 0; i < criticalCount; i++)
        {
            var system = i == 0 && targetedSystem is not null
                ? NextAvailableSystem(targetedSystem.Value, target)
                : SystemFromPercent(dice.RollPercent(), target);
            effects.Add(system is { } available
                ? new CriticalDamageEffect(available, target.Systems.ApplyCritical(available))
                : new CriticalDamageEffect(null, null));
        }
        string description = $"{rawDamage} dmg ({shieldDamage} shield, {hullDamage} hull)";
        foreach (var effect in effects)
            description += effect.System is null
                ? $". CRITICAL: crew injury requires manual resolution ({hullDamage} damage, DC 20 Reflex for half)!"
                : $". CRITICAL: {effect.System} → {effect.Condition}!";
        return new DamageResolution(shieldDamage, hullDamage, effects.Count > 0, effects.FirstOrDefault()?.System,
            effects.FirstOrDefault()?.Condition, description) { CriticalEffects = effects };
    }

    public static string FormatCheck(int roll, int modifier, int total)
        => modifier < 0 ? $"{roll}-{Math.Abs(modifier)}={total}" : $"{roll}+{modifier}={total}";

    /// <summary>
    /// Resolves a single weapon attack from attacker to target.
    /// Returns the result and mutates the target's state (shields, hull, systems).
    /// </summary>
    public static AttackResult ResolveAttack(ShipInstance attacker, ShipInstance target, Weapon weapon, IDiceRoller dice, int? gunnerBonus = null, int? defense = null)
    {
        if (weapon.Type == WeaponType.Tracking)
            return AttackResult.Miss(0, target.CurrentTL, "Tracking weapons must be resolved as projectiles by the combat engine.");
        var check = RollAttack(attacker, target, weapon, dice, gunnerBonus, defense);
        if (!check.IsHit) return AttackResult.Miss(check.CheckTotal, check.DefenseValue, check.Description);
        var damage = ApplyDamage(target, target.QuadrantOf(attacker.Position), check.DamageRolled, check.Roll, dice);
        return AttackResult.Success(check.CheckTotal, check.DamageRolled, damage.ShieldDamage, damage.HullDamage,
            damage.CriticalScored, damage.CriticalSystem, damage.NewCriticalCondition,
            $"{check.Description} Effects: {damage.Description}.");
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
    /// Computes the probability that an attack hits. Natural 20s hit automatically,
    /// natural 1s always miss, and a wrecked weapons array cannot attack.
    /// </summary>
    public static int HitChancePercent(int bonus, int defenseValue)
    {
        if (bonus == int.MinValue) return 0;
        int need = defenseValue - bonus;
        int successfulNaturalRolls = need > 20 ? 1 : Math.Max(1, 21 - Math.Max(2, need));
        return successfulNaturalRolls * 5;
    }

    /// <summary>
    /// Maps a d% roll to the affected system per the Starfinder critical damage table.
    /// 1-10: Life Support, 11-30: Sensors, 31-60: Weapons Array, 61-80: Engines, 81-100: Power Core.
    /// If a system is already wrecked, cascades to the next one down.
    /// </summary>
    public static ShipSystem? SystemFromPercent(int percent, ShipInstance target)
    {
        ShipSystem system = percent <= 10 ? ShipSystem.LifeSupport : percent <= 30 ? ShipSystem.Sensors
            : percent <= 60 ? ShipSystem.WeaponsArray : percent <= 80 ? ShipSystem.Engines : ShipSystem.PowerCore;
        return NextAvailableSystem(system, target);
    }

    private static ShipSystem? NextAvailableSystem(ShipSystem startingSystem, ShipInstance target)
    {
        ShipSystem[] order = [ShipSystem.LifeSupport, ShipSystem.Sensors, ShipSystem.WeaponsArray, ShipSystem.Engines, ShipSystem.PowerCore];
        for (int i = Array.IndexOf(order, startingSystem); i < order.Length; i++)
            if (target.Systems.Get(order[i]) != SystemCondition.Wrecked) return order[i];
        return null;
    }
}
