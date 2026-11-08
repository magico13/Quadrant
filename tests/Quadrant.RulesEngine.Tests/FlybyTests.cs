using Quadrant.RulesEngine;
using Quadrant.Server.Sessions;

namespace Quadrant.RulesEngine.Tests;

public class FlybyTests
{
    private static (CombatEngine Engine, GameState State, ShipInstance Ship, ShipInstance Enemy) Create(int roll = 18)
    {
        var definition = new ShipDefinition
        {
            Id = "ship", Name = "Ship", Tier = 1, Speed = 6, Maneuverability = Maneuverability.Average,
            Shields = new QuadrantShields { Forward = 10, Aft = 10, Port = 10, Starboard = 10 },
            Weapons = [new Weapon { Name = "Port", Arc = FiringArc.Port, RangeIncrement = 1, Damage = DamageExpression.From("3") },
                new Weapon { Name = "Aft", Arc = FiringArc.Aft, RangeIncrement = 1, Damage = DamageExpression.From("3") },
                new Weapon { Name = "Turret", Arc = FiringArc.AllArcs, RangeIncrement = 1, Damage = DamageExpression.From("3") }],
            Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["piloting"] = 0 }),
                new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 100 }),
                new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 100 })]
        };
        var ship = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var enemy = (definition with { Tier = 2 }).Instantiate("b", new Hex(2, 0), Facing.West, 1);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        state.Ships.AddRange([ship, enemy]);
        return (new CombatEngine(state, new FixedDice(roll)), state, ship, enemy);
    }

    [Theory]
    [InlineData(18, true)]
    [InlineData(17, false)]
    public void CommittedCrossingRollsAgainstEnemyTierAndHandlesFreeAttack(int roll, bool success)
    {
        var (engine, state, ship, enemy) = Create(roll);
        Assert.Equal(15, engine.GetStuntSuccessChance(ship, "Flyby", enemy));
        Assert.True(engine.StartStunt("Flyby").IsValid);
        Assert.Empty(ship.CrewActionsUsed);
        Assert.Null(ship.FlybyTargetId);
        Assert.False(ship.FlybySucceeded);
        Assert.All(engine.GetValidMoves(), option => Assert.Contains(option.Path, p => p.Position == enemy.Position));
        Assert.DoesNotContain(engine.GetValidMoves(), option => option.Position == enemy.Position);
        Assert.True(engine.TryMoveTo(new Hex(4, 0), Facing.East).IsValid);
        Assert.Equal(success, ship.FlybySucceeded);
        Assert.Equal("b", ship.FlybyTargetId);
        Assert.True(ship.FlybyCrossed);
        Assert.Equal(!success, engine.HasPendingFreeAttack);
        Assert.Contains(0, ship.CrewActionsUsed);
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("vs DC 18"));
        if (!success)
        {
            Assert.Equal("b", engine.FreeAttackShipId);
            Assert.Null(engine.ResolveFreeAttack(null));
        }
        Assert.Equal(new Hex(4, 0), ship.Position);
        engine.SkipMove();
        Assert.Equal(CombatPhase.Gunnery, state.Phase);
        if (success)
            Assert.Contains(engine.GetValidTargets(), t => t.WeaponIndex == 0 && t.Flyby && t.Range == 1 && t.RangePenalty == 0);
        else
        {
            Assert.DoesNotContain(engine.GetValidTargets(), t => t.WeaponIndex == 0);
            Assert.All(engine.GetValidTargets(), t => Assert.False(t.Flyby));
        }
    }

    [Fact]
    public void ChoosesOneFiringArcAndAnyEnemyQuadrant()
    {
        var (engine, _, ship, enemy) = Create();
        Assert.True(engine.StartStunt("Flyby").IsValid);
        Assert.True(engine.TryMoveTo(new Hex(4, 0), Facing.East).IsValid);
        engine.SkipMove();
        Assert.Null(engine.ApplyFire(new FireAction(0, "b")));
        Assert.Null(engine.ApplyFire(new FireAction(0, "b", (Quadrant)999)));
        Assert.Null(ship.FlybyFiringArc);
        var check = engine.ApplyFire(new FireAction(0, "b", Quadrant.Port));
        Assert.True(check!.IsHit);
        Assert.Equal(100, check.Modifier);
        Assert.Equal(FiringArc.Port, ship.FlybyFiringArc);
        Assert.Null(engine.ApplyFire(new FireAction(1, "b", Quadrant.Aft)));
        Assert.DoesNotContain(engine.GetValidTargets(), t => t.WeaponIndex == 1);
        Assert.NotNull(engine.ApplyFire(new FireAction(2, "b", Quadrant.Aft)));
        engine.EndGunneryTurn();
        engine.EndGunneryTurn();
        Assert.Equal(7, enemy.Shields.Port);
        Assert.Equal(7, enemy.Shields.Aft);
        Assert.Equal(10, enemy.Shields.Forward);
        Assert.Null(ship.FlybyTargetId);
        Assert.Null(ship.FlybyFiringArc);
        Assert.False(ship.FlybySucceeded);
    }

    [Fact]
    public void TurretRequiresExplicitFlybyArcBeforeFirstShot()
    {
        var (engine, _, ship, _) = Create();
        Assert.True(engine.StartStunt("Flyby").IsValid);
        Assert.True(engine.TryMoveTo(new Hex(4, 0), Facing.East).IsValid);
        engine.SkipMove();
        Assert.True(Assert.Single(engine.GetValidTargets(), t => t.WeaponIndex == 2).ArcChoiceRequired);
        Assert.Null(engine.ApplyFire(new FireAction(2, "b", Quadrant.Forward)));
        Assert.NotNull(engine.ApplyFire(new FireAction(2, "b", Quadrant.Forward), FiringArc.Aft));
        Assert.Equal(FiringArc.Aft, ship.FlybyFiringArc);
        Assert.NotNull(engine.ApplyFire(new FireAction(1, "b", Quadrant.Port)));
    }

    [Fact]
    public void RejectsUnreachableEnemiesAndDamagedEnginesWithoutUsingAnAction()
    {
        var (engine, _, ship, enemy) = Create();
        enemy.Position = new Hex(6, 0);
        Assert.False(engine.StartStunt("Flyby").IsValid);
        enemy.Position = new Hex(2, 0);
        ship.Systems.ApplyCritical(ShipSystem.Engines);
        ship.Systems.ApplyCritical(ShipSystem.Engines);
        Assert.False(engine.StartStunt("Flyby").IsValid);
        Assert.Empty(ship.CrewActionsUsed);
    }

    [Fact]
    public void FlybyPathsCannotCrossMultipleEnemyOccupiedSpaces()
    {
        var (engine, state, ship, enemy) = Create();
        state.Ships.Add(enemy.Definition.Instantiate("c", new Hex(3, 0), Facing.West, 1));
        Assert.True(engine.StartStunt("Flyby").IsValid);
        Assert.All(engine.GetValidMoves(), option =>
        {
            var crossings = option.Path.Zip(option.Path.Skip(1))
                .Where(pair => pair.First.Position != pair.Second.Position)
                .SelectMany(pair => state.Ships.Where(s => s.InstanceId != ship.InstanceId && s.Position == pair.Second.Position));
            Assert.Single(crossings);
        });
        // With average maneuverability, this straight destination requires
        // crossing both enemies and must not be offered.
        Assert.False(engine.TryMoveTo(new Hex(4, 0), Facing.East).IsValid);
        Assert.Null(ship.FlybyTargetId);
        Assert.Empty(ship.CrewActionsUsed);
    }

    [Fact]
    public void RouteSelectsWhichEnemyIsUsedForTheCheck()
    {
        var (engine, state, ship, enemy) = Create(17);
        enemy.Position = new Hex(2, 1);
        var other = (enemy.Definition with { Tier = 1 }).Instantiate("c", new Hex(3, 0), Facing.West, 1);
        state.Ships.Add(other);
        Assert.True(engine.StartStunt("Flyby").IsValid);
        var option = engine.GetValidMoves().First(m => m.Path.Any(p => p.Position == other.Position));
        Assert.True(engine.TryMoveTo(option.Position, option.FinalFacing).IsValid);
        Assert.Equal("c", ship.FlybyTargetId);
        Assert.True(ship.FlybySucceeded);
        Assert.False(engine.HasPendingFreeAttack);
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("vs DC 16"));
    }

    [Theory]
    [InlineData(18, true)]
    [InlineData(17, false)]
    public void FlybyCanCrossAlliesBeforeAndAfterTheEnemyWithoutFreeAttacks(int roll, bool success)
    {
        var (engine, state, ship, enemy) = Create(roll);
        state.Ships.Add(enemy.Definition.Instantiate("ally", new Hex(1, 0), Facing.East, ship.TeamId));
        state.Ships.Add(enemy.Definition.Instantiate("ally-after", new Hex(3, 0), Facing.East, ship.TeamId));
        Assert.Contains(engine.GetValidMoves(), m => m.Position == new Hex(4, 0) && m.FinalFacing == Facing.East);
        Assert.True(engine.StartStunt("Flyby").IsValid);
        Assert.DoesNotContain(engine.GetValidMoves(), m => m.Position == new Hex(1, 0) || m.Position == new Hex(3, 0));
        Assert.True(engine.TryMoveTo(new Hex(4, 0), Facing.East).IsValid);
        Assert.Equal("b", ship.FlybyTargetId);
        Assert.Equal(success, ship.FlybySucceeded);
        if (!success)
        {
            Assert.Equal("b", engine.FreeAttackShipId);
            Assert.Null(engine.ResolveFreeAttack(null));
        }
        Assert.False(engine.HasPendingFreeAttack);
        Assert.Equal(new Hex(4, 0), ship.Position);
    }

    [Fact]
    public void TrackingFlybyFiresAtCloseRangeIntoChosenQuadrant()
    {
        var (_, state, original, enemy) = Create();
        var launcher = new Weapon { Name = "Missile", Arc = FiringArc.Port, Type = WeaponType.Tracking,
            ProjectileSpeed = 1, RangeIncrement = 1, Damage = DamageExpression.From("3"), LimitedFireUses = 1 };
        var ship = (original.Definition with { Weapons = [launcher] }).Instantiate("a", original.Position, original.Facing, 0);
        state.Ships[0] = ship;
        var engine = new CombatEngine(state, new FixedDice(18));
        Assert.True(engine.StartStunt("Flyby").IsValid);
        Assert.True(engine.TryMoveTo(new Hex(4, 0), Facing.East).IsValid);
        engine.SkipMove();
        Assert.NotNull(engine.ApplyFire(new FireAction(0, "b", Quadrant.Port)));
        Assert.Equal(0, ship.WeaponUsesRemaining[0]);
        engine.EndGunneryTurn();
        engine.EndGunneryTurn();
        Assert.Empty(state.Projectiles);
        Assert.Equal(7, enemy.Shields.Port);
        Assert.Equal(10, enemy.Shields.Forward);
    }

    [Fact]
    public void FailedFlybyAndCommittedPathSurviveRestartDuringFreeAttack()
    {
        string db = Path.Combine(Path.GetTempPath(), $"quadrant-flyby-{Guid.NewGuid():N}.db");
        try
        {
            var (_, _, ship, enemy) = Create();
            var manager = new SessionManager(db, diceFactory: () => new FixedDice(17));
            var session = manager.Create("flyby", [ship, enemy]);
            session.State.Phase = CombatPhase.Helm;
            session.State.ActionOrder = ["a", "b"];
            Assert.True(session.Engine.StartStunt("Flyby").IsValid);
            manager.Save(session, true);
            var restoredManager = new SessionManager(db, diceFactory: () => new FixedDice(17));
            var restored = restoredManager.Get(session.RoomCode)!;
            Assert.Equal("Flyby", restored.Engine.PendingStunt);
            Assert.True(restored.Engine.TryMoveTo(new Hex(4, 0), Facing.East).IsValid);
            Assert.True(restored.Engine.HasPendingFreeAttack);
            restoredManager.Save(restored, true);
            var again = new SessionManager(db).Get(session.RoomCode)!;
            Assert.Null(again.Engine.ResolveFreeAttack(null));
            Assert.Equal(new Hex(4, 0), again.State.GetShip("a")!.Position);
            Assert.False(again.State.GetShip("a")!.FlybySucceeded);
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }

    [Fact]
    public void SuccessfulFlybyArcAndPendingHitSurviveRestart()
    {
        string db = Path.Combine(Path.GetTempPath(), $"quadrant-flyby-{Guid.NewGuid():N}.db");
        try
        {
            var (_, _, ship, enemy) = Create();
            var manager = new SessionManager(db, diceFactory: () => new FixedDice(18));
            var session = manager.Create("flyby", [ship, enemy]);
            session.State.Phase = CombatPhase.Helm;
            session.State.ActionOrder = ["a", "b"];
            Assert.True(session.Engine.StartStunt("Flyby").IsValid);
            Assert.True(session.Engine.TryMoveTo(new Hex(4, 0), Facing.East).IsValid);
            session.Engine.SkipMove();
            Assert.NotNull(session.Engine.ApplyFire(new FireAction(0, "b", Quadrant.Port)));
            manager.Save(session, true);
            var restored = new SessionManager(db, diceFactory: () => new FixedDice(18)).Get(session.RoomCode)!;
            var restoredShip = restored.State.GetShip("a")!;
            Assert.True(restoredShip.FlybySucceeded);
            Assert.True(restoredShip.FlybyCrossed);
            Assert.Equal(FiringArc.Port, restoredShip.FlybyFiringArc);
            Assert.DoesNotContain(restored.Engine.GetValidTargets(), t => t.WeaponIndex == 1);
            Assert.NotNull(restored.Engine.ApplyFire(new FireAction(2, "b", Quadrant.Aft)));
            restored.Engine.EndGunneryTurn();
            restored.Engine.EndGunneryTurn();
            Assert.Equal(7, restored.State.GetShip("b")!.Shields.Port);
            Assert.Equal(7, restored.State.GetShip("b")!.Shields.Aft);
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }

    private sealed class FixedDice(int roll) : IDiceRoller
    {
        public int RollD20() => roll;
        public int Roll(int sides) => 1;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }
}
