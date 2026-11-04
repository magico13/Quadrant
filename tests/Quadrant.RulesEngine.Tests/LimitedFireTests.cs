using Quadrant.RulesEngine;
using Quadrant.Server.Sessions;

namespace Quadrant.RulesEngine.Tests;

public class LimitedFireTests
{
    [Fact]
    public void LimitedFireUsesAreTrackedSeparatelyForEachWeaponMount()
    {
        var launcher = MakeWeapon(2);
        var ship = MakeShip("attacker", 0, new Hex(0, 0), Facing.East, [launcher, launcher]);

        Assert.True(ship.ConsumeWeaponUse(0));

        Assert.Equal(1, ship.WeaponUsesRemaining[0]);
        Assert.Equal(2, ship.WeaponUsesRemaining[1]);
        Assert.True(ship.ConsumeWeaponUse(1));
        Assert.Equal(1, ship.WeaponUsesRemaining[1]);
    }

    [Fact]
    public void MissedShotConsumesLimitedFireUseAndEmptyLauncherCannotFire()
    {
        var attacker = MakeShip("attacker", 0, new Hex(0, 0), Facing.East, [MakeWeapon(1)]);
        var target = MakeShip("target", 1, new Hex(1, 0), Facing.West, [], baseAc: 50);
        var state = new GameState
        {
            GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery,
            ActionOrder = ["attacker", "target"]
        };
        state.Ships.AddRange([attacker, target]);
        var engine = new CombatEngine(state, new FixedDice(1));

        engine.ApplyGunneryTurn([new FireAction(0, "target")]);

        Assert.Equal(0, attacker.WeaponUsesRemaining[0]);
        state.ActionOrder = ["attacker"];
        state.CurrentActorIndex = 0;
        Assert.Empty(engine.GetValidTargets());
        engine.ApplyGunneryTurn([new FireAction(0, "target")]);
        Assert.Equal(0, attacker.WeaponUsesRemaining[0]);
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("out of ammunition"));
    }

    [Fact]
    public void CatalogLoadsLimitedFireCapacityFromWeaponProperties()
    {
        var catalog = new ShipCatalogService();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Quadrant.Server"));
        catalog.LoadWeapons(Path.Combine(root, "weapons"));
        catalog.LoadShips(Path.Combine(root, "ships"));

        var necrofighter = Assert.Single(catalog.All, s => s.Id == "deaths-curtain-necrofighter");
        var launcher = Assert.Single(necrofighter.Weapons, w => w.Name == "Light Plasma Torpedo Launcher");
        Assert.Equal(5, launcher.LimitedFireUses);
        Assert.Equal(14, launcher.ProjectileSpeed);
    }

    private static Weapon MakeWeapon(int uses) => new()
    {
        Name = "Limited Cannon", Arc = FiringArc.Forward, Damage = DamageExpression.From("4"),
        RangeIncrement = 5, LimitedFireUses = uses
    };

    private static ShipInstance MakeShip(string id, int team, Hex position, Facing facing, IReadOnlyList<Weapon> weapons, int baseAc = 12)
    {
        var definition = new ShipDefinition
        {
            Id = id, Name = id, HullPoints = 50, BaseAC = baseAc, BaseTL = 12,
            DamageThreshold = 0, CriticalThreshold = 10, Shields = new QuadrantShields(), Weapons = weapons
        };
        var ship = definition.Instantiate(id, position, facing, team);
        ship.InitializeWeaponUses();
        return ship;
    }

    private sealed class FixedDice(int d20) : IDiceRoller
    {
        public int RollD20() => d20;
        public int Roll(int sides) => 1;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }
}
