using Quadrant.RulesEngine;

namespace Quadrant.RulesEngine.Tests;

public class GlideTests
{
    private static (CombatEngine Engine, ShipInstance Ship) Create()
    {
        var def = new ShipDefinition { Id = "ship", Name = "Ship", Speed = 6, Maneuverability = Maneuverability.Average,
            Shields = new QuadrantShields(), Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["piloting"] = 10 }, 4)] };
        var ship = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        state.Ships.Add(ship);
        state.Ships.Add(def.Instantiate("b", new Hex(20, 0), Facing.West, 1));
        return (new CombatEngine(state, new DiceRoller(1)), ship);
    }

    [Fact]
    public void WreckedEnginesAllowPilotToGlideAsMinorAction()
    {
        var (engine, ship) = Create();
        for (int i = 0; i < 3; i++) ship.Systems.ApplyCritical(ShipSystem.Engines);
        Assert.Equal(0, ship.AvailableGlideCrew());
        Assert.Null(engine.GetManeuverChance(ship));
        Assert.False(engine.CanTurnInPlace(ship));
        var moves = engine.GetValidMoves();
        Assert.Contains(moves, m => m.Position == new Hex(3, 0));
        Assert.DoesNotContain(moves, m => m.Position == new Hex(4, 0));
        Assert.DoesNotContain(moves, m => m.FinalFacing != Facing.East);
        Assert.True(engine.TryMoveTo(new Hex(3, 0), Facing.East).IsValid);
        Assert.Contains(0, ship.CrewMinorActionsUsed);
        Assert.Empty(ship.CrewActionsUsed);
        Assert.Equal(4, ship.RoundAcTlModifier);
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("moves to"));
    }

    [Fact]
    public void WorkingOrHeldEnginesUseNormalPilotMovement()
    {
        var (engine, ship) = Create();
        Assert.Equal(-1, ship.AvailableGlideCrew());
        for (int i = 0; i < 3; i++) ship.Systems.ApplyCritical(ShipSystem.Engines);
        ship.Systems.HeldSystem = ShipSystem.Engines;
        Assert.Equal(-1, ship.AvailableGlideCrew());
        Assert.Contains(engine.GetValidMoves(), m => m.Position == new Hex(6, 0));
        Assert.True(engine.TryMoveTo(new Hex(6, 0), Facing.East).IsValid);
        Assert.Contains(0, ship.CrewActionsUsed);
        Assert.Empty(ship.CrewMinorActionsUsed);
    }

    [Fact]
    public void PilotWhoAlreadyActedCannotGlide()
    {
        var (engine, ship) = Create();
        for (int i = 0; i < 3; i++) ship.Systems.ApplyCritical(ShipSystem.Engines);
        ship.CrewActionsUsed.Add(0);
        Assert.Equal(-1, ship.AvailableGlideCrew());
        Assert.Empty(engine.GetValidMoves());
    }
}
