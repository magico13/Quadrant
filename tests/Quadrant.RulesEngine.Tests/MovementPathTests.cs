namespace Quadrant.RulesEngine.Tests;

public class MovementPathTests
{
    [Fact]
    public void PerfectManeuverabilityCannotTurnBeforeMovingEvenWithDivertedEngines()
    {
        var def = new ShipDefinition { Id = "perfect", Name = "Perfect", Speed = 4,
            Maneuverability = Maneuverability.Perfect, HullPoints = 100, Shields = new QuadrantShields() };
        var ship = def.Instantiate("ship", new Hex(0, 0), Facing.East, 0);
        ship.DivertEngines = true;
        var engine = MakeHelmEngine(ship);
        Assert.All(engine.GetValidMoves(), move => {
            if (move.Path.Count > 1) Assert.NotEqual(move.Path[0].Position, move.Path[1].Position);
        });
    }
    [Fact]
    public void RoutePrefersLongerPathThatAvoidsEnemyHex()
    {
        var definition = new ShipDefinition { Id = "mover", Name = "Mover", Speed = 5, Maneuverability = Maneuverability.Perfect,
            HullPoints = 100, Shields = new QuadrantShields() };
        var mover = definition.Instantiate("mover", new Hex(0, 0), Facing.East, 0);
        var enemy = definition.Instantiate("enemy", new Hex(2, 0), Facing.West, 1);
        var engine = MakeHelmEngine(mover, enemy);
        var option = Assert.Single(engine.GetValidMoves(), m => m.Position == new Hex(3, 0) && m.FinalFacing == Facing.East);
        Assert.DoesNotContain(option.Path, step => step.Position == enemy.Position);
        Assert.True(option.Path.Zip(option.Path.Skip(1)).Count(pair => pair.First.Position != pair.Second.Position) > 3);
    }

    [Fact]
    public void PassingEnemyOffersFreeAttackAgainstAftWithoutSpendingGunnerAction()
    {
        var gun = new Weapon { Name = "Aft Cannon", Arc = FiringArc.Aft, Damage = DamageExpression.From("3") };
        var attackerDef = new ShipDefinition { Id = "enemy", Name = "Enemy", Shields = new QuadrantShields(), Weapons = [gun],
            Crew = [new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 30 })] };
        var mover = MakeShip(speed: 3, position: new Hex(0, 0), facing: Facing.East, id: "mover");
        mover.Shields.Aft = 10;
        var enemy = attackerDef.Instantiate("enemy", new Hex(1, 0), Facing.East, 1);
        enemy.CrewActionsUsed.Add(0);
        var engine = MakeHelmEngine(mover, enemy);
        Assert.True(engine.TryMoveTo(new Hex(3, 0), Facing.East).IsValid);
        Assert.Equal("enemy", engine.FreeAttackShipId);
        Assert.Equal(new Hex(1, 0), mover.Position);
        Assert.Single(engine.GetFreeAttackWeapons());
        Assert.Null(engine.ResolveFreeAttack(0, 0));
        Assert.Equal(7, mover.Shields.Aft);
        Assert.Equal(new Hex(3, 0), mover.Position);
        Assert.Single(enemy.CrewActionsUsed);
        Assert.Contains(engine.LastEvents, e => e.Message.Contains("free attack") && e.Message.Contains("HIT"));
    }

    [Fact]
    public void EnemyWithoutGunnerDoesNotGetFreeAttack()
    {
        var mover = MakeShip(speed: 3, position: new Hex(0, 0), facing: Facing.East, id: "mover");
        var enemy = MakeShip(speed: 1, position: new Hex(1, 0), facing: Facing.East, id: "enemy");
        enemy.TeamId = 1;
        var engine = MakeHelmEngine(mover, enemy);
        Assert.True(engine.TryMoveTo(new Hex(3, 0), Facing.East).IsValid);
        Assert.Null(engine.FreeAttackShipId);
        Assert.Equal(new Hex(3, 0), mover.Position);
    }

    [Fact]
    public void FreeAttackCanBeDeclinedAndDoesNotSpendAmmunition()
    {
        var weapon = new Weapon { Name = "Cannon", Arc = FiringArc.Port, Damage = DamageExpression.From("3"), LimitedFireUses = 2 };
        var definition = new ShipDefinition { Id = "enemy", Name = "Enemy", Shields = new QuadrantShields(), Weapons = [weapon],
            Crew = [new CrewDefinition("Gunner", new Dictionary<string, int> { ["gunnery"] = 5 })] };
        var mover = MakeShip(speed: 3, position: new Hex(0, 0), facing: Facing.East, id: "mover");
        var enemy = definition.Instantiate("enemy", new Hex(1, 0), Facing.West, 1);
        var engine = MakeHelmEngine(mover, enemy);
        Assert.True(engine.TryMoveTo(new Hex(3, 0), Facing.East).IsValid);
        Assert.True(engine.HasPendingFreeAttack);
        Assert.Null(engine.ResolveFreeAttack(null));
        Assert.False(engine.HasPendingFreeAttack);
        Assert.Equal(new Hex(3, 0), mover.Position);
        Assert.Equal(2, enemy.WeaponUsesRemaining[0]);
        Assert.Empty(enemy.CrewActionsUsed);
    }
    private static ShipInstance MakeShip(int speed = 4, Hex? position = null, Facing facing = Facing.East, string id = "test-ship")
    {
        var def = new ShipDefinition
        {
            Id = "test-ship-def",
            Name = "Test Ship",
            Speed = speed,
            Maneuverability = Maneuverability.Average,
            HullPoints = 100,
            Shields = new QuadrantShields(),
        };

        return new ShipInstance
        {
            InstanceId = id,
            Definition = def,
            Position = position ?? new Hex(0, 0),
            Facing = facing,
            HullPoints = 100,
        };
    }

    private static CombatEngine MakeHelmEngine(params ShipInstance[] ships)
    {
        var state = new GameState
        {
            GameId = "test",
            RoomCode = "TEST12",
            Phase = CombatPhase.Helm,
            Round = 1,
        };
        foreach (var ship in ships)
        {
            state.Ships.Add(ship);
            state.ActionOrder.Add(ship.InstanceId);
        }
        return new CombatEngine(state, new DiceRoller(42));
    }

    [Fact]
    public void GetValidMoves_PathStartsAtShipAndEndsAtDestination()
    {
        var ship = MakeShip(speed: 3, position: new Hex(0, 0), facing: Facing.East);
        var engine = MakeHelmEngine(ship);

        var moves = engine.GetValidMoves();
        Assert.NotEmpty(moves);

        foreach (var m in moves)
        {
            Assert.Equal(new Hex(0, 0), m.Path[0].Position);
            Assert.Equal(m.Position, m.Path[^1].Position);
        }
    }

    [Fact]
    public void GetValidMoves_StraightMovePathFollowsHexes()
    {
        // Facing East from (0,0): a straight move of 3 should pass through (1,0), (2,0), (3,0).
        var ship = MakeShip(speed: 3, position: new Hex(0, 0), facing: Facing.East);
        var engine = MakeHelmEngine(ship);

        var moves = engine.GetValidMoves();
        var straight = moves.First(m => m.Position == new Hex(3, 0) && m.FinalFacing == Facing.East);

        Assert.Equal(new[] { new Hex(0, 0), new Hex(1, 0), new Hex(2, 0), new Hex(3, 0) }, straight.Path.Select(s => s.Position));
    }

    [Fact]
    public void GetValidMoves_PathLengthMatchesHexesTraveled()
    {
        var ship = MakeShip(speed: 4, position: new Hex(0, 0), facing: Facing.East);
        var engine = MakeHelmEngine(ship);

        foreach (var m in engine.GetValidMoves())
        {
            // Path length minus one equals the hex distance from start to destination
            // only for straight moves; in general it must be at least the hex distance.
            int dist = new Hex(0, 0).DistanceTo(m.Position);
            // Count only actual hex-to-hex moves (ignore in-place turn steps on the same hex).
            int moves = m.Path.Zip(m.Path.Skip(1)).Count(p => p.First.Position != p.Second.Position);
            Assert.True(moves >= dist, $"Path of {moves} moves is shorter than hex distance {dist}.");
        }
    }

    [Fact]
    public void GetValidMoves_TurnInPlaceHasSingleHexPath()
    {
        var ship = MakeShip(speed: 2, position: new Hex(0, 0), facing: Facing.East);
        var engine = MakeHelmEngine(ship);

        // Turning in place (no movement) should yield a path of just the start hex.
        var turns = engine.GetValidMoves().Where(m => m.Position == new Hex(0, 0)).ToList();
        Assert.NotEmpty(turns);
        foreach (var t in turns)
            Assert.All(t.Path, s => Assert.Equal(new Hex(0, 0), s.Position));
    }

    [Fact]
    public void GetValidMoves_PathDoesNotEndOnOccupiedHex()
    {
        var mover = MakeShip(speed: 3, position: new Hex(0, 0), facing: Facing.East, id: "mover");
        var blocker = MakeShip(speed: 1, position: new Hex(2, 0), facing: Facing.West, id: "blocker");
        var engine = MakeHelmEngine(mover, blocker);

        foreach (var m in engine.GetValidMoves())
            Assert.NotEqual(new Hex(2, 0), m.Position);
    }

    [Fact]
    public void GetValidMoves_CannotReverseFacingInOneTurn()
    {
        // Average maneuverability: min 2 hexes between turns, each turn is ±60°.
        // Starting West and moving all the way West should NOT allow ending East.
        var ship = MakeShip(speed: 4, position: new Hex(0, 0), facing: Facing.West);
        var engine = MakeHelmEngine(ship);

        var moves = engine.GetValidMoves();
        // Moving West reaches (-1,0) through (-4,0). None should be facing East.
        for (int q = -1; q >= -4; q--)
        {
            var eastFacing = moves.Where(m => m.Position == new Hex(q, 0) && m.FinalFacing == Facing.East).ToList();
            Assert.Empty(eastFacing);
        }
    }

    [Fact]
    public void GetValidMoves_CanTurn60DegreesAfterMinimumDistance()
    {
        // Average: move 2 West from (0,0) → (-2,0), then turn ±60°.
        // West(5) +1 = NW(0), West(5) -1 = SW(4).
        var ship = MakeShip(speed: 3, position: new Hex(0, 0), facing: Facing.West);
        var engine = MakeHelmEngine(ship);

        var moves = engine.GetValidMoves();
        Assert.Contains(moves, m => m.Position == new Hex(-2, 0) && m.FinalFacing == Facing.NorthWest);
        Assert.Contains(moves, m => m.Position == new Hex(-2, 0) && m.FinalFacing == Facing.SouthWest);
    }

    [Fact]
    public void DIAG_MoveOneForwardEndOppositeFacing()
    {
        // Perfect maneuverability: can turn in place anytime.
        var def = new ShipDefinition
        {
            Id = "diag", Name = "Diag", Speed = 4,
            Maneuverability = Maneuverability.Perfect,
            HullPoints = 100, Shields = new QuadrantShields(),
        };
        var ship = new ShipInstance
        {
            InstanceId = "diag", Definition = def,
            Position = new Hex(0, 0), Facing = Facing.East, HullPoints = 100,
        };
        var engine = MakeHelmEngine(ship);

        // Outcome: 1 hex forward (East → (1,0)) but ending facing West (opposite).
        var m = engine.GetValidMoves().FirstOrDefault(x => x.Position == new Hex(1, 0) && x.FinalFacing == Facing.West);
        Assert.NotNull(m);
        Console.WriteLine($"DIAG path for (1,0)/West: [{string.Join(", ", m!.Path.Select(s => $"({s.Position.Q},{s.Position.R})/{s.Facing}"))}]");

        // Also the straight-forward outcome for comparison.
        var s = engine.GetValidMoves().FirstOrDefault(x => x.Position == new Hex(1, 0) && x.FinalFacing == Facing.East);
        Console.WriteLine($"DIAG path for (1,0)/East: [{string.Join(", ", s!.Path.Select(s2 => $"({s2.Position.Q},{s2.Position.R})/{s2.Facing}"))}]");
    }

    [Fact]
    public void GetValidMoves_PathMayPassThroughOccupiedHex()
    {
        var mover = MakeShip(speed: 3, position: new Hex(0, 0), facing: Facing.East, id: "mover");
        var blocker = MakeShip(speed: 1, position: new Hex(2, 0), facing: Facing.West, id: "blocker");
        var engine = MakeHelmEngine(mover, blocker);

        // Moving to (3,0) requires passing through the occupied (2,0).
        var through = engine.GetValidMoves().FirstOrDefault(m => m.Position == new Hex(3, 0));
        Assert.NotNull(through);
        Assert.Contains(new Hex(2, 0), through!.Path.Select(s => s.Position));
    }
}
