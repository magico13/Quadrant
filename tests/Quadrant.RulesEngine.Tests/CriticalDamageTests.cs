using Quadrant.RulesEngine;
using Q = Quadrant.RulesEngine.Quadrant;

namespace Quadrant.RulesEngine.Tests;

public class CriticalDamageTests
{
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

    private static ShipInstance MakeShip(Facing facing, Hex position)
    {
        var def = new ShipDefinition
        {
            Id = "test-ship-def",
            Name = "Test Ship",
            HullPoints = 50,
            Speed = 10,
            Maneuverability = Maneuverability.Good,
            BaseAC = 12,
            BaseTL = 12,
            DamageThreshold = 0,
            CriticalThreshold = 11,
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
            InstanceId = "test-ship",
            Definition = def,
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
