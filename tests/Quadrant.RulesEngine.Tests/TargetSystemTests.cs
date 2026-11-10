using Quadrant.RulesEngine;

namespace Quadrant.RulesEngine.Tests;

public class TargetSystemTests
{
    private static (CombatEngine Engine, GameState State, ShipInstance Actor, ShipInstance Enemy) Create(int scienceRoll = 15, int scienceBonus = 20)
    {
        var gun = new Weapon { Name = "Laser", Arc = FiringArc.Forward, Damage = DamageExpression.From("1") };
        var definition = new ShipDefinition { Id = "a", Name = "Science ship", Tier = 1, HullPoints = 100,
            HasSensors = true, SensorRangeHexes = 2, CriticalThreshold = 0, DamageThreshold = 0,
            Shields = new QuadrantShields { Forward = 0, Starboard = 0, Aft = 0, Port = 0 }, Weapons = [gun],
            Crew = [new CrewDefinition("Science Officer", new Dictionary<string, int> { ["computers"] = scienceBonus }),
                new CrewDefinition("Science Officer", new Dictionary<string, int> { ["computers"] = scienceBonus }),
                new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 20 })] };
        var actor = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var enemy = (definition with { Id = "b", Name = "Enemy", Crew = [],
            Weapons = [gun with { Arc = FiringArc.Aft }, gun with { Arc = FiringArc.Port }] })
            .Instantiate("b", new Hex(1, 0), Facing.West, 1);
        var state = new GameState { GameId = "test", RoomCode = "TEST", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        state.Ships.AddRange([actor, enemy]);
        return (new CombatEngine(state, new Dice(scienceRoll)), state, actor, enemy);
    }

    [Fact]
    public void TargetingWeaponsStoresOnlyTheSystemAndRandomizesTheArcWhenCriticalDamageResolves()
    {
        var (engine, _, actor, enemy) = Create();
        Assert.Null(engine.ApplyScience(new ScienceAction("a", 0, "Target System", "b", "WeaponsArray")));
        Assert.Equal(ShipSystem.WeaponsArray, Assert.Single(engine.GetTargetedSystems("a")).System);
        Assert.Empty(enemy.Systems.WeaponArcs);
        Assert.Contains(0, actor.CrewActionsUsed);
        engine.SkipMove();
        engine.SkipMove();
        Assert.True(engine.ApplyFire(new FireAction(0, "b"))!.IsHit);
        Assert.Empty(enemy.Systems.WeaponArcs); // Damage is deferred until Gunnery ends.
        engine.EndGunneryTurn();
        engine.EndGunneryTurn();
        var critical = Assert.Single(Assert.Single(enemy.DamageReport).CriticalEffects);
        Assert.Equal(ShipSystem.WeaponsArray, critical.System);
        Assert.Equal(FiringArc.Port, critical.WeaponArc); // The second eligible arc, selected by the dice.
        Assert.Equal(SystemCondition.None, enemy.Systems.GetWeaponArc(FiringArc.Aft));
        Assert.Equal(SystemCondition.Glitching, enemy.Systems.GetWeaponArc(FiringArc.Port));
        Assert.Empty(engine.GetTargetedSystems("a"));
    }

    [Fact]
    public void OutOfRangeAndAlreadyLockedTargetsDoNotSpendAnotherOfficerAction()
    {
        var (engine, _, actor, enemy) = Create();
        enemy.Position = new Hex(11, 0);
        Assert.NotNull(engine.ApplyScience(new ScienceAction("a", 0, "Target System", "b", "WeaponsArray")));
        Assert.Empty(actor.CrewActionsUsed);
        enemy.Position = new Hex(10, 0);
        Assert.Null(engine.ApplyScience(new ScienceAction("a", 0, "Target System", "b", "WeaponsArray")));
        Assert.NotNull(engine.ApplyScience(new ScienceAction("a", 1, "Target System", "b", "Engines")));
        Assert.DoesNotContain(1, actor.CrewActionsUsed);
        Assert.Equal(ShipSystem.WeaponsArray, Assert.Single(engine.GetTargetedSystems("a")).System);
    }

    [Fact]
    public void FailedTargetingSpendsTheOfficerActionWithoutAddingALock()
    {
        var (engine, _, actor, _) = Create(scienceRoll: 1, scienceBonus: 0);
        Assert.Null(engine.ApplyScience(new ScienceAction("a", 0, "Target System", "b", "WeaponsArray")));
        Assert.Contains(0, actor.CrewActionsUsed);
        Assert.Empty(engine.GetTargetedSystems("a"));
    }

    [Theory]
    [InlineData(false, false, 75)]
    [InlineData(true, true, 85)]
    public void DivertScienceAddsTwoToTargetSystemChecksForTheRound(bool diverted, bool succeeds, int chance)
    {
        var (engine, _, actor, _) = Create(scienceRoll: 5, scienceBonus: 0);
        actor.DivertScience = diverted;

        Assert.Equal(chance, engine.GetScienceSuccessChance(actor, 0, engine.State.GetShip("b")));
        Assert.Null(engine.ApplyScience(new ScienceAction("a", 0, "Target System", "b", "WeaponsArray")));
        Assert.Equal(succeeds, engine.GetTargetedSystems("a").Any());
    }

    private sealed class Dice(int scienceRoll) : IDiceRoller
    {
        private int _checks;
        public int RollD20() => _checks++ == 0 ? scienceRoll : 20;
        public int Roll(int sides) => sides;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }
}
