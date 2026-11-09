using Quadrant.RulesEngine;

namespace Quadrant.RulesEngine.Tests;

public class TurnInPlaceTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void StuntRequiresEnginesThatAreNotMalfunctioningOrWrecked(int damage, bool allowed)
    {
        var (engine, ship) = Create();
        for (int i = 0; i < damage; i++) ship.Systems.ApplyCritical(ShipSystem.Engines);
        Assert.Equal(allowed, engine.CanTurnInPlace(ship));
        Assert.Equal(allowed, engine.TryTurnInPlace(Facing.West).IsValid);
        Assert.Equal(allowed ? Facing.West : Facing.East, ship.Facing);
    }

    [Fact]
    public void HeldEnginesPermitTheStuntUntilTheRoundEnds()
    {
        var (engine, ship) = Create();
        ship.Systems.ApplyCritical(ShipSystem.Engines);
        ship.Systems.ApplyCritical(ShipSystem.Engines);
        ship.Systems.HeldSystem = ShipSystem.Engines;
        Assert.True(engine.CanTurnInPlace(ship));
        ship.Systems.HeldSystem = null;
        Assert.False(engine.CanTurnInPlace(ship));
    }

    [Fact]
    public void StuntRequiresAnAvailablePilot()
    {
        var (engine, ship) = Create();
        ship.CrewActionsUsed.Add(0);
        Assert.False(engine.CanTurnInPlace(ship));
        Assert.False(engine.TryTurnInPlace(Facing.West).IsValid);
        Assert.Equal(Facing.East, ship.Facing);
    }

    private static (CombatEngine Engine, ShipInstance Ship) Create()
    {
        var definition = new ShipDefinition { Id = "hauler", Name = "Hauler", Size = "Huge", Maneuverability = Maneuverability.Poor,
            Shields = new QuadrantShields(), Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["piloting"] = 10 })] };
        var ship = definition.Instantiate("hauler", new Hex(0, 0), Facing.East, 0);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["hauler"] };
        state.Ships.Add(ship);
        return (new CombatEngine(state, new DiceRoller(1)), ship);
    }
}
