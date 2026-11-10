using System.Text.Json;
using System.Text.Json.Serialization;
using Quadrant.RulesEngine;

namespace Quadrant.RulesEngine.Tests;

public class DamageReportTests
{
    private static (CombatEngine Engine, ShipInstance Target) Create(string damage = "34", int roll = 15,
        WeaponType type = WeaponType.DirectFire, int threshold = 0, bool pointDefense = false)
    {
        var weapon = new Weapon { Name = "Laser", Arc = FiringArc.Forward, Damage = DamageExpression.From(damage),
            Type = type, ProjectileSpeed = 2 };
        var attacker = new ShipDefinition { Id = "a", Name = "Attacker", HullPoints = 100,
            Shields = new QuadrantShields(), Weapons = [weapon], GunnerBaseAttack = 10 }.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var target = new ShipDefinition { Id = "b", Name = "Target", HullPoints = 100, DamageThreshold = threshold,
            Shields = new QuadrantShields { Forward = 30 },
            Weapons = pointDefense ? [weapon with { Name = "Point laser", PointDefenseBonus = 20 }] : []
        }.Instantiate("b", new Hex(1, 0), Facing.West, 1);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery,
            Round = 3, ActionOrder = ["a", "b"] };
        state.Ships.AddRange([attacker, target]);
        return (new CombatEngine(state, new FixedDice(roll)), target);
    }

    private static void FireAndFinish(CombatEngine engine)
    {
        Assert.NotNull(engine.ApplyFire(new FireAction(0, "b")));
        engine.EndGunneryTurn();
        engine.EndGunneryTurn();
    }

    [Fact]
    public void CapturesFrozenBeforeAndAfterAndPersistsThroughJson()
    {
        var (engine, target) = Create();
        FireAndFinish(engine);
        var damage = Assert.Single(target.DamageReport);
        Assert.Equal(3, damage.Round);
        Assert.Equal(CombatPhase.Gunnery, damage.Phase);
        Assert.Equal("a", damage.AttackerId);
        Assert.Equal("Attacker", damage.AttackerName);
        Assert.Equal(30, damage.Before.Shields[Quadrant.Forward]);
        Assert.Equal(0, damage.After.Shields[Quadrant.Forward]);
        Assert.Equal(100, damage.Before.Hull);
        Assert.Equal(96, damage.After.Hull);
        Assert.Equal(30, damage.ShieldDamage);
        Assert.Equal(4, damage.HullDamage);
        target.HullPoints = 100;
        target.Shields.Forward = 20;
        Assert.Equal(96, damage.After.Hull);
        Assert.Equal(0, damage.After.Shields[Quadrant.Forward]);
        var restored = JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(engine.State))!;
        var saved = Assert.Single(restored.GetShip("b")!.DamageReport);
        Assert.Equal(96, saved.After.Hull);
        Assert.Equal(30, saved.Before.Shields[Quadrant.Forward]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        using var api = JsonDocument.Parse(JsonSerializer.Serialize(saved, options));
        Assert.Equal(30, api.RootElement.GetProperty("before").GetProperty("shields").GetProperty("Forward").GetInt32());
    }

    [Fact]
    public void KeepsStoppedHitsButOmitsMisses()
    {
        var (engine, target) = Create("34", threshold: 5);
        FireAndFinish(engine);
        var hit = Assert.Single(target.DamageReport);
        Assert.Equal(4, hit.StoppedDamage);
        Assert.Equal(0, hit.HullDamage);
        var (missEngine, missTarget) = Create(roll: 1);
        FireAndFinish(missEngine);
        Assert.Empty(missTarget.DamageReport);
    }

    [Fact]
    public void ClearsEveryShipAtGunneryEntryButNotEarlier()
    {
        var (engine, target) = Create();
        FireAndFinish(engine);
        target.IsDestroyed = true;
        engine.State.Phase = CombatPhase.Engineering;
        engine.AdvancePhase();
        Assert.Single(target.DamageReport);
        engine.AdvancePhase();
        Assert.Equal(CombatPhase.Gunnery, engine.State.Phase);
        Assert.Empty(target.DamageReport);
        Assert.Equal(engine.State.Round, engine.State.DamageReportRound);
    }

    [Fact]
    public void ReportsShotDownMissilesButNotFailedTrackingChecks()
    {
        var (engine, target) = Create(type: WeaponType.Tracking, pointDefense: true);
        FireAndFinish(engine);
        var stopped = Assert.Single(target.DamageReport);
        Assert.True(stopped.ShotDown);
        Assert.NotNull(stopped.ProjectileId);
        Assert.Equal("Point laser", stopped.DefenseWeapon);
        Assert.Equal(FiringArc.Forward, stopped.DefenseWeaponArc);
        Assert.Equal(stopped.Before, stopped.After);
        var (missEngine, missTarget) = Create(type: WeaponType.Tracking, roll: 1);
        FireAndFinish(missEngine);
        Assert.Empty(missTarget.DamageReport);
    }

    [Fact]
    public void MissileImpactsIncludeAllCriticalEffectsAndStatusChanges()
    {
        var (engine, target) = Create("140", roll: 20, type: WeaponType.Tracking);
        FireAndFinish(engine);
        var damage = Assert.Single(target.DamageReport);
        Assert.False(damage.ShotDown);
        Assert.NotNull(damage.ProjectileId);
        Assert.True(damage.After.IsDisabled);
        Assert.Equal(-10, damage.After.Hull);
        Assert.NotEmpty(damage.CriticalEffects);
        // This unarmed target skips weapons-array results and takes engine damage.
        Assert.NotEqual(damage.Before.Systems[ShipSystem.Engines], damage.After.Systems[ShipSystem.Engines]);
    }

    [Fact]
    public void ExistingMissileThatFailsTrackingDoesNotEnterReport()
    {
        var (engine, target) = Create(roll: 1);
        engine.State.Projectiles.Add(new TrackingProjectile { Id = "missile", Name = "Missile", TeamId = 0,
            OwnerShipId = "a", TargetShipId = "b", Speed = 2, GunnerBonus = 100,
            Position = new Hex(0, 0), Facing = Facing.East,
            Weapon = new Weapon { Name = "Launcher", Arc = FiringArc.Forward, Type = WeaponType.Tracking,
                Damage = DamageExpression.From("34"), ProjectileSpeed = 2 } });
        engine.AdvancePhase();
        Assert.Empty(target.DamageReport);
        Assert.Empty(engine.State.Projectiles);
    }

    private sealed class FixedDice(int roll) : IDiceRoller
    {
        public int RollD20() => roll;
        public int Roll(int sides) => 1;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }
}
