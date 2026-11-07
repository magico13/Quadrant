using Quadrant.RulesEngine;
using Q = Quadrant.RulesEngine.Quadrant;

namespace Quadrant.RulesEngine.Tests;

public class CriticalDamageTests
{
    [Fact]
    public void TargetedCriticalOnWreckedEnginesMovesToPowerCore()
    {
        var target = MakeShip(Facing.West, new Hex(1, 0));
        target.Shields.Forward = 0;
        for (int i = 0; i < 3; i++) target.Systems.ApplyCritical(ShipSystem.Engines);
        var result = CombatResolver.ApplyDamage(target, Q.Forward, 1, 20, new ScriptedDice(20, 70), ShipSystem.Engines);
        Assert.Equal(ShipSystem.PowerCore, Assert.Single(result.CriticalEffects).System);
        Assert.Equal(SystemCondition.Glitching, target.Systems.Get(ShipSystem.PowerCore));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CriticalPastWreckedPowerCoreReportsCrewInjury(bool targeted)
    {
        var target = MakeShip(Facing.West, new Hex(1, 0));
        target.Shields.Forward = 0;
        for (int i = 0; i < 3; i++) target.Systems.ApplyCritical(ShipSystem.PowerCore);
        var result = CombatResolver.ApplyDamage(target, Q.Forward, 1, 20, new ScriptedDice(20, 90), targeted ? ShipSystem.PowerCore : null);
        Assert.Null(Assert.Single(result.CriticalEffects).System);
        Assert.Contains("crew injury requires manual resolution (1 damage, DC 20 Reflex for half)", result.Description);
    }

    [Fact]
    public void NaturalTwentyAndTwoThresholdCrossingsCauseThreeCriticalEffects()
    {
        var target = MakeShip(Facing.West, new Hex(1, 0), criticalThreshold: 7);
        target.Shields.Forward = 0;
        target.TotalHullDamageTaken = 1;
        var result = CombatResolver.ApplyDamage(target, Q.Forward, 16, 20, new ScriptedDice(20, 50));
        Assert.Equal(3, result.CriticalEffects.Count);
        Assert.Equal(SystemCondition.Wrecked, target.Systems.Get(ShipSystem.WeaponsArray));
    }
    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    public void CriticalThreshold_RequiresDamageToExceedThreshold(int previousDamage, bool critical)
    {
        var attacker = MakeShip(Facing.East, new Hex(0, 0));
        var target = MakeShip(Facing.West, new Hex(1, 0));
        target.Shields.Forward = 0;
        target.TotalHullDamageTaken = previousDamage;
        var result = CombatResolver.ResolveAttack(attacker, target, MakeWeapon(), new ScriptedDice(15, 50));
        Assert.Equal(critical, result.CriticalScored);
    }

    [Fact]
    public void Attack_AppliesRangePenalty()
    {
        var attacker = MakeShip(Facing.East, new Hex(0, 0), gunnerBonus: 0);
        var target = MakeShip(Facing.West, new Hex(4, 0));
        var result = CombatResolver.ResolveAttack(attacker, target, MakeWeapon(), new ScriptedDice(10, 50));
        Assert.False(result.IsHit);
    }

    [Fact]
    public void NaturalTwenty_HitsEvenWhenTotalIsBelowAc()
    {
        var attacker = MakeShip(Facing.East, new Hex(0, 0));
        var target = MakeShip(Facing.West, new Hex(1, 0));
        target.Shields.Forward = 0;
        target = MakeShip(Facing.West, new Hex(1, 0), baseAc: 40);

        var result = CombatResolver.ResolveAttack(attacker, target, MakeWeapon(), new ScriptedDice(20, 50));

        Assert.True(result.IsHit);
        Assert.Contains("natural 20 hits automatically", result.Description);
    }

    [Fact]
    public void NaturalOne_MissesEvenWhenTotalMeetsAc()
    {
        var attacker = MakeShip(Facing.East, new Hex(0, 0), gunnerBonus: 30);
        var target = MakeShip(Facing.West, new Hex(1, 0));

        var result = CombatResolver.ResolveAttack(attacker, target, MakeWeapon(), new ScriptedDice(1, 50));

        Assert.False(result.IsHit);
    }

    [Fact]
    public void CheckFormatting_UsesSubtractionForNegativeModifiers()
    {
        Assert.Equal("7-3=4", CombatResolver.FormatCheck(7, -3, 4));
        Assert.Equal("7+3=10", CombatResolver.FormatCheck(7, 3, 10));
    }

    [Fact]
    public void Gunnery_ResolvesWeaponsIndividuallyAndLocksFiredWeapon()
    {
        var target = MakeShip(Facing.West, new Hex(1, 0), id: "target", teamId: 1);
        var firstWeapon = MakeWeapon();
        var secondWeapon = MakeWeapon() with { Name = "Second Cannon" };
        var attacker = MakeShip(Facing.East, new Hex(0, 0), id: "attacker", teamId: 0, weapons: [firstWeapon, secondWeapon]);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery, ActionOrder = [attacker.InstanceId, target.InstanceId] };
        state.Ships.AddRange([attacker, target]);
        var engine = new CombatEngine(state, new SequenceDice([1, 15]));

        var first = engine.ApplyFire(new FireAction(0, target.InstanceId));
        Assert.NotNull(first);
        Assert.False(first!.IsHit);
        Assert.Equal(50, target.HullPoints);
        Assert.Contains(0, engine.FiredWeaponIndices);
        Assert.DoesNotContain(0, engine.GetValidTargets().Select(o => o.WeaponIndex));

        var second = engine.ApplyFire(new FireAction(1, target.InstanceId));
        Assert.NotNull(second);
        Assert.True(second!.IsHit);
        Assert.Equal(50, target.HullPoints);
    }

    [Fact]
    public void ArcBoundary_WithWeaponsInTwoArcs_RequiresOneChoiceAndLocksOtherArc()
    {
        var target = MakeShip(Facing.West, new Hex(1, 1), id: "target", teamId: 1);
        var forward = MakeWeapon() with { Name = "Forward Cannon", Arc = FiringArc.Forward };
        var starboard = MakeWeapon() with { Name = "Starboard Cannon", Arc = FiringArc.Starboard };
        var attacker = MakeShip(Facing.East, new Hex(0, 0), id: "attacker", teamId: 0, weapons: [forward, starboard]);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery, ActionOrder = ["attacker", "target"] };
        state.Ships.AddRange([attacker, target]);
        var engine = new CombatEngine(state, new SequenceDice([15]));

        var options = engine.GetValidTargets();
        Assert.Equal(2, options.Count);
        Assert.All(options, option => Assert.True(option.ArcChoiceRequired));
        Assert.Equal(new[] { FiringArc.Forward, FiringArc.Starboard }, options[0].ArcChoices.Order().ToArray());
        Assert.Null(engine.ApplyFire(new FireAction(0, "target")));

        Assert.NotNull(engine.ApplyFire(new FireAction(0, "target"), FiringArc.Forward));
        Assert.DoesNotContain(engine.GetValidTargets(), option => option.TargetShipId == "target" && option.Arc == FiringArc.Starboard);
    }

    [Fact]
    public void ArcBoundary_WithOnlyOneAvailableArc_AutoSelectsIt()
    {
        var target = MakeShip(Facing.West, new Hex(1, 1), id: "target", teamId: 1);
        var forward = MakeWeapon() with { Arc = FiringArc.Forward };
        var attacker = MakeShip(Facing.East, new Hex(0, 0), id: "attacker", teamId: 0, weapons: [forward]);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery, ActionOrder = ["attacker", "target"] };
        state.Ships.AddRange([attacker, target]);
        var engine = new CombatEngine(state, new SequenceDice([15]));

        var option = Assert.Single(engine.GetValidTargets());
        Assert.False(option.ArcChoiceRequired);
        Assert.Single(option.ArcChoices);
        Assert.NotNull(engine.ApplyFire(new FireAction(0, "target")));
    }

    [Fact]
    public void Gunnery_DamageWaitsUntilBothShipsHaveFired()
    {
        var first = MakeShip(Facing.East, new Hex(0, 0));
        var second = MakeShip(Facing.West, new Hex(1, 0));
        first = MakeArmed(first, "first", 0);
        second = MakeArmed(second, "second", 1);
        first.HullPoints = second.HullPoints = 4;
        first.Shields.Forward = second.Shields.Forward = 0;
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery, ActionOrder = ["first", "second"] };
        state.Ships.AddRange([first, second]);
        var engine = new CombatEngine(state, new ScriptedDice(15, 50));
        engine.ApplyGunneryTurn([new FireAction(0, "second")]);
        Assert.Equal(4, second.HullPoints);
        engine.ApplyGunneryTurn([new FireAction(0, "first")]);
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("first (Team 1) fires Test Cannon at second (Team 2):") && e.Message.Contains("HIT"));
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("second (Team 2) fires Test Cannon at first (Team 1):") && e.Message.Contains("HIT"));
        Assert.DoesNotContain(engine.LastEvents, e => e.Message.Contains("resolving at the end"));
        Assert.True(first.IsDisabled);
        Assert.True(second.IsDisabled);
        Assert.Single(engine.LastEvents, e => e.Message.Contains("first (Team 1) is disabled!"));
        Assert.Single(engine.LastEvents, e => e.Message.Contains("second (Team 2) is disabled!"));
        Assert.Equal("Mutual destruction", state.Winner);
    }

    [Fact]
    public void GunneryLog_NumbersDuplicateShipNames()
    {
        var first = MakeArmed(MakeShip(Facing.East, new Hex(0, 0)), "same-1", 0, "Pegasus");
        var second = MakeArmed(MakeShip(Facing.West, new Hex(1, 0)), "same-2", 1, "Pegasus");
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery, ActionOrder = ["same-1", "same-2"] };
        state.Ships.AddRange([first, second]);
        var engine = new CombatEngine(state, new ScriptedDice(15, 50));
        engine.ApplyGunneryTurn([new FireAction(0, "same-2")]);
        engine.ApplyGunneryTurn([new FireAction(0, "same-1")]);
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("Pegasus #1 (Team 1) fires Test Cannon at Pegasus #2 (Team 2)"));
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("Pegasus #2 (Team 2) fires Test Cannon at Pegasus #1 (Team 1)"));
    }

    private static ShipInstance MakeArmed(ShipInstance source, string id, int team, string? name = null)
    {
        var definition = new ShipDefinition { Id = id, Name = name ?? id, HullPoints = 50, BaseAC = 12,
            DamageThreshold = 0, CriticalThreshold = 11, Shields = new QuadrantShields(), Weapons = [MakeWeapon()] };
        return new ShipInstance { InstanceId = id, Definition = definition, TeamId = team,
            Position = source.Position, Facing = source.Facing, HullPoints = 50, Shields = new QuadrantShields() };
    }

    /// <summary>
    /// A natural 20 that is fully absorbed by shields must NOT score critical damage.
    /// Per the rules: "A starship takes critical damage from an attack only if that
    /// attack deals damage to the ship's Hull Points, even if the result of the gunnery
    /// check is a natural 20."
    /// </summary>
    [Fact]
    public void Natural20_FullyAbsorbedByShields_NoCriticalDamage()
    {
        var attacker = MakeShip(Facing.East, new Hex(0, 0));
        var target = MakeShip(Facing.West, new Hex(1, 0));

        // Target has 10 shield points in the Forward quadrant — more than enough
        // to absorb a flat-4 weapon.
        target.Shields.Forward = 10;

        var weapon = MakeWeapon();
        var dice = new ScriptedDice(d20: 20, percent: 50);

        var result = CombatResolver.ResolveAttack(attacker, target, weapon, dice);

        Assert.True(result.IsHit);
        Assert.Equal(0, result.HullDamage);
        Assert.False(result.CriticalScored, "A natural 20 absorbed entirely by shields must not score critical damage.");
    }

    /// <summary>
    /// A natural 20 that overflows into hull points MUST score critical damage.
    /// </summary>
    [Fact]
    public void Natural20_OverflowsToHull_ScoresCriticalDamage()
    {
        var attacker = MakeShip(Facing.East, new Hex(0, 0));
        var target = MakeShip(Facing.West, new Hex(1, 0));

        // Target has only 2 shield points in Forward — a flat-4 weapon overflows by 2.
        target.Shields.Forward = 2;

        var weapon = MakeWeapon();
        var dice = new ScriptedDice(d20: 20, percent: 50);

        var result = CombatResolver.ResolveAttack(attacker, target, weapon, dice);

        Assert.True(result.IsHit);
        Assert.Equal(2, result.HullDamage);
        Assert.True(result.CriticalScored, "A natural 20 that deals hull damage must score critical damage.");
        Assert.NotNull(result.NewCriticalCondition);
    }

    /// <summary>
    /// A non-critical roll (e.g. 15) that overflows to hull does NOT score critical
    /// unless the cumulative hull damage crosses a multiple of the Critical Threshold.
    /// </summary>
    [Fact]
    public void NonCriticalRoll_OverflowsToHull_NoCriticalDamage()
    {
        var attacker = MakeShip(Facing.East, new Hex(0, 0));
        var target = MakeShip(Facing.West, new Hex(1, 0));

        // Target has 2 shield points; weapon deals 4 → 2 hull damage.
        // Critical Threshold is 11, so 2 < 11 → no threshold cross.
        target.Shields.Forward = 2;

        var weapon = MakeWeapon();
        var dice = new ScriptedDice(d20: 15, percent: 50);

        var result = CombatResolver.ResolveAttack(attacker, target, weapon, dice);

        Assert.True(result.IsHit);
        Assert.Equal(2, result.HullDamage);
        Assert.False(result.CriticalScored, "A non-natural-20 roll that doesn't cross the critical threshold must not score critical.");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static ShipInstance MakeShip(Facing facing, Hex position, string id = "test-ship", int teamId = 0, int baseAc = 12, int gunnerBonus = 3, IReadOnlyList<Weapon>? weapons = null, int criticalThreshold = 11)
    {
        var def = new ShipDefinition
        {
            Id = id,
            Name = id,
            HullPoints = 50,
            Speed = 10,
            Maneuverability = Maneuverability.Good,
            BaseAC = baseAc,
            BaseTL = 12,
            DamageThreshold = 0,
            CriticalThreshold = criticalThreshold,
            GunnerBaseAttack = gunnerBonus,
            Weapons = weapons ?? [],
            Shields = new QuadrantShields
            {
                Forward = 10,
                Starboard = 10,
                Aft = 10,
                Port = 10,
            },
        };

        return new ShipInstance
        {
            InstanceId = id,
            Definition = def,
            TeamId = teamId,
            Position = position,
            Facing = facing,
            HullPoints = def.HullPoints,
            Shields = def.Shields.Clone(),
        };
    }

    private static Weapon MakeWeapon() => new()
    {
        Name = "Test Cannon",
        Arc = FiringArc.Forward,
        Damage = DamageExpression.From("4"),
        MinRange = 1,
        RangeIncrement = 3,
    };

    /// <summary>
    /// A deterministic dice roller that returns fixed values.
    /// </summary>
    private sealed class SequenceDice(IReadOnlyList<int> d20s) : IDiceRoller
    {
        private int _index;
        public int RollD20() => d20s[_index++];
        public int Roll(int sides) => 1;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }

    private sealed class ScriptedDice : IDiceRoller
    {
        private readonly int _d20;
        private readonly int _percent;

        public ScriptedDice(int d20, int percent)
        {
            _d20 = d20;
            _percent = percent;
        }

        public int RollD20() => _d20;
        public int Roll(int sides) => 1; // flat damage doesn't use this
        public int RollMultiple(int count, int sides) => count; // all ones
        public int RollPercent() => _percent;
    }
}
