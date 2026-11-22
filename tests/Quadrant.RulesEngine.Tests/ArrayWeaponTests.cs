using Quadrant.RulesEngine;
using Quadrant.Server.Sessions;
using System.Text.Json;

namespace Quadrant.RulesEngine.Tests;

public class ArrayWeaponTests
{
    private sealed class Dice : IDiceRoller
    {
        public int DamageRolls;
        public int RollD20() => 15;
        public int Roll(int sides) { DamageRolls++; return DamageRolls; }
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }

    [Fact]
    public void LinkedArrayHitsAllShipsWithOneDamageRollAndSurvivesSnapshot()
    {
        var array = new Weapon { Name = "Array", Arc = FiringArc.Forward, Array = true,
            LinkedGroup = "pair", Damage = DamageExpression.From("1d6"), RangeIncrement = 5 };
        var definition = new ShipDefinition { Id = "a", Name = "Attacker", HullPoints = 100, BaseAC = 10,
            Shields = new QuadrantShields(), Weapons = [array, array],
            Crew = [new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 8 }, 1)] };
        var victim = new ShipDefinition { Id = "b", Name = "Target", HullPoints = 100, BaseAC = 10, DamageThreshold = 0,
            Shields = new QuadrantShields { Forward = 0, Aft = 0, Port = 0, Starboard = 0 } };
        var attacker = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var enemy = victim.Instantiate("b", new Hex(2, 0), Facing.West, 1);
        var ally = victim.Instantiate("c", new Hex(3, 0), Facing.West, 0);
        var outside = victim.Instantiate("d", new Hex(-1, 0), Facing.East, 1);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery, ActionOrder = ["a", "b", "c", "d"] };
        state.Ships.AddRange([attacker, enemy, ally, outside]);
        var dice = new Dice();
        var engine = new CombatEngine(state, dice);
        var options = engine.GetValidTargets();
        Assert.Equal(2, options.Count);
        Assert.Contains(options, o => o.TargetShipId == "c");
        Assert.All(options, o => Assert.Equal(75, o.HitChancePercent));
        Assert.NotNull(engine.ApplyFire(new FireAction(0, "b")));
        Assert.Single(attacker.CrewActionsUsed);
        var snapshot = engine.CaptureSnapshot();
        Assert.Equal(2, snapshot.PendingHits.Count);
        Assert.NotNull(snapshot.PendingHits[0].FiringId);
        Assert.Equal(snapshot.PendingHits[0].FiringId, snapshot.PendingHits[1].FiringId);
        var restored = new CombatEngine(state, dice);
        restored.RestoreSnapshot(JsonSerializer.Deserialize<CombatEngineSnapshot>(JsonSerializer.Serialize(snapshot))!);
        restored.AdvancePhase();
        Assert.Equal(2, dice.DamageRolls);
        Assert.Equal(97, enemy.HullPoints);
        Assert.Equal(97, ally.HullPoints);
        Assert.Equal(100, outside.HullPoints);
    }

    [Fact]
    public void CatalogLoadsArrayForMercilessBlade()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Quadrant.Server"));
        var catalog = new ShipCatalogService();
        catalog.LoadWeapons(Path.Combine(root, "weapons"));
        catalog.LoadShips(Path.Combine(root, "ships"));
        var blade = catalog.Get("merciless-blade")!;
        Assert.Equal(6, blade.Weapons.Count(w => w.Array));
    }
}
