using Quadrant.RulesEngine;
using Quadrant.Server.Sessions;

namespace Quadrant.RulesEngine.Tests;

public class DisabledDriftTests
{
    private static ShipDefinition Definition(int speed = 6) => new()
    {
        Id = "ship", Name = "Ship", Speed = speed, HullPoints = 10, DamageThreshold = 0,
        CriticalThreshold = 100, Shields = new QuadrantShields { Forward = 0, Aft = 0, Port = 0, Starboard = 0 }
    };

    private static GameState CreateState(params ShipInstance[] ships)
    {
        var state = new GameState { GameId = "test", RoomCode = "test", Round = 2, Phase = CombatPhase.Engineering };
        state.Ships.AddRange(ships);
        return state;
    }

    [Fact]
    public void DamageRecordsFacingAndShipDriftsOnceAtStartOfEachHelm()
    {
        var ship = Definition().Instantiate("a", new Hex(0, 0), Facing.East, 0);
        CombatResolver.ApplyDamage(ship, Quadrant.Forward, 10, 10, new DiceRoller(1));
        Assert.True(ship.IsDisabled);
        Assert.Equal(Facing.East, ship.DriftFacing);
        ship.Facing = Facing.NorthWest; // Drift uses the facing at disablement.
        var state = CreateState(ship);
        var engine = new CombatEngine(state, new DiceRoller(1));
        engine.AdvancePhase();
        Assert.Equal(new Hex(3, 0), ship.Position);
        Assert.DoesNotContain("a", state.ActionOrder);
        Assert.Single(engine.LastEvents, e => e.Message.Contains("drifts 3 hexes"));
        state.Phase = CombatPhase.Engineering;
        engine.AdvancePhase();
        Assert.Equal(new Hex(3, 0), ship.Position);
        state.Round++;
        state.Phase = CombatPhase.Engineering;
        engine.AdvancePhase();
        Assert.Equal(new Hex(6, 0), ship.Position);
    }

    [Fact]
    public void DriftPassesOccupiedHexesAndEndsAtLastOpenHexWithinSpeed()
    {
        var def = Definition();
        var ship = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        ship.IsDisabled = true; // Old saved games have no recorded drift direction.
        var first = def.Instantiate("b", new Hex(1, 0), Facing.West, 1);
        var last = def.Instantiate("c", new Hex(3, 0), Facing.West, 1);
        var state = CreateState(ship, first, last);
        var engine = new CombatEngine(state, new DiceRoller(1));
        engine.AdvancePhase();
        Assert.Equal(Facing.East, ship.DriftFacing);
        Assert.Equal(new Hex(2, 0), ship.Position);
        Assert.Equal(new Hex(1, 0), first.Position);
        Assert.Equal(new Hex(3, 0), last.Position);
    }

    [Fact]
    public void DestroyedShipsDoNotDriftAndOddSpeedRoundsDown()
    {
        var def = Definition(5);
        var ship = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        ship.IsDisabled = true;
        var destroyed = def.Instantiate("b", new Hex(0, 3), Facing.East, 1);
        destroyed.IsDisabled = destroyed.IsDestroyed = true;
        var engine = new CombatEngine(CreateState(ship, destroyed), new DiceRoller(1));
        engine.AdvancePhase();
        Assert.Equal(new Hex(2, 0), ship.Position);
        Assert.Equal(new Hex(0, 3), destroyed.Position);
    }

    [Fact]
    public void DriftStateSurvivesReloadWithoutRepeatingTheSameRound()
    {
        string db = Path.Combine(Path.GetTempPath(), $"quadrant-drift-{Guid.NewGuid():N}.db");
        try
        {
            var ship = Definition().Instantiate("a", new Hex(0, 0), Facing.East, 0);
            ship.IsDisabled = true;
            var manager = new SessionManager(db);
            var game = manager.Create("drift", [ship]);
            game.State.Round = 3;
            game.State.Phase = CombatPhase.Engineering;
            game.Engine.AdvancePhase();
            manager.Save(game, true);
            var restored = new SessionManager(db).Get(game.RoomCode)!;
            var saved = restored.State.GetShip("a")!;
            Assert.Equal(Facing.East, saved.DriftFacing);
            Assert.Equal(3, saved.LastDriftRound);
            Assert.Equal(new Hex(3, 0), saved.Position);
            restored.State.Phase = CombatPhase.Engineering;
            restored.Engine.AdvancePhase();
            Assert.Equal(new Hex(3, 0), saved.Position);
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }
}
