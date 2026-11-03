namespace Quadrant.RulesEngine.Tests;

public class MovementValidatorTests
{
    private static ShipInstance MakeShip(int speed = 4, Maneuverability maneuverability = Maneuverability.Average, Hex? position = null, string id = "test-ship")
    {
        var def = new ShipDefinition
        {
            Id = "test-ship-def",
            Name = "Test Ship",
            Speed = speed,
            Maneuverability = maneuverability,
            HullPoints = 100,
            Shields = new QuadrantShields(),
        };

        return new ShipInstance
        {
            InstanceId = id,
            Definition = def,
            Position = position ?? new Hex(0, 0),
            Facing = Facing.East,
            HullPoints = 100,
        };
    }

    private static GameState MakeState(params ShipInstance[] ships)
    {
        var state = new GameState { GameId = "test", RoomCode = "TEST12" };
        foreach (var ship in ships)
            state.Ships.Add(ship);
        return state;
    }

    [Fact]
    public void Validate_MoveWithinSpeed_IsValid()
    {
        var ship = MakeShip(speed: 4);
        var action = new MoveAction(new[]
        {
            new TurnSegment(2, null),
            new TurnSegment(1, Facing.SouthEast),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MoveExceedsSpeed_IsInvalid()
    {
        var ship = MakeShip(speed: 4);
        var action = new MoveAction(new[]
        {
            new TurnSegment(3, null),
            new TurnSegment(2, Facing.SouthEast),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ZeroHexesSegment_IsInvalid()
    {
        var ship = MakeShip(speed: 4);
        var action = new MoveAction(new[]
        {
            new TurnSegment(0, null),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ClumsyShip_TurnAfter3Hexes_IsInvalid()
    {
        // Clumsy requires 4 hexes between turns. Moving 3 then turning should fail.
        var ship = MakeShip(speed: 6, maneuverability: Maneuverability.Clumsy);
        var action = new MoveAction(new[]
        {
            new TurnSegment(3, Facing.SouthEast),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ClumsyShip_TurnAfter4Hexes_IsValid()
    {
        // Clumsy requires 4 hexes between turns. Moving 4 then turning should pass.
        var ship = MakeShip(speed: 6, maneuverability: Maneuverability.Clumsy);
        var action = new MoveAction(new[]
        {
            new TurnSegment(4, Facing.SouthEast),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_PoorShip_TurnAfter2Hexes_IsInvalid()
    {
        // Poor requires 3 hexes between turns. Moving 2 then turning should fail.
        var ship = MakeShip(speed: 5, maneuverability: Maneuverability.Poor);
        var action = new MoveAction(new[]
        {
            new TurnSegment(2, Facing.SouthEast),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_PoorShip_TurnAfter3Hexes_IsValid()
    {
        // Poor requires 3 hexes between turns. Moving 3 then turning should pass.
        var ship = MakeShip(speed: 5, maneuverability: Maneuverability.Poor);
        var action = new MoveAction(new[]
        {
            new TurnSegment(3, Facing.SouthEast),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_GoodShip_TurnAfter1Hex_IsValid()
    {
        // Good requires 1 hex between turns. Moving 1 then turning should pass.
        var ship = MakeShip(speed: 4, maneuverability: Maneuverability.Good);
        var action = new MoveAction(new[]
        {
            new TurnSegment(1, Facing.SouthEast),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_PerfectShip_TurnAfter0Hexes_IsValid()
    {
        // Perfect requires 0 hexes between turns. Any turn is fine.
        var ship = MakeShip(speed: 4, maneuverability: Maneuverability.Perfect);
        var action = new MoveAction(new[]
        {
            new TurnSegment(1, Facing.SouthEast),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MoveIntoOccupiedHex_IsInvalid()
    {
        // Ship A at (0,0) facing East. Ship B at (2,0). Ship A tries to move 2 east into B's hex.
        var shipA = MakeShip(speed: 4, position: new Hex(0, 0), id: "ship-a");
        var shipB = MakeShip(speed: 4, position: new Hex(2, 0), id: "ship-b");

        var action = new MoveAction(new[]
        {
            new TurnSegment(2, null), // moves to (2,0) which is occupied
        });

        var result = MovementValidator.Validate(shipA, action, MakeState(shipA, shipB));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_MoveToEmptyHex_IsValid()
    {
        var shipA = MakeShip(speed: 4, position: new Hex(0, 0), id: "ship-a");
        var shipB = MakeShip(speed: 4, position: new Hex(5, 5), id: "ship-b");

        var action = new MoveAction(new[]
        {
            new TurnSegment(2, null), // moves to (2,0) which is empty
        });

        var result = MovementValidator.Validate(shipA, action, MakeState(shipA, shipB));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MultipleSegments_TotalSpeedExceeded()
    {
        // Speed 4: 2 + 3 = 5 total, should fail
        var ship = MakeShip(speed: 4);
        var action = new MoveAction(new[]
        {
            new TurnSegment(2, null),
            new TurnSegment(3, Facing.SouthEast),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_MultipleSegments_WithinSpeed()
    {
        // Speed 4: 2 + 2 = 4 total, should pass
        var ship = MakeShip(speed: 4);
        var action = new MoveAction(new[]
        {
            new TurnSegment(2, null),
            new TurnSegment(2, Facing.SouthEast),
        });

        var result = MovementValidator.Validate(ship, action, MakeState(ship));
        Assert.True(result.IsValid);
    }
}
