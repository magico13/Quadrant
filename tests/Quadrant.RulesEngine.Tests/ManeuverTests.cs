using Quadrant.RulesEngine;
using Quadrant.Server.Sessions;

namespace Quadrant.RulesEngine.Tests;

public class ManeuverTests
{
    private static (GameState State, ShipInstance Ship, CombatEngine Engine) Create(int roll)
    {
        var def = new ShipDefinition { Id = "ship", Name = "Ship", Speed = 5, Tier = 2,
            Maneuverability = Maneuverability.Average, Shields = new QuadrantShields(),
            Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["piloting"] = 0 })] };
        var ship = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        state.Ships.Add(ship);
        state.Ships.Add(def.Instantiate("b", new Hex(20, 0), Facing.West, 1));
        return (state, ship, new CombatEngine(state, new FixedDice(roll)));
    }

    [Theory]
    [InlineData(18, true)]
    [InlineData(17, false)]
    public void AttemptLocksActionAndKeepsMovementAvailable(int roll, bool success)
    {
        var (_, ship, engine) = Create(roll);
        var normal = engine.GetValidMoves().Select(m => (m.Position, m.FinalFacing)).ToHashSet();
        var previews = engine.GetMovementPreviews();
        var restricted = Assert.Single(previews, m => m.Position == new Hex(1, 0) && m.FinalFacing == Facing.NorthEast);
        Assert.True(restricted.RequiresManeuver);
        Assert.Contains(previews, m => m.Position == restricted.Position && m.FinalFacing == Facing.East && !m.RequiresManeuver);
        Assert.Null(engine.AttemptManeuver(restricted.Position, restricted.FinalFacing));
        Assert.Equal(success, ship.ManeuverSucceeded);
        Assert.Contains(0, ship.CrewActionsUsed);
        Assert.NotNull(engine.AttemptManeuver(restricted.Position, restricted.FinalFacing));
        Assert.False(engine.StartStunt("Evade").IsValid);
        Assert.False(engine.TryTurnInPlace(Facing.West).IsValid);
        Assert.All(engine.GetMovementPreviews(), m => Assert.False(m.RequiresManeuver));
        Assert.Equal(success, engine.GetValidMoves().Any(m => m.Position == restricted.Position && m.FinalFacing == restricted.FinalFacing));
        if (!success)
        {
            Assert.True(normal.SetEquals(engine.GetValidMoves().Select(m => (m.Position, m.FinalFacing))));
            Assert.False(engine.TryMoveTo(restricted.Position, restricted.FinalFacing).IsValid);
            Assert.True(engine.TryMoveTo(new Hex(1, 0), Facing.East).IsValid);
        }
        else Assert.True(engine.TryMoveTo(restricted.Position, restricted.FinalFacing).IsValid);
        ship.BeginRound();
        Assert.Null(ship.ManeuverSucceeded);
    }

    [Fact]
    public void NormalAndIllegalChoicesCannotSpendOrBypassTheCheck()
    {
        var (_, ship, engine) = Create(18);
        Assert.NotNull(engine.AttemptManeuver(new Hex(1, 0), Facing.East));
        Assert.NotNull(engine.AttemptManeuver(new Hex(30, 0), Facing.East));
        Assert.False(engine.TryMoveTo(new Hex(1, 0), Facing.NorthEast).IsValid);
        Assert.Null(ship.ManeuverSucceeded);
        Assert.Empty(ship.CrewActionsUsed);
    }

    [Fact]
    public void MalfunctioningEnginesAllowManeuverWithPenaltyButWreckedEnginesDoNot()
    {
        var (_, ship, engine) = Create(20);
        ship.Systems.ApplyCritical(ShipSystem.Engines);
        ship.Systems.ApplyCritical(ShipSystem.Engines);
        Assert.Equal(0, engine.GetManeuverChance(ship));
        Assert.Null(engine.AttemptManeuver(new Hex(1, 0), Facing.NorthEast));
        Assert.False(ship.ManeuverSucceeded);
        ship.BeginRound();
        ship.Systems.ApplyCritical(ShipSystem.Engines);
        Assert.Null(engine.GetManeuverChance(ship));
        Assert.DoesNotContain(engine.GetMovementPreviews(), move => move.RequiresManeuver);
    }

    [Fact]
    public void ManeuverResultAndActionSurviveSessionReload()
    {
        var (_, ship, _) = Create(18);
        string db = Path.Combine(Path.GetTempPath(), $"quadrant-maneuver-{Guid.NewGuid():N}.db");
        try
        {
            var manager = new SessionManager(db, diceFactory: () => new FixedDice(18));
            var game = manager.Create("game", [ship]);
            game.State.Phase = CombatPhase.Helm;
            game.State.ActionOrder = ["a"];
            Assert.Null(game.Engine.AttemptManeuver(new Hex(1, 0), Facing.NorthEast));
            manager.Save(game, true);
            var restored = new SessionManager(db).Get(game.RoomCode)!;
            Assert.True(restored.State.Ships[0].ManeuverSucceeded);
            Assert.Contains(0, restored.State.Ships[0].CrewActionsUsed);
            Assert.False(restored.Engine.StartStunt("Evade").IsValid);
            Assert.Contains(restored.Engine.GetValidMoves(), m => m.Position == new Hex(1, 0) && m.FinalFacing == Facing.NorthEast);
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
