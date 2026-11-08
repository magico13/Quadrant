using Quadrant.RulesEngine;

namespace Quadrant.RulesEngine.Tests;

public class BarrelRollTests
{
    private static (CombatEngine Engine, GameState State, ShipInstance Ship) Create(int roll, string? size = "Large")
    {
        var definition = new ShipDefinition
        {
            Id = "ship", Name = "Ship", Size = size, Tier = 2, Speed = 8,
            Shields = new QuadrantShields { Port = 7, Starboard = 13 },
            Weapons = [new Weapon { Name = "Port gun", Arc = FiringArc.Port, Damage = DamageExpression.From("1") },
                new Weapon { Name = "Forward gun", Arc = FiringArc.Forward, Damage = DamageExpression.From("1") }],
            Crew = [new CrewDefinition("Pilot", new Dictionary<string, int> { ["piloting"] = 0 }),
                new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 100 })]
        };
        var ship = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Helm, ActionOrder = ["a", "b"] };
        state.Ships.Add(ship);
        state.Ships.Add(definition.Instantiate("b", ship.Position.Step(3, 2), Facing.West, 1));
        return (new CombatEngine(state, new FixedDice(roll)), state, ship);
    }

    [Theory]
    [InlineData(13, true, 0)]
    [InlineData(12, false, 0)]
    [InlineData(8, false, -4)]
    public void ResolvesCheckAndHalfSpeedMovement(int roll, bool rolled, int penalty)
    {
        var (engine, _, ship) = Create(roll);
        Assert.True(engine.StartStunt("Barrel Roll").IsValid);
        Assert.Equal(rolled, ship.BarrelRolledThisRound);
        Assert.False(ship.BarrelRollActive);
        Assert.Equal(penalty, ship.RoundAcTlModifier);
        Assert.Contains(0, ship.CrewActionsUsed);
        Assert.False(engine.StartStunt("Evade").IsValid);
        var moves = engine.GetValidMoves();
        Assert.NotEmpty(moves);
        Assert.All(moves, m => Assert.InRange(m.Path.Zip(m.Path.Skip(1)).Count(p => p.First.Position != p.Second.Position), rolled ? 0 : 4, 4));
        Assert.Contains(moves, m => m.Position == new Hex(4, 0));
        Assert.DoesNotContain(moves, m => m.Position == new Hex(5, 0));
    }

    [Theory]
    [InlineData("Tiny", true)]
    [InlineData("Small", true)]
    [InlineData("Medium", true)]
    [InlineData("Large", true)]
    [InlineData("Huge", false)]
    [InlineData("Gargantuan", false)]
    [InlineData("Colossal", false)]
    [InlineData(null, false)]
    public void EnforcesSizeBeforeConsumingAction(string? size, bool allowed)
    {
        var (engine, _, ship) = Create(13, size);
        Assert.Equal(allowed, engine.GetStuntSuccessChance(ship, "Barrel Roll") is not null);
        Assert.Equal(allowed, engine.StartStunt("Barrel Roll").IsValid);
        Assert.Equal(allowed, ship.CrewActionsUsed.Contains(0));
    }

    [Fact]
    public void SwapsWeaponsAndDamageShieldsOnlyDuringGunneryAndResets()
    {
        var (engine, state, ship) = Create(13);
        Assert.True(engine.StartStunt("Barrel Roll").IsValid);
        Assert.Equal(FiringArc.Port, ship.Weapons[0].Arc);
        Assert.True(engine.TryMoveTo(ship.Position, ship.Facing).IsValid);
        engine.SkipMove();
        Assert.Equal(CombatPhase.Gunnery, state.Phase);
        Assert.True(ship.BarrelRollActive);
        Assert.Equal(FiringArc.Starboard, ship.Weapons[0].Arc);
        Assert.Equal(FiringArc.Forward, ship.Weapons[1].Arc);
        Assert.Equal(FiringArc.Port, ship.Definition.Weapons[0].Arc);
        Assert.Contains(engine.GetValidTargets(), t => t.WeaponIndex == 0 && t.TargetShipId == "b");
        Assert.NotNull(engine.ApplyFire(new FireAction(0, "b")));
        var incoming = state.Ships[1].Position;
        Assert.Equal(Quadrant.Port, ship.QuadrantOf(incoming));
        CombatResolver.ApplyDamage(ship, ship.QuadrantOf(incoming), 3, 10, new FixedDice(13));
        Assert.Equal(4, ship.Shields.Port);
        Assert.Equal(13, ship.Shields.Starboard);
        ship.BeginRound();
        Assert.False(ship.BarrelRollActive);
        Assert.False(ship.BarrelRolledThisRound);
        Assert.Equal(FiringArc.Port, ship.Weapons[0].Arc);
        Assert.Equal(Quadrant.Starboard, ship.QuadrantOf(incoming));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncomingProjectilesUseRolledShieldAndPointDefenseCoverage(bool pointDefense)
    {
        var (engine, state, ship) = Create(13);
        var point = new Weapon { Name = "Port point", Arc = FiringArc.Port, PointDefenseBonus = 10, Damage = DamageExpression.From("1") };
        var targetDefinition = ship.Definition with { Weapons = pointDefense ? [point] : [] };
        var target = targetDefinition.Instantiate("a", ship.Position, ship.Facing, 0);
        target.BarrelRolledThisRound = true;
        state.Ships[0] = target;
        engine.AdvancePhase();
        var missile = new Weapon { Name = "Missile", Arc = FiringArc.AllArcs, Type = WeaponType.Tracking, Damage = DamageExpression.From("3") };
        state.Projectiles.Add(new TrackingProjectile
        {
            Id = "missile", Name = "Missile", TeamId = 1, OwnerShipId = "b", TargetShipId = "a",
            Speed = 1, GunnerBonus = 100, Weapon = missile, Position = target.Position.Step(3, 1), Facing = Facing.NorthWest
        });
        engine.AdvancePhase();
        Assert.Equal(pointDefense ? 7 : 4, target.Shields.Port);
        Assert.Equal(13, target.Shields.Starboard);
        Assert.Equal(pointDefense, engine.LastEvents.Any(e => e.Message.Contains("point defense")));
    }

    private sealed class FixedDice(int roll) : IDiceRoller
    {
        public int RollD20() => roll;
        public int Roll(int sides) => 1;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }
}
