using Quadrant.RulesEngine;

namespace Quadrant.RulesEngine.Tests;

public class TrackingProjectileTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImmediateMissileImpactAndDirectHitResolveInAttackOrderAfterRestore(bool missileFirst)
    {
        var missile = new Weapon { Name = "Torpedo Launcher", Arc = FiringArc.Forward, Type = WeaponType.Tracking,
            ProjectileSpeed = 2, RangeIncrement = 10, Damage = DamageExpression.From("1") };
        var cannon = new Weapon { Name = "Cannon", Arc = FiringArc.Forward, RangeIncrement = 10, Damage = DamageExpression.From("1") };
        var attackerDefinition = new ShipDefinition { Id = "attacker", Name = "Attacker", HullPoints = 20,
            Shields = new QuadrantShields(), Weapons = [missile, cannon], GunnerBaseAttack = 10 };
        var targetDefinition = new ShipDefinition { Id = "target", Name = "Target", HullPoints = 20,
            Shields = new QuadrantShields(), BaseAC = 10, BaseTL = 10 };
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery, ActionOrder = ["attacker", "target"] };
        state.Ships.Add(attackerDefinition.Instantiate("attacker", new Hex(0, 0), Facing.East, 0));
        state.Ships.Add(targetDefinition.Instantiate("target", new Hex(1, 0), Facing.West, 1));
        var engine = new CombatEngine(state, new CountingDice());
        Assert.True(engine.ApplyFire(new FireAction(missileFirst ? 0 : 1, "target"))!.IsHit);
        Assert.True(engine.ApplyFire(new FireAction(missileFirst ? 1 : 0, "target"))!.IsHit);
        var restored = new CombatEngine(state, new CountingDice());
        restored.RestoreSnapshot(engine.CaptureSnapshot());
        restored.EndGunneryTurn();
        restored.EndGunneryTurn();
        var damageEvents = restored.LastEvents.Where(e => e.Message.Contains(" dmg (")).ToList();
        Assert.Equal(2, damageEvents.Count);
        Assert.Contains(missileFirst ? "Torpedo" : "Cannon", damageEvents[0].Message);
        Assert.Contains(missileFirst ? "Cannon" : "Torpedo", damageEvents[1].Message);
    }

    [Fact]
    public void MissileDamageIsRolledOnlyWhenItReachesItsTarget()
    {
        var weapon = new Weapon { Name = "Torpedo Launcher", Arc = FiringArc.Forward, Type = WeaponType.Tracking,
            ProjectileSpeed = 2, RangeIncrement = 10, Damage = DamageExpression.From("1d6") };
        var attackerDefinition = new ShipDefinition { Id = "attacker", Name = "Attacker", HullPoints = 20,
            Shields = new QuadrantShields(), Weapons = [weapon], GunnerBaseAttack = 2, DexterityModifier = 1 };
        var targetDefinition = new ShipDefinition { Id = "target", Name = "Target", HullPoints = 20,
            Shields = new QuadrantShields(), BaseTL = 12 };
        var attacker = attackerDefinition.Instantiate("attacker", new Hex(0, 0), Facing.East, 0);
        var target = targetDefinition.Instantiate("target", new Hex(4, 0), Facing.West, 1);
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery,
            ActionOrder = ["attacker", "target"] };
        state.Ships.AddRange([attacker, target]);
        var dice = new CountingDice();
        var engine = new CombatEngine(state, dice);

        var result = engine.ApplyFire(new FireAction(0, "target"));
        Assert.NotNull(result);
        Assert.True(result!.IsHit);
        Assert.Equal(0, dice.DamageRolls);
        Assert.Contains("15+3=18", result.Description);
        engine.EndGunneryTurn();
        engine.EndGunneryTurn();

        Assert.Single(state.Projectiles);
        Assert.Equal(0, dice.DamageRolls);
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("advances 2 hexes toward Target (Team 2). Gunnery: 15+3=18 vs TL 12."));

        state.Phase = CombatPhase.Gunnery;
        engine.AdvancePhase();
        Assert.Empty(state.Projectiles);
        Assert.Equal(1, dice.DamageRolls);
    }

    [Fact]
    public void AutomaticEndOnlyAdvancesWhenNoTargetableWeaponsRemain()
    {
        var weapon = new Weapon { Name = "Cannon", Arc = FiringArc.Forward, Damage = DamageExpression.From("1d6") };
        var attackerDefinition = new ShipDefinition { Id = "attacker", Name = "Attacker", HullPoints = 20,
            Shields = new QuadrantShields(), Weapons = [weapon, weapon] };
        var targetDefinition = new ShipDefinition { Id = "target", Name = "Target", HullPoints = 20, Shields = new QuadrantShields() };
        var state = new GameState { GameId = "test", RoomCode = "test", Phase = CombatPhase.Gunnery,
            ActionOrder = ["attacker", "target"] };
        state.Ships.Add(attackerDefinition.Instantiate("attacker", new Hex(0, 0), Facing.East, 0));
        state.Ships.Add(targetDefinition.Instantiate("target", new Hex(1, 0), Facing.West, 1));
        var dice = new CountingDice();
        var engine = new CombatEngine(state, dice);

        engine.ApplyFire(new FireAction(0, "target"));
        Assert.False(engine.AutoEndGunneryTurnIfNoActions());
        Assert.Equal("attacker", state.CurrentActor?.InstanceId);
        engine.ApplyFire(new FireAction(1, "target"));
        Assert.Equal(0, dice.DamageRolls);
        Assert.True(engine.AutoEndGunneryTurnIfNoActions());
        Assert.Equal("target", state.CurrentActor?.InstanceId);
        Assert.True(engine.AutoEndGunneryTurnIfNoActions());
        Assert.Equal(2, dice.DamageRolls);
    }

    [Fact]
    public void PreviewPathMovesAtProjectileSpeedAndFacesAlongItsRoute()
    {
        var (projectile, engine) = MakeScenario(new Hex(0, 0), Facing.NorthWest, new Hex(5, 0), speed: 2);

        var path = engine.GetProjectilePreviewPath(projectile);

        Assert.NotEmpty(path);
        Assert.Equal(projectile.Position.Neighbor(projectile.Facing), path[0].Position);
        var steps = path.Prepend(new CombatEngine.PathStep(projectile.Position, projectile.Facing));
        Assert.Equal(2, steps.Zip(steps.Skip(1)).Count(pair => pair.First.Position != pair.Second.Position));
    }

    [Fact]
    public void PreviewRecalculatesRouteTowardTargetsNewPosition()
    {
        var (projectile, engine) = MakeScenario(new Hex(0, 0), Facing.East, new Hex(0, -5), speed: 2);

        var path = engine.GetProjectilePreviewPath(projectile);

        Assert.NotEmpty(path);
        Assert.Equal(projectile.Position.Neighbor(projectile.Facing), path[0].Position);
        Assert.True(path[^1].Position.DistanceTo(new Hex(0, -5)) < path[0].Position.DistanceTo(new Hex(0, -5)));
    }

    [Fact]
    public void ProjectileUsesDirectShortestRouteInsteadOfDriftingAlongEqualDistanceHexes()
    {
        var (projectile, engine) = MakeScenario(new Hex(0, 0), Facing.East, new Hex(6, -3), speed: 4);
        var path = engine.GetProjectilePreviewPath(projectile);
        var visited = path.Select(p => p.Position).Distinct().ToList();
        Assert.Equal(4, visited.Count);
        Assert.Equal(new Hex(1, 0), visited[0]);
        // The direct route alternates between the two hex columns bordering
        // the line rather than first following one axis to its extreme.
        Assert.All(visited, hex => Assert.InRange(Math.Abs(hex.Q + 2 * hex.R), 0, 1));
        Assert.Equal(2, path[^1].Position.DistanceTo(new Hex(6, -3)));
    }

    [Fact]
    public void ProjectileCanInitiallyMoveAwayWhenItsFacingRequiresIt()
    {
        var (projectile, engine) = MakeScenario(new Hex(0, 0), Facing.West, new Hex(3, 0), speed: 8);
        var path = engine.GetProjectilePreviewPath(projectile);
        Assert.Equal(new Hex(-1, 0), path[0].Position);
        Assert.Equal(new Hex(3, 0), path[^1].Position);
        Assert.Contains(path.Zip(path.Skip(1)), p => p.First.Position == p.Second.Position);
        var steps = path.Prepend(new CombatEngine.PathStep(projectile.Position, projectile.Facing)).ToList();
        int turns = 0;
        foreach (var pair in steps.Zip(steps.Skip(1)))
        {
            if (pair.First.Position == pair.Second.Position)
            {
                turns++;
                Assert.InRange(turns, 1, 2);
            }
            else
            {
                Assert.Equal(pair.First.Position.Neighbor(pair.Second.Facing), pair.Second.Position);
                turns = 0;
            }
        }
    }

    [Fact]
    public void ProjectilePassesThroughShipsButDoesNotEndOnOne()
    {
        var (projectile, engine) = MakeScenario(new Hex(0, 0), Facing.East, new Hex(5, 0), speed: 2);
        var blocker = engine.State.Ships[0].Definition.Instantiate("blocker", new Hex(2, 0), Facing.East, 1);
        engine.State.Ships.Add(blocker);
        Assert.Equal(new Hex(1, 0), engine.GetProjectilePreviewPath(projectile)[^1].Position);
        projectile = new TrackingProjectile { Id = projectile.Id, Name = projectile.Name, TeamId = projectile.TeamId,
            OwnerShipId = projectile.OwnerShipId, TargetShipId = projectile.TargetShipId, Position = projectile.Position,
            Facing = projectile.Facing, Speed = 3, GunnerBonus = projectile.GunnerBonus, Weapon = projectile.Weapon };
        var path = engine.GetProjectilePreviewPath(projectile);
        Assert.Contains(path, step => step.Position == blocker.Position);
        Assert.Equal(new Hex(3, 0), path[^1].Position);
    }

    private static (TrackingProjectile Projectile, CombatEngine Engine) MakeScenario(Hex position, Facing facing, Hex targetPosition, int speed)
    {
        var targetDefinition = new ShipDefinition
        {
            Id = "target", Name = "Target", HullPoints = 10, Shields = new QuadrantShields()
        };
        var target = targetDefinition.Instantiate("target", targetPosition, Facing.West, 1);
        var state = new GameState { GameId = "test", RoomCode = "test" };
        state.Ships.Add(target);
        var projectile = new TrackingProjectile
        {
            Id = "projectile-1", Name = "Torpedo", TeamId = 0,
            OwnerShipId = "attacker", TargetShipId = "target", Position = position,
            Facing = facing, Speed = speed, GunnerBonus = 0,
            Weapon = new Weapon { Name = "Torpedo Launcher", Arc = FiringArc.Forward, Damage = DamageExpression.From("1") }
        };
        state.Projectiles.Add(projectile);
        return (projectile, new CombatEngine(state, new Dice()));
    }

    private sealed class Dice : IDiceRoller
    {
        public int RollD20() => 10;
        public int Roll(int sides) => 1;
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }

    private sealed class CountingDice : IDiceRoller
    {
        public int DamageRolls { get; private set; }
        public int RollD20() => 15;
        public int Roll(int sides) { DamageRolls++; return 1; }
        public int RollMultiple(int count, int sides) => count;
        public int RollPercent() => 50;
    }
}
