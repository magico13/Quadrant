using System.Text.Json;
using Quadrant.RulesEngine;

namespace Quadrant.RulesEngine.Tests;

public class WeaponArcDamageTests
{
    private static Weapon Gun(FiringArc arc) => new() { Name = $"{arc} cannon", Arc = arc, Damage = DamageExpression.From("1") };
    private static ShipInstance Ship(string id = "a", Hex? position = null, int team = 0, params Weapon[] weapons)
        => new ShipDefinition { Id = id, Name = id, HullPoints = 100, BaseAC = 12, BaseTL = 12,
            GunnerBaseAttack = 5, DexterityModifier = 2, CriticalThreshold = 0, DamageThreshold = 0,
            Shields = new QuadrantShields { Forward = 0, Starboard = 0, Aft = 0, Port = 0 }, Weapons = weapons }
            .Instantiate(id, position ?? new Hex(0, 0), Facing.East, team);

    private static void Wreck(ShipInstance ship, FiringArc arc)
    {
        for (int i = 0; i < 3; i++) ship.Systems.ApplyWeaponCritical(arc);
    }

    private static CombatEngine Battle(params ShipInstance[] ships)
    {
        var state = new GameState { GameId = "test", RoomCode = "TEST", Phase = CombatPhase.Gunnery,
            ActionOrder = ships.Select(s => s.InstanceId).ToList() };
        state.Ships.AddRange(ships);
        return new CombatEngine(state, new Dice());
    }

    [Fact]
    public void WeaponCriticalRandomlySelectsAnOccupiedArcAndEscalatesOnlyThatArc()
    {
        var ship = Ship(weapons: [Gun(FiringArc.Forward), Gun(FiringArc.Port)]);
        var damage = CombatResolver.ApplyDamage(ship, Quadrant.Forward, 1, 20, new Dice(2), ShipSystem.WeaponsArray);
        var effect = Assert.Single(damage.CriticalEffects);
        Assert.Equal(FiringArc.Port, effect.WeaponArc);
        Assert.Equal(SystemCondition.Glitching, ship.Systems.GetWeaponArc(FiringArc.Port));
        Assert.Equal(SystemCondition.None, ship.Systems.GetWeaponArc(FiringArc.Forward));
        CombatResolver.ApplyDamage(ship, Quadrant.Forward, 1, 20, new Dice(2), ShipSystem.WeaponsArray);
        Assert.Equal(SystemCondition.Malfunctioning, ship.Systems.GetWeaponArc(FiringArc.Port));
        Assert.Equal(SystemCondition.None, ship.Systems.GetWeaponArc(FiringArc.Forward));
    }

    [Fact]
    public void WreckedArraysAreSkippedAndCriticalsCascadeOnlyWhenAllOccupiedArcsAreWrecked()
    {
        var ship = Ship(weapons: [Gun(FiringArc.Forward), Gun(FiringArc.Port)]);
        Wreck(ship, FiringArc.Forward);
        var damage = CombatResolver.ApplyDamage(ship, Quadrant.Forward, 1, 20, new Dice(), ShipSystem.WeaponsArray);
        Assert.Equal(FiringArc.Port, Assert.Single(damage.CriticalEffects).WeaponArc);
        Assert.Equal(SystemCondition.None, ship.Systems.Get(ShipSystem.Engines));
        Wreck(ship, FiringArc.Port);
        damage = CombatResolver.ApplyDamage(ship, Quadrant.Forward, 1, 20, new Dice(), ShipSystem.WeaponsArray);
        Assert.Equal(ShipSystem.Engines, Assert.Single(damage.CriticalEffects).System);
    }

    [Fact]
    public void TurretCountsAsWeaponsInAllFourArcsForCriticalSelection()
    {
        var ship = Ship(weapons: [Gun(FiringArc.AllArcs)]);
        Assert.Equal(4, ship.OccupiedWeaponArcs.Count());
        var damage = CombatResolver.ApplyDamage(ship, Quadrant.Forward, 1, 20, new Dice(3), ShipSystem.WeaponsArray);
        Assert.Equal(FiringArc.Aft, Assert.Single(damage.CriticalEffects).WeaponArc);
    }

    [Fact]
    public void UnarmedShipSkipsWeaponsCriticals()
    {
        var ship = Ship();
        var damage = CombatResolver.ApplyDamage(ship, Quadrant.Forward, 1, 20, new Dice(), ShipSystem.WeaponsArray);
        Assert.Equal(ShipSystem.Engines, Assert.Single(damage.CriticalEffects).System);
    }

    [Fact]
    public void FixedMountUsesItsOwnConditionWhileTurretUsesTargetDirection()
    {
        var forward = Gun(FiringArc.Forward);
        var port = Gun(FiringArc.Port);
        var turret = Gun(FiringArc.AllArcs);
        var ship = Ship(weapons: [forward, port, turret]);
        ship.Systems.ApplyWeaponCritical(FiringArc.Port);
        ship.Systems.ApplyWeaponCritical(FiringArc.Port);
        Assert.Equal(7, CombatResolver.ComputeGunnerBonus(ship, forward, new Hex(1, 0)));
        Assert.Equal(3, CombatResolver.ComputeGunnerBonus(ship, port, new Hex(0, -1)));
        Assert.Equal(7, CombatResolver.ComputeGunnerBonus(ship, turret, new Hex(1, 0)));
        Assert.Equal(3, CombatResolver.ComputeGunnerBonus(ship, turret, new Hex(0, -1)));
        Assert.Equal(3, CombatResolver.ComputeGunnerBonus(ship, port with { BroadArc = true }, new Hex(1, 0)));
    }

    [Fact]
    public void GunneryOptionsAndRollsRespectOnlyTheSelectedWeaponsArc()
    {
        var ship = Ship(weapons: [Gun(FiringArc.Forward), Gun(FiringArc.AllArcs)]);
        Wreck(ship, FiringArc.Forward);
        ship.Systems.ApplyWeaponCritical(FiringArc.Port);
        var ahead = Ship("b", new Hex(1, 0), 1);
        var left = Ship("c", new Hex(0, -1), 1);
        var engine = Battle(ship, ahead, left);
        var option = Assert.Single(engine.GetValidTargets());
        Assert.Equal("c", option.TargetShipId);
        Assert.Equal(1, option.WeaponIndex);
        Assert.Equal(70, option.HitChancePercent);
        Assert.Equal(5, engine.ApplyFire(new FireAction(1, "c"))!.Modifier);
    }

    [Fact]
    public void BarrelRollMovesTheDamagedArrayWithItsMountsAndTurretDirection()
    {
        var ship = Ship(weapons: [Gun(FiringArc.Port), Gun(FiringArc.AllArcs)]);
        Wreck(ship, FiringArc.Port);
        ship.BarrelRollActive = true;
        Assert.Equal(FiringArc.Starboard, ship.Weapons[0].Arc);
        Assert.Equal(int.MinValue, ship.WeaponActionPenalty(ship.Weapons[0], new Hex(0, 1)));
        Assert.Equal(int.MinValue, ship.WeaponActionPenalty(ship.Weapons[1], new Hex(0, 1)));
        Assert.Equal(0, ship.WeaponActionPenalty(ship.Weapons[1], new Hex(0, -1)));
        var engine = Battle(ship, Ship("b", new Hex(0, 1), 1), Ship("c", new Hex(0, -1), 1));
        var option = Assert.Single(engine.GetValidTargets());
        Assert.Equal("c", option.TargetShipId);
        Assert.Equal(7, engine.ApplyFire(new FireAction(1, "c"))!.Modifier);
    }

    [Fact]
    public void HoldingOneArrayTogetherDoesNotRepairOtherArraysAndExpiresNextRound()
    {
        var ship = Ship(weapons: [Gun(FiringArc.Forward), Gun(FiringArc.Port)]);
        Wreck(ship, FiringArc.Forward);
        Wreck(ship, FiringArc.Port);
        ship.Systems.HeldSystem = ShipSystem.WeaponsArray;
        ship.Systems.HeldWeaponArc = FiringArc.Forward;
        Assert.Equal(-2, ship.Systems.GetWeaponActionPenalty(FiringArc.Forward));
        Assert.Equal(int.MinValue, ship.Systems.GetWeaponActionPenalty(FiringArc.Port));
        ship.BeginRound();
        Assert.Equal(int.MinValue, ship.Systems.GetWeaponActionPenalty(FiringArc.Forward));
        Assert.Null(ship.Systems.HeldWeaponArc);
    }

    [Fact]
    public void PerArcDamageAndTemporaryRepairSurviveSerializationAndSnapshotsStayFrozen()
    {
        var ship = Ship(weapons: [Gun(FiringArc.Forward), Gun(FiringArc.Port)]);
        Wreck(ship, FiringArc.Forward);
        ship.Systems.HeldSystem = ShipSystem.WeaponsArray;
        ship.Systems.HeldWeaponArc = FiringArc.Forward;
        var snapshot = DamageState.Capture(ship);
        var restored = JsonSerializer.Deserialize<ShipInstance>(JsonSerializer.Serialize(ship))!;
        Assert.Equal(-2, restored.Systems.GetWeaponActionPenalty(FiringArc.Forward));
        Assert.Equal(0, restored.Systems.GetWeaponActionPenalty(FiringArc.Port));
        ship.Systems.RepairWeaponArc(FiringArc.Forward);
        Assert.Equal(SystemCondition.Wrecked, snapshot.WeaponArcs[FiringArc.Forward]);
    }

    [Fact]
    public void TurretAtBoundaryOffersDistinctChancesAndUsesChosenArc()
    {
        var ship = Ship(weapons: [Gun(FiringArc.AllArcs)]);
        ship.Systems.ApplyWeaponCritical(FiringArc.Forward);
        var target = Ship("b", new Hex(1, 1), 1); // +30 degrees
        var engine = Battle(ship, target);
        var option = Assert.Single(engine.GetValidTargets());
        Assert.True(option.ArcChoiceRequired);
        Assert.Equal(70, option.ArcHitChances![FiringArc.Forward]);
        Assert.Equal(80, option.ArcHitChances[FiringArc.Starboard]);
        Assert.Equal(5, engine.ApplyFire(new FireAction(0, "b"), FiringArc.Forward)!.Modifier);
    }

    [Fact]
    public void EngineeringRequiresAnArcAndHoldsOnlyThatArrayTogether()
    {
        var definition = new ShipDefinition { Id = "a", Name = "Engineer ship", Tier = 1,
            Shields = new QuadrantShields(), Weapons = [Gun(FiringArc.AllArcs)],
            Crew = [new CrewDefinition("Engineer", new Dictionary<string, int> { ["engineering"] = 20 })] };
        var ship = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        Wreck(ship, FiringArc.Forward);
        Wreck(ship, FiringArc.Port);
        var state = new GameState { GameId = "test", RoomCode = "TEST", Phase = CombatPhase.Engineering };
        state.Ships.Add(ship);
        var engine = new CombatEngine(state, new Dice());
        Assert.NotNull(engine.ApplyEngineering(new EngineerAction("a", 0, "Hold It Together", "WeaponsArray", null)));
        Assert.NotNull(engine.ApplyEngineering(new EngineerAction("a", 0, "Hold It Together", "WeaponsArray", null, FiringArc.AllArcs)));
        Assert.Empty(ship.CrewActionsUsed);
        Assert.Null(engine.ApplyEngineering(new EngineerAction("a", 0, "Hold It Together", "WeaponsArray", null, FiringArc.Port)));
        Assert.Equal(-2, ship.Systems.GetWeaponActionPenalty(FiringArc.Port));
        Assert.Equal(int.MinValue, ship.Systems.GetWeaponActionPenalty(FiringArc.Forward));
    }

    [Fact]
    public void CrewGunnerAndTrackingLaunchUseTheFiringArcPenalty()
    {
        var missile = Gun(FiringArc.AllArcs) with { Type = WeaponType.Tracking, ProjectileSpeed = 2 };
        var definition = new ShipDefinition { Id = "a", Name = "Missile ship", Shields = new QuadrantShields(), Weapons = [missile],
            Crew = [new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 10 })] };
        var ship = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        Wreck(ship, FiringArc.Forward);
        ship.Systems.ApplyWeaponCritical(FiringArc.Port);
        var engine = Battle(ship, Ship("b", new Hex(0, -1), 1));
        var option = Assert.Single(engine.GetValidTargets());
        var check = engine.ApplyFire(new FireAction(0, "b"))!;
        Assert.Equal(ship.CrewBonus(0, "gunnery", "Gunner") - 2, check.Modifier);
        Assert.Equal(CombatResolver.HitChancePercent(check.Modifier, 12), option.HitChancePercent);
        Assert.Equal(check.Modifier, Assert.Single(engine.CaptureSnapshot().PendingLaunches!).Projectile.GunnerBonus);
    }

    private sealed class Dice(int arcRoll = 1) : IDiceRoller
    {
        public int RollD20() => 15;
        public int Roll(int sides) { Assert.InRange(arcRoll, 1, sides); return arcRoll; }
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }
}
