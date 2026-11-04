using Quadrant.RulesEngine;

namespace Quadrant.RulesEngine.Tests;

public class HitChanceTests
{
    // ─── HitChancePercent: pure probability math ──────────────────────────────

    [Theory]
    [InlineData(12, 0, 45)]   // need=12 → (21-12)/20 = 45%
    [InlineData(12, 3, 60)]   // need=9  → (21-9)/20  = 60%
    [InlineData(12, -3, 30)]  // need=15 → (21-15)/20 = 30%
    [InlineData(12, 11, 95)] // natural 1 always misses
    [InlineData(12, 12, 95)] // natural 1 always misses
    [InlineData(12, -8, 5)]   // need=20 → (21-20)/20 = 5%
    [InlineData(12, -9, 5)]   // natural 20 always hits
    public void HitChancePercent_ComputesExpectedProbability(int defenseValue, int bonus, int expected)
    {
        Assert.Equal(expected, CombatResolver.HitChancePercent(bonus, defenseValue));
    }

    [Fact]
    public void HitChancePercent_WreckedWeaponsArray_ReturnsZero()
    {
        // A wrecked weapons array (int.MinValue sentinel) can never hit.
        Assert.Equal(0, CombatResolver.HitChancePercent(int.MinValue, 12));
    }

    [Theory]
    [InlineData(100, -10, 5)]
    [InlineData(1, 10, 95)]
    public void HitChancePercent_RespectsNaturalTwentyAndNaturalOne(int defenseValue, int bonus, int expected)
    {
        Assert.Equal(expected, CombatResolver.HitChancePercent(bonus, defenseValue));
    }

    [Fact]
    public void HitChancePercent_MidRangeValues()
    {
        // need = defenseValue - bonus; chance = (21 - need) / 20.
        Assert.Equal(35, CombatResolver.HitChancePercent(-1, 13)); // need=14 → 7/20
        Assert.Equal(25, CombatResolver.HitChancePercent(-2, 14)); // need=16 → 5/20
    }

    // ─── ComputeGunnerBonus: bonus assembly ────────────────────────────────────

    [Fact]
    public void ComputeGunnerBonus_SumsBaseAttackAndDexterity()
    {
        var ship = MakeShip(gunnerBaseAttack: 5, dexterityModifier: 2);
        Assert.Equal(7, CombatResolver.ComputeGunnerBonus(ship));
    }

    [Fact]
    public void ComputeGunnerBonus_WreckedWeaponsArray_ReturnsSentinel()
    {
        var ship = MakeShip(gunnerBaseAttack: 5, dexterityModifier: 2);
        // Escalate the weapons array to Wrecked (None → Glitching → Malfunctioning → Wrecked).
        for (int i = 0; i < 3; i++)
            ship.Systems.ApplyCritical(ShipSystem.WeaponsArray);
        Assert.Equal(int.MinValue, CombatResolver.ComputeGunnerBonus(ship));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static ShipInstance MakeShip(int gunnerBaseAttack, int dexterityModifier)
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
            GunnerBaseAttack = gunnerBaseAttack,
            DexterityModifier = dexterityModifier,
            Shields = new QuadrantShields { Forward = 10, Starboard = 10, Aft = 10, Port = 10 },
        };

        return new ShipInstance
        {
            InstanceId = "test-ship",
            Definition = def,
            Position = new Hex(0, 0),
            Facing = Facing.East,
            HullPoints = def.HullPoints,
            Shields = def.Shields.Clone(),
        };
    }
}
