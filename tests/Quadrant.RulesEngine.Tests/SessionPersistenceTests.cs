using Quadrant.RulesEngine;
using Quadrant.Server.Sessions;

namespace Quadrant.RulesEngine.Tests;

public class SessionPersistenceTests
{
    [Fact]
    public void EngineeringEffectsAndUsedActionsSurviveRestart()
    {
        string db = Path.Combine(Path.GetTempPath(), $"quadrant-{Guid.NewGuid():N}.db");
        try
        {
            var def = new ShipDefinition { Id = "ship", Name = "Ship", Tier = 2, Shields = new QuadrantShields(),
                Crew = [new CrewDefinition("Engineer", new Dictionary<string, int> { ["engineering"] = 20 }, 2),
                        new CrewDefinition("Engineer", new Dictionary<string, int> { ["engineering"] = 20 }, 2)] };
            var manager = new SessionManager(db, diceFactory: () => new FixedDice());
            var session = manager.Create("game", [def.Instantiate("a", new Hex(0, 0), Facing.East, 0)]);
            session.State.Phase = CombatPhase.Engineering;
            var ship = session.State.Ships[0];
            ship.Systems.ApplyCritical(ShipSystem.Engines);
            ship.BarrelRolledThisRound = true;
            ship.BarrelRollActive = true;
            Assert.Null(session.Engine.ApplyEngineering(new EngineerAction("a", 0, "Divert", "Weapons", null)));
            Assert.Null(session.Engine.ApplyEngineering(new EngineerAction("a", 1, "Hold It Together", "Engines", null)));
            manager.Save(session, true);
            var restored = new SessionManager(db).Get(session.RoomCode)!.State.Ships[0];
            Assert.True(restored.DivertWeapons);
            Assert.True(restored.BarrelRolledThisRound);
            Assert.True(restored.BarrelRollActive);
            Assert.Equal(ShipSystem.Engines, restored.Systems.HeldSystem);
            Assert.Equal(SystemCondition.None, restored.Systems.GetEffective(ShipSystem.Engines));
            Assert.Contains("Divert", restored.EngineeringActionsUsed);
            Assert.Equal(2, restored.CrewActionsUsed.Count);
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FixedDice : IDiceRoller
    {
        public int RollD20() => 15;
        public int Roll(int sides) => 1;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }

    [Fact]
    public void PendingLaunchAndInFlightProjectileSurviveRestarts()
    {
        string db = Path.Combine(Path.GetTempPath(), $"quadrant-{Guid.NewGuid():N}.db");
        try
        {
            var weapon = new Weapon { Name = "Missile Launcher", Arc = FiringArc.Forward, Type = WeaponType.Tracking,
                ProjectileSpeed = 2, RangeIncrement = 10, Damage = DamageExpression.From("3"), LimitedFireUses = 2 };
            var definition = new ShipDefinition { Id = "ship", Name = "Ship", HullPoints = 50, Shields = new QuadrantShields(),
                Weapons = [weapon], Crew = [new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 100 }, 2)] };
            var manager = new SessionManager(db, diceFactory: () => new FixedDice());
            var game = manager.Create("game", [definition.Instantiate("a", new Hex(0, 0), Facing.East, 0),
                definition.Instantiate("b", new Hex(6, 0), Facing.West, 1)]);
            game.State.Phase = CombatPhase.Gunnery;
            game.State.ActionOrder = ["a", "b"];
            Assert.True(game.Engine.ApplyFire(new FireAction(0, "b"))!.IsHit);
            manager.Save(game, true);
            var restartedManager = new SessionManager(db);
            var restarted = restartedManager.Get(game.RoomCode)!;
            Assert.Equal(1, restarted.State.GetShip("a")!.WeaponUsesRemaining[0]);
            restarted.Engine.EndGunneryTurn();
            restarted.Engine.EndGunneryTurn();
            Assert.Single(restarted.State.Projectiles);
            restartedManager.Save(restarted, true);
            var again = new SessionManager(db).Get(game.RoomCode)!;
            Assert.Equal(new Hex(2, 0), Assert.Single(again.State.Projectiles).Position);
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }

    [Fact]
    public void CreatingGameExpiresCompletedUnstartedAndInactiveGames()
    {
        string db = Path.Combine(Path.GetTempPath(), $"quadrant-{Guid.NewGuid():N}.db");
        try
        {
            var clock = new Clock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var manager = new SessionManager(db, clock);
            var setup = manager.Create("setup", []);
            var completed = manager.Create("done", []);
            completed.State.Phase = CombatPhase.Complete;
            manager.Save(completed, true);
            var old = manager.Create("old", []);
            old.State.Phase = CombatPhase.Helm;
            manager.Save(old, true);
            clock.Now = clock.Now.AddDays(29);
            var active = manager.Create("active", []);
            active.State.Phase = CombatPhase.Helm;
            manager.Save(active, true);
            clock.Now = clock.Now.AddDays(1);
            manager.Get(old.RoomCode); // Looking at state is not an action.
            manager.Create("new", []);
            Assert.Null(manager.Get(setup.RoomCode));
            Assert.Null(manager.Get(completed.RoomCode));
            Assert.Null(manager.Get(old.RoomCode));
            Assert.NotNull(manager.Get(active.RoomCode));
            var restarted = new SessionManager(db, clock);
            Assert.Null(restarted.Get(old.RoomCode));
            Assert.NotNull(restarted.Get(active.RoomCode));
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }

    [Fact]
    public void PendingFreeAttackAndRemainingMovementSurviveRestart()
    {
        string db = Path.Combine(Path.GetTempPath(), $"quadrant-{Guid.NewGuid():N}.db");
        try
        {
            var gun = new Weapon { Name = "Cannon", Arc = FiringArc.Aft, Damage = DamageExpression.From("3") };
            var moverDef = new ShipDefinition { Id = "mover", Name = "Mover", Speed = 3, Shields = new QuadrantShields(),
                Crew = [new CrewDefinition("Pilot", new Dictionary<string, int>(), 1)] };
            var enemyDef = new ShipDefinition { Id = "enemy", Name = "Enemy", Shields = new QuadrantShields(), Weapons = [gun],
                Crew = [new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 5 }, 1)] };
            var manager = new SessionManager(db, diceFactory: () => new FixedDice());
            var session = manager.Create("game", [moverDef.Instantiate("a", new Hex(0, 0), Facing.East, 0),
                enemyDef.Instantiate("b", new Hex(1, 0), Facing.West, 1)]);
            session.State.Phase = CombatPhase.Helm;
            session.State.ActionOrder = ["a", "b"];
            Assert.True(session.Engine.TryMoveTo(new Hex(3, 0), Facing.East).IsValid);
            Assert.True(session.Engine.HasPendingFreeAttack);
            manager.Save(session, true);
            var restored = new SessionManager(db).Get(session.RoomCode)!;
            Assert.Equal("b", restored.Engine.FreeAttackShipId);
            Assert.Single(restored.Engine.GetFreeAttackWeapons());
            Assert.Null(restored.Engine.ResolveFreeAttack(null));
            Assert.Equal(new Hex(3, 0), restored.State.GetShip("a")!.Position);
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }
    [Fact]
    public void SavedGameRestoresShipAndPendingGunneryState()
    {
        string db = Path.Combine(Path.GetTempPath(), $"quadrant-{Guid.NewGuid():N}.db");
        try
        {
            var weapon = new Weapon { Name = "Cannon", Arc = FiringArc.Forward, Damage = DamageExpression.From("4") };
            var definition = new ShipDefinition { Id = "ship", Name = "Ship", HullPoints = 50, Shields = new QuadrantShields(),
                Weapons = [weapon], Crew = [new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 50 }, 2)] };
            var attacker = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
            var target = definition.Instantiate("b", new Hex(1, 0), Facing.West, 1);
            var manager = new SessionManager(db, diceFactory: () => new FixedDice());
            var session = manager.Create("game", [attacker, target]);
            session.State.Phase = CombatPhase.Gunnery;
            session.State.ActionOrder = ["a", "b"];
            attacker.Systems.ApplyCritical(ShipSystem.Engines);
            var shot = session.Engine.ApplyFire(new FireAction(0, "b"));
            Assert.NotNull(shot);
            manager.Save(session, true);

            var reloaded = new SessionManager(db).Get(session.RoomCode);
            Assert.NotNull(reloaded);
            Assert.Equal(CombatPhase.Gunnery, reloaded!.State.Phase);
            Assert.Equal(SystemCondition.Glitching, reloaded.State.GetShip("a")!.Systems.Get(ShipSystem.Engines));
            Assert.Contains(0, reloaded.Engine.FiredWeaponIndices);
            Assert.Empty(reloaded.Engine.GetValidTargets());
            reloaded.Engine.EndGunneryTurn();
            reloaded.Engine.EndGunneryTurn();
            Assert.Contains(reloaded.Engine.LastEvents, e => e.Message.Contains("4 dmg (4 shield, 0 hull)"));
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }
}
