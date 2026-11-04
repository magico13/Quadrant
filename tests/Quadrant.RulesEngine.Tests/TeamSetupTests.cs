using Quadrant.RulesEngine;
using Quadrant.Server.Sessions;

namespace Quadrant.RulesEngine.Tests;

public class TeamSetupTests
{
    private static ShipCatalogService Catalog()
    {
        var catalog = new ShipCatalogService();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Quadrant.Server"));
        catalog.LoadWeapons(Path.Combine(root, "weapons"));
        catalog.LoadShips(Path.Combine(root, "ships"));
        return catalog;
    }

    [Fact]
    public void PlacementUsesRolledDistanceGroupFacingAndDistinctHexes()
    {
        var catalog = Catalog();
        var selections = new List<ShipSelection> { new("barrow-boneshard", 2), new("pegasus", 2) };
        selections.AddRange(Enumerable.Repeat(new ShipSelection("barrow-boneshard", 2), 45));
        selections.Add(new("gorgon", 7));
        for (int seed = 0; seed < 10; seed++)
        {
            var ships = catalog.CreateShips(selections, new Random(seed));
            var firstTeam = ships.Where(s => s.TeamId == 2).ToList();
            var secondTeam = ships.Where(s => s.TeamId == 7).ToList();
            Assert.Equal(ships.Count, ships.Select(s => s.Position).Distinct().Count());
            Assert.Single(firstTeam.Select(s => s.Facing).Distinct());
            Assert.Equal("pegasus", firstTeam[0].Definition.Id);
            Assert.InRange(firstTeam[0].Position.DistanceTo(secondTeam[0].Position), 8, 23);
            for (int i = 1; i < firstTeam.Count; i++)
                Assert.Contains(firstTeam.Take(i), other => other.Position.DistanceTo(firstTeam[i].Position) <= 3);
        }
    }

    [Fact]
    public void ThreeTeamsStartSeparatedInATriangle()
    {
        var catalog = Catalog();
        var selections = Enumerable.Range(0, 3).Select(i => new ShipSelection("gorgon", i)).ToList();
        for (int seed = 0; seed < 100; seed++)
        {
            var ships = catalog.CreateShips(selections, new Random(seed));
            var distances = new[] { ships[0].Position.DistanceTo(ships[1].Position),
                ships[0].Position.DistanceTo(ships[2].Position), ships[1].Position.DistanceTo(ships[2].Position) };
            Assert.All(distances, d => Assert.InRange(d, 8, 23));
            Assert.True(distances.Max() - distances.Min() <= 15);
        }
    }

    [Fact]
    public void SupportsMoreThanFiveTeamsAndRejectsInvalidSelections()
    {
        var catalog = Catalog();
        var selections = Enumerable.Range(0, 8).Select(i => new ShipSelection("gorgon", i * 2)).ToList();
        var ships = catalog.CreateShips(selections, new Random(2));
        Assert.Equal(8, ships.Select(s => s.TeamId).Distinct().Count());
        Assert.Equal(8, ships.Select(s => s.Position).Distinct().Count());
        Assert.Throws<ArgumentException>(() => catalog.CreateShips([new("missing", 0), new("gorgon", 1)]));
        Assert.Throws<ArgumentException>(() => catalog.CreateShips([new("gorgon", 0)]));
    }

    [Theory]
    [InlineData(false, false, CombatPhase.Engineering, null)]
    [InlineData(true, false, CombatPhase.Complete, "Team 3")]
    [InlineData(true, true, CombatPhase.Complete, "Mutual destruction")]
    public void VictoryRequiresAtMostOneActiveTeam(bool secondDown, bool thirdDown, CombatPhase expected, string? winner)
    {
        var definition = new ShipDefinition { Id = "ship", Name = "Ship", Shields = new QuadrantShields() };
        var state = new GameState { GameId = "test", RoomCode = "TEST", Phase = CombatPhase.Gunnery };
        for (int team = 0; team < 3; team++)
        {
            var ship = definition.Instantiate($"ship-{team}", new Hex(team, 0), Facing.East, team);
            ship.IsDisabled = team == 0 || (team == 1 && secondDown) || (team == 2 && thirdDown);
            state.Ships.Add(ship);
        }
        new CombatEngine(state, new DiceRoller(1)).AdvancePhase();
        Assert.Equal(expected, state.Phase);
        Assert.Equal(winner, state.Winner);
    }
}
