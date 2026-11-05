using Quadrant.RulesEngine;

namespace Quadrant.RulesEngine.Tests;

public class EngineeringTests
{
    private sealed class FixedDice(int d20 = 15) : IDiceRoller
    {
        public int RollD20() => d20;
        public int Roll(int sides) => 1;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }

    private static (CombatEngine Engine, ShipInstance Ship) Scenario()
    {
        var def = new ShipDefinition { Id = "ship", Name = "Ship", Tier = 3, Speed = 6, HullPoints = 50,
            PowerCorePcu = 100, Shields = new QuadrantShields { Forward = 10, Starboard = 10, Aft = 10, Port = 10 },
            Crew = [new CrewDefinition("Engineer", new Dictionary<string, int> { ["engineering"] = 8 }, 3),
                    new CrewDefinition("Engineer", new Dictionary<string, int> { ["engineering"] = 8 }, 3)] };
        var ship = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Engineering };
        state.Ships.Add(ship);
        return (new CombatEngine(state, new FixedDice()), ship);
    }

    [Fact]
    public void ShieldDivertRequiresPlayerAllocationAndCapsTotalRatherThanInitialQuadrants()
    {
        var (engine, ship) = Scenario();
        ship.Shields.Forward = 9;
        ship.Shields.Starboard = ship.Shields.Aft = ship.Shields.Port = 0;
        Assert.NotNull(engine.ApplyEngineering(new EngineerAction("a", 0, "Divert", "Shields", new ShieldAllocation(4, 0, 0, 0))));
        Assert.Empty(ship.CrewActionsUsed);
        Assert.Null(engine.ApplyEngineering(new EngineerAction("a", 0, "Divert", "Shields", new ShieldAllocation(5, 0, 0, 0))));
        Assert.Equal(14, ship.Shields.Forward);
        Assert.Equal(0, ship.Shields.Port);
        Assert.NotNull(engine.ApplyEngineering(new EngineerAction("a", 1, "Divert", "Engines", null)));
    }

    [Fact]
    public void HoldItTogetherWorksThroughWreckedCoreAndExpiresAtNextRound()
    {
        var (engine, ship) = Scenario();
        for (int i = 0; i < 3; i++) ship.Systems.ApplyCritical(ShipSystem.PowerCore);
        Assert.Null(engine.ApplyEngineering(new EngineerAction("a", 0, "Hold It Together", "PowerCore", null)));
        Assert.Equal(SystemCondition.Wrecked, ship.Systems.Get(ShipSystem.PowerCore));
        Assert.Equal(SystemCondition.Glitching, ship.Systems.GetEffective(ShipSystem.PowerCore));
        Assert.Equal(-2, ship.Systems.GetActionPenalty(ShipSystem.PowerCore));
        ship.BeginRound();
        Assert.Equal(SystemCondition.Wrecked, ship.Systems.GetEffective(ShipSystem.PowerCore));
    }

    [Fact]
    public void HoldingWreckedEnginesRestoresPilotMovementUntilRoundEnds()
    {
        var def = new ShipDefinition { Id = "ship", Name = "Ship", Tier = 2, Speed = 6, Shields = new QuadrantShields(),
            Crew = [new CrewDefinition("Pilot", new Dictionary<string, int>(), 2),
                    new CrewDefinition("Engineer", new Dictionary<string, int> { ["engineering"] = 20 }, 2)] };
        var ship = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Engineering, ActionOrder = ["a"] };
        state.Ships.Add(ship);
        for (int i = 0; i < 3; i++) ship.Systems.ApplyCritical(ShipSystem.Engines);
        var engine = new CombatEngine(state, new FixedDice());
        Assert.Null(engine.ApplyEngineering(new EngineerAction("a", 1, "Hold It Together", "Engines", null)));
        state.Phase = CombatPhase.Helm;
        Assert.Contains(engine.GetValidMoves(), move => move.Position == new Hex(1, 0));
        ship.BeginRound();
        Assert.Empty(engine.GetValidMoves());
    }

    [Fact]
    public void DivertEnginesBoostsSpeedForOneRoundAndWeaponDiceTreatOnesAsTwos()
    {
        var (engine, ship) = Scenario();
        Assert.Null(engine.ApplyEngineering(new EngineerAction("a", 0, "Divert", "Engines", null)));
        Assert.Equal(8, ship.Speed);
        Assert.Equal(4, DamageExpression.From("2d6").Roll(new FixedDice(), divertWeapons: true));
        ship.BeginRound();
        Assert.Equal(6, ship.Speed);
    }

    [Fact]
    public void DivertWeaponsImprovesDamageWhenGunneryResolves()
    {
        var weapon = new Weapon { Name = "Cannon", Arc = FiringArc.Forward, Damage = DamageExpression.From("2d6") };
        var def = new ShipDefinition { Id = "ship", Name = "Ship", Tier = 2, HullPoints = 50, DamageThreshold = 0, CriticalThreshold = 20,
            Shields = new QuadrantShields { Forward = 0, Starboard = 0, Aft = 0, Port = 0 }, Weapons = [weapon],
            Crew = [new CrewDefinition("Engineer", new Dictionary<string, int> { ["engineering"] = 8 }, 2),
                    new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 50 }, 2)] };
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Engineering, ActionOrder = ["a", "b"] };
        var attacker = def.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var target = def.Instantiate("b", new Hex(1, 0), Facing.West, 1);
        state.Ships.AddRange([attacker, target]);
        var engine = new CombatEngine(state, new FixedDice());
        Assert.Null(engine.ApplyEngineering(new EngineerAction("a", 0, "Divert", "Weapons", null)));
        state.Phase = CombatPhase.Gunnery;
        Assert.True(engine.ApplyFire(new FireAction(0, "b"))!.IsHit);
        Assert.Equal(50, target.HullPoints);
        engine.EndGunneryTurn();
        engine.EndGunneryTurn();
        Assert.Equal(46, target.HullPoints);
    }

    [Fact]
    public void FailedCheckConsumesTheEngineerAndShipAction()
    {
        var (_, ship) = Scenario();
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Engineering };
        state.Ships.Add(ship);
        var engine = new CombatEngine(state, new FixedDice(1));
        Assert.Null(engine.ApplyEngineering(new EngineerAction("a", 0, "Divert", "Weapons", null)));
        Assert.False(ship.DivertWeapons);
        Assert.Contains(0, ship.CrewActionsUsed);
        Assert.NotNull(engine.ApplyEngineering(new EngineerAction("a", 1, "Divert", "Weapons", null)));
    }
}
