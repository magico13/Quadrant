using System.Text.Json;
using Quadrant.RulesEngine;

namespace Quadrant.RulesEngine.Tests;

public class FireAtWillTests
{
    private sealed class FixedDice(int d20 = 10) : IDiceRoller
    {
        public int RollD20() => d20;
        public int Roll(int sides) => 1;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }

    private static (CombatEngine Engine, ShipInstance Ship) Battle(int d20 = 10)
    {
        var definition = new ShipDefinition
        {
            Id = "ship", Name = "Ship", HullPoints = 100, BaseAC = 12, BaseTL = 12,
            Shields = new QuadrantShields(),
            Crew = [new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 8 }, 1),
                    new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 6 }, 1)],
            Weapons = [new Weapon { Name = "First", Arc = FiringArc.Forward, Damage = DamageExpression.From("1d6") },
                       new Weapon { Name = "Second", Arc = FiringArc.Forward, Damage = DamageExpression.From("2d6") }]
        };
        var ship = definition.Instantiate("a", new Hex(0, 0), Facing.East, 0);
        var target = definition.Instantiate("b", new Hex(2, 0), Facing.West, 1);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery, ActionOrder = ["a", "b"] };
        state.Ships.AddRange([ship, target]);
        return (new CombatEngine(state, new FixedDice(d20)), ship);
    }

    [Fact]
    public void ActivatedGunnerHasTwoShotsAndBestRemainingGunnerIsSelectedFirst()
    {
        var (engine, ship) = Battle(d20: 1);
        Assert.Null(engine.ActivateFireAtWill(0));
        Assert.Contains(0, ship.CrewActionsUsed);
        Assert.Equal(2, ship.FireAtWillShots[0]);
        Assert.Equal(1, engine.GetGunneryChoices()[0].CrewIndex);
        Assert.Equal(6, engine.GetGunneryChoices()[0].Bonus);
        Assert.Equal(4, engine.GetGunneryChoices()[1].Bonus);
        Assert.Equal(75, engine.GetValidTargets(1, false).First().HitChancePercent);
        Assert.Equal(65, engine.GetValidTargets(0, true).First().HitChancePercent);

        var first = engine.ApplyFire(new FireAction(0, "b"), crewIndex: 0, fireAtWill: true)!;
        Assert.Equal(4, first.Modifier);
        Assert.False(first.IsHit);
        Assert.Equal(1, ship.FireAtWillShots[0]);
        Assert.Null(engine.ApplyFire(new FireAction(0, "b"), crewIndex: 0, fireAtWill: true));
        Assert.Equal(1, ship.FireAtWillShots[0]);
        Assert.Equal(4, engine.ApplyFire(new FireAction(1, "b"), crewIndex: 0, fireAtWill: true)!.Modifier);
        Assert.Empty(ship.FireAtWillShots);
        Assert.DoesNotContain(engine.GetGunneryChoices(), choice => choice.CrewIndex == 0);
    }

    [Fact]
    public void DefaultShotUsesTheAvailableGunnerWithTheHighestBonus()
    {
        var (engine, ship) = Battle();
        Assert.Equal(8, engine.ApplyFire(new FireAction(0, "b"))!.Modifier);
        Assert.Contains(0, ship.CrewActionsUsed);
        Assert.DoesNotContain(1, ship.CrewActionsUsed);
        Assert.Equal(6, engine.ApplyFire(new FireAction(1, "b"))!.Modifier);
    }

    [Fact]
    public void DifferentGunnersCanActivateFireAtWillInTheSameTurn()
    {
        var (engine, ship) = Battle();
        Assert.Null(engine.ActivateFireAtWill(0));
        Assert.Null(engine.ActivateFireAtWill(1));
        Assert.Equal(4, ship.RemainingGunneryActions());
        Assert.Equal(4, engine.ApplyFire(new FireAction(0, "b"), crewIndex: 0, fireAtWill: true)!.Modifier);
        Assert.Equal(1, ship.FireAtWillShots[0]);
        Assert.Equal(2, ship.FireAtWillShots[1]);
    }

    [Fact]
    public void GunnerCanBeChosenExplicitlyAndActivationSpendsActionEvenWithoutFiring()
    {
        var (engine, ship) = Battle();
        Assert.Equal(6, engine.ApplyFire(new FireAction(0, "b"), crewIndex: 1)!.Modifier);
        Assert.Contains(1, ship.CrewActionsUsed);
        Assert.Null(engine.ActivateFireAtWill(0));
        Assert.Contains(0, ship.CrewActionsUsed);
        Assert.NotNull(engine.ActivateFireAtWill(0));
        engine.EndGunneryTurn();
        Assert.Empty(ship.FireAtWillShots);
        Assert.Contains(0, ship.CrewActionsUsed);
    }

    [Fact]
    public void MalfunctioningWeaponArcCannotUseFireAtWillAndInvalidAttemptDoesNotSpendShot()
    {
        var (engine, ship) = Battle();
        ship.Systems.ApplyWeaponCritical(FiringArc.Forward);
        ship.Systems.ApplyWeaponCritical(FiringArc.Forward);
        Assert.Null(engine.ActivateFireAtWill(0));
        Assert.Empty(engine.GetValidTargets(0, true));
        Assert.NotEmpty(engine.GetValidTargets(1, false));
        Assert.Null(engine.ApplyFire(new FireAction(0, "b"), crewIndex: 0, fireAtWill: true));
        Assert.Equal(2, ship.FireAtWillShots[0]);
        Assert.Empty(engine.FiredWeaponIndices);
    }

    [Fact]
    public void RemainingShotsSurviveSerializationAndResetNextRound()
    {
        var (engine, ship) = Battle();
        Assert.Null(engine.ActivateFireAtWill(0));
        Assert.NotNull(engine.ApplyFire(new FireAction(0, "b"), crewIndex: 0, fireAtWill: true));
        var restored = JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(engine.State))!;
        var snapshot = JsonSerializer.Deserialize<CombatEngineSnapshot>(JsonSerializer.Serialize(engine.CaptureSnapshot()))!;
        var savedShip = restored.GetShip("a")!;
        Assert.Equal(1, savedShip.FireAtWillShots[0]);
        Assert.Contains(0, savedShip.CrewActionsUsed);
        var restoredEngine = new CombatEngine(restored, new FixedDice());
        restoredEngine.RestoreSnapshot(snapshot);
        Assert.Null(restoredEngine.ApplyFire(new FireAction(0, "b"), crewIndex: 0, fireAtWill: true));
        Assert.NotNull(restoredEngine.ApplyFire(new FireAction(1, "b"), crewIndex: 0, fireAtWill: true));
        savedShip.BeginRound();
        Assert.Empty(savedShip.FireAtWillShots);
    }

    [Fact]
    public void AverageDamageIncludesLinkedDiceMultipliersAndDivert()
    {
        var damage = new DamageExpression(2, 6, 3, 10);
        Assert.Equal(73, damage.Average());
        Assert.Equal(73 + 20.0 / 6, damage.Average(true), 6);
    }
}
