using Quadrant.RulesEngine;
using Quadrant.Server.Sessions;

namespace Quadrant.RulesEngine.Tests;

public class TeamSetupTests
{
    [Fact]
    public void DuplicateDisplayNamesRemainStableAcrossTeamsAndDestruction()
    {
        var definition = new ShipDefinition { Id = "ship", Name = "Ship", Shields = new QuadrantShields() };
        var other = new ShipDefinition { Id = "other", Name = "Other", Shields = new QuadrantShields() };
        var state = new GameState { GameId = "test", RoomCode = "test" };
        var first = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var second = definition.Instantiate("b", new Hex(1, 0), Facing.West, 1);
        var unique = other.Instantiate("c", new Hex(2, 0), Facing.West, 1);
        state.Ships.AddRange([first, second, unique]);
        Assert.Equal("Ship #1", state.DisplayName(first));
        Assert.Equal("Ship #2", state.DisplayName(second));
        Assert.Equal("Other", state.DisplayName(unique));
        first.IsDestroyed = true;
        Assert.Equal("Ship #2", state.DisplayName(second));
    }
    [Theory]
    [InlineData(14, 3, 0)]
    [InlineData(13, 1, 0)]
    [InlineData(9, 0, -4)]
    public void TierThreeBackOffRoundsDownBeforeResolving(int roll, int maxDistance, int penalty)
    {
        var definition = new ShipDefinition { Id = "ship", Name = "Ship", Tier = 3, Speed = 6,
            Shields = new QuadrantShields(), Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["piloting"] = 0 }, 3)] };
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        var ship = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        state.Ships.Add(ship);
        state.Ships.Add(definition.Instantiate("b", new Hex(10, 0), Facing.West, 1));
        var engine = new CombatEngine(state, new BackOffDice(roll));
        Assert.True(engine.StartStunt("Back Off").IsValid);
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("vs DC 14"));
        Assert.Equal(maxDistance, engine.GetValidMoves().Max(m => Math.Abs(m.Position.Q)));
        Assert.Equal(penalty, ship.RoundAcTlModifier);
    }
    [Theory]
    [InlineData(13, -3, 0)]
    [InlineData(12, -1, 0)]
    [InlineData(8, 0, -4)]
    public void BackOffResolvesCheckMovementAndPenalty(int roll, int q, int penalty)
    {
        var def = new ShipDefinition { Id = "ship", Name = "Ship", Tier = 2, Speed = 8,
            Shields = new QuadrantShields(), Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["piloting"] = 0 }, 2)] };
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        var ship = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        state.Ships.Add(ship);
        state.Ships.Add(def.Instantiate("b", new Hex(5, 0), Facing.West, 1));
        var engine = new CombatEngine(state, new BackOffDice(roll));
        var result = engine.StartStunt("Back Off");
        Assert.True(result.IsValid);
        Assert.Equal(new Hex(0, 0), ship.Position);
        var option = Assert.Single(engine.GetValidMoves(), m => m.Position == new Hex(q, 0));
        Assert.True(engine.TryMoveTo(option.Position, option.FinalFacing).IsValid);
        Assert.Equal(new Hex(q, 0), ship.Position);
        Assert.Equal(Facing.East, ship.Facing);
        Assert.Equal(penalty, ship.RoundAcTlModifier);
    }

    [Theory]
    [InlineData(18, 2, Facing.West)]
    [InlineData(17, 2, Facing.East)]
    public void FlipAndBurnSelectsForwardMovementAfterCheck(int roll, int distance, Facing facing)
    {
        var def = new ShipDefinition { Id = "ship", Name = "Ship", Tier = 2, Speed = 8,
            Shields = new QuadrantShields(), Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["piloting"] = 0 }, 2)] };
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        var ship = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        state.Ships.Add(ship);
        state.Ships.Add(def.Instantiate("b", new Hex(10, 0), Facing.West, 1));
        var engine = new CombatEngine(state, new BackOffDice(roll));
        Assert.True(engine.StartStunt("Flip and Burn").IsValid);
        Assert.False(engine.StartStunt("Back Off").IsValid);
        Assert.Equal(5, engine.GetValidMoves().Count);
        Assert.All(engine.GetValidMoves(), m => Assert.Equal(facing, m.FinalFacing));
        var option = Assert.Single(engine.GetValidMoves(), m => m.Position == new Hex(distance, 0));
        Assert.Equal(facing, option.FinalFacing);
        Assert.True(engine.TryMoveTo(option.Position, option.FinalFacing).IsValid);
        Assert.Equal(facing, ship.Facing);
    }

    [Theory]
    [InlineData(13, 2)]
    [InlineData(12, 0)]
    [InlineData(8, -2)]
    public void EvadeUsesNormalMovementAndAppliesDefenseUntilNextRound(int roll, int modifier)
    {
        var def = new ShipDefinition { Id = "ship", Name = "Ship", Tier = 2, Speed = 8,
            Shields = new QuadrantShields(), Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["piloting"] = 0 })] };
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        var ship = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        state.Ships.Add(ship);
        state.Ships.Add(def.Instantiate("b", new Hex(20, 0), Facing.West, 1));
        var engine = new CombatEngine(state, new BackOffDice(roll));
        var normal = engine.GetValidMoves().Select(m => (m.Position, m.FinalFacing)).ToHashSet();
        Assert.True(engine.StartStunt("Evade").IsValid);
        Assert.True(normal.SetEquals(engine.GetValidMoves().Select(m => (m.Position, m.FinalFacing))));
        Assert.Equal(modifier, ship.RoundAcTlModifier);
        Assert.Contains(0, ship.CrewActionsUsed);
        ship.BeginRound();
        Assert.Equal(0, ship.RoundAcTlModifier);
    }

    [Theory]
    [InlineData(13, true)]
    [InlineData(12, false)]
    public void SlideUsesDiagonalOnSuccessAndHalfSpeedForwardOnFailure(int roll, bool success)
    {
        var def = new ShipDefinition { Id = "ship", Name = "Ship", Tier = 2, Speed = 8,
            Shields = new QuadrantShields(), Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["piloting"] = 0 })] };
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        var ship = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        state.Ships.Add(ship);
        state.Ships.Add(def.Instantiate("b", new Hex(20, 0), Facing.West, 1));
        var engine = new CombatEngine(state, new BackOffDice(roll));
        Assert.True(engine.StartStunt("Slide").IsValid);
        var moves = engine.GetValidMoves();
        Assert.All(moves, m => Assert.Equal(Facing.East, m.FinalFacing));
        Assert.Equal(success ? 17 : 5, moves.Count);
        Assert.Contains(moves, m => m.Position == ship.Position.Step(success ? 1 : 2, success ? 8 : 4));
        if (success) Assert.Contains(moves, m => m.Position == ship.Position.Step(3, 8));
        Assert.True(engine.TryMoveTo(moves.Last().Position, moves.Last().FinalFacing).IsValid);
        Assert.Equal(Facing.East, ship.Facing);
    }

    private sealed class BackOffDice(int roll) : IDiceRoller
    {
        public int RollD20() => roll;
        public int Roll(int sides) => 1;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }
    [Fact]
    public void GunnerCanFireOnlyOneWeaponPerRound()
    {
        var weapon = new Weapon { Name = "Cannon", Arc = FiringArc.Forward, Damage = DamageExpression.From("1") };
        var definition = new ShipDefinition { Id = "armed", Name = "Armed", Shields = new QuadrantShields(),
            Weapons = [weapon, weapon], Crew = [new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 5 })] };
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery, ActionOrder = ["a", "b"] };
        state.Ships.Add(definition.Instantiate("a", new Hex(0, 0), Facing.East, 0));
        state.Ships.Add(definition.Instantiate("b", new Hex(1, 0), Facing.West, 1));
        var engine = new CombatEngine(state, new DiceRoller(1));
        Assert.NotNull(engine.ApplyFire(new FireAction(0, "b")));
        Assert.Empty(engine.GetValidTargets());
        Assert.Null(engine.ApplyFire(new FireAction(1, "b")));
    }

    [Fact]
    public void LonePilotCanMoveAndTakeOneSnapShot()
    {
        var weapon = new Weapon { Name = "Cannon", Arc = FiringArc.AllArcs, Damage = DamageExpression.From("1") };
        var definition = new ShipDefinition { Id = "pilot", Name = "Pilot", Speed = 4, Shields = new QuadrantShields(),
            Weapons = [weapon, weapon], Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["gunnery"] = 5 })] };
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        var ship = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        state.Ships.Add(ship);
        state.Ships.Add(definition.Instantiate("b", new Hex(4, 0), Facing.West, 1));
        var engine = new CombatEngine(state, new DiceRoller(1));
        Assert.True(engine.TryMoveTo(new Hex(1, 0), Facing.East).IsValid);
        engine.SkipMove();
        var result = engine.ApplyFire(new FireAction(0, "b"));
        Assert.NotNull(result);
        Assert.Equal(3, result!.Modifier);
        Assert.Empty(engine.GetValidTargets());
    }
    [Fact]
    public void CrewSetupValidatesRolesAndKeepsAssignmentsForNextRound()
    {
        var definition = new ShipDefinition { Id = "crew-test", Name = "Crew Test", Shields = new QuadrantShields(),
            Crew = [new CrewDefinition("Pilot", new Dictionary<string, int>()), new CrewDefinition("Gunner", new Dictionary<string, int>())] };
        var state = new GameState { GameId = "test", RoomCode = "test" };
        var first = definition.Instantiate("first", new Hex(0, 0), Facing.East, 0);
        var second = definition.Instantiate("second", new Hex(2, 0), Facing.West, 1);
        state.Ships.AddRange([first, second]);
        var engine = new CombatEngine(state, new DiceRoller(1));
        Assert.True(engine.StartCombat());
        Assert.Equal(CombatPhase.CrewSetup, state.Phase);
        var invalid = new Dictionary<string, IReadOnlyList<string>> { ["first"] = ["Pilot", "Pilot"], ["second"] = ["Pilot", "Gunner"] };
        Assert.NotNull(engine.AssignRoles(invalid));
        Assert.Equal(new[] { "Pilot", "Gunner" }, first.CrewRoles);
        var chosen = new Dictionary<string, IReadOnlyList<string>> { ["first"] = ["Engineer", "Gunner"], ["second"] = ["Pilot", "Gunner"] };
        Assert.Null(engine.AssignRoles(chosen));
        Assert.Equal(CombatPhase.Engineering, state.Phase);
        state.Phase = CombatPhase.Gunnery;
        engine.AdvancePhase();
        Assert.Equal(CombatPhase.CrewSetup, state.Phase);
        Assert.Equal(new[] { "Engineer", "Gunner" }, first.CrewRoles);
    }
    [Theory]
    [InlineData("grave-casket", 3)]
    [InlineData("deaths-curtain-necrofighter", 1)]
    [InlineData("barrow-boneshard", 1)]
    public void CatalogAssignsCrewRanksFromShipTier(string shipId, int ranks)
    {
        var ship = Catalog().Get(shipId);
        Assert.NotNull(ship);
        Assert.NotEmpty(ship!.Crew);
        Assert.All(ship.Crew, crew => Assert.Equal(ranks, crew.Ranks));
    }
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
        var selections = new List<ShipSelection> { new("barrow-boneshard", 2), new("grave-casket", 2) };
        selections.AddRange(Enumerable.Repeat(new ShipSelection("barrow-boneshard", 2), 45));
        selections.Add(new("deaths-curtain-necrofighter", 7));
        for (int seed = 0; seed < 10; seed++)
        {
            var ships = catalog.CreateShips(selections, new Random(seed));
            var firstTeam = ships.Where(s => s.TeamId == 2).ToList();
            var secondTeam = ships.Where(s => s.TeamId == 7).ToList();
            Assert.Equal(ships.Count, ships.Select(s => s.Position).Distinct().Count());
            Assert.Single(firstTeam.Select(s => s.Facing).Distinct());
            Assert.Equal("grave-casket", firstTeam[0].Definition.Id);
            Assert.InRange(firstTeam[0].Position.DistanceTo(secondTeam[0].Position), 8, 23);
            for (int i = 1; i < firstTeam.Count; i++)
                Assert.Contains(firstTeam.Take(i), other => other.Position.DistanceTo(firstTeam[i].Position) <= 3);
        }
    }

    [Fact]
    public void ThreeTeamsStartSeparatedInATriangle()
    {
        var catalog = Catalog();
        var selections = Enumerable.Range(0, 3).Select(i => new ShipSelection("grave-casket", i)).ToList();
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
        var selections = Enumerable.Range(0, 8).Select(i => new ShipSelection("grave-casket", i * 2)).ToList();
        var ships = catalog.CreateShips(selections, new Random(2));
        Assert.Equal(8, ships.Select(s => s.TeamId).Distinct().Count());
        Assert.Equal(8, ships.Select(s => s.Position).Distinct().Count());
        Assert.Throws<ArgumentException>(() => catalog.CreateShips([new("missing", 0), new("grave-casket", 1)]));
        Assert.Throws<ArgumentException>(() => catalog.CreateShips([new("grave-casket", 0)]));
    }

    [Theory]
    [InlineData(false, false, CombatPhase.CrewSetup, null)]
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
