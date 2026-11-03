using Q = Quadrant.RulesEngine.Quadrant;

namespace Quadrant.RulesEngine.Tests;

public class ShipArcTests
{
    private static ShipInstance MakeShip(Facing facing = Facing.East, Hex? position = null)
    {
        var def = new ShipDefinition
        {
            Id = "test-ship-def",
            Name = "Test Ship",
            Speed = 4,
            Maneuverability = Maneuverability.Average,
            HullPoints = 100,
            Shields = new QuadrantShields(),
        };

        return new ShipInstance
        {
            InstanceId = "test-ship",
            Definition = def,
            Position = position ?? new Hex(0, 0),
            Facing = facing,
            HullPoints = 100,
        };
    }

    [Fact]
    public void QuadrantOf_ForwardTarget_ReturnsForward()
    {
        var ship = MakeShip(Facing.East);
        Assert.Equal(Q.Forward, ship.QuadrantOf(new Hex(1, 0)));
    }

    [Fact]
    public void QuadrantOf_AftTarget_ReturnsAft()
    {
        var ship = MakeShip(Facing.East);
        Assert.Equal(Q.Aft, ship.QuadrantOf(new Hex(-1, 0)));
    }

    [Fact]
    public void QuadrantOf_StarboardTargets_ReturnsStarboard()
    {
        var ship = MakeShip(Facing.East);
        Assert.Equal(Q.Starboard, ship.QuadrantOf(new Hex(0, 1)));  // SE
        Assert.Equal(Q.Starboard, ship.QuadrantOf(new Hex(-1, 1))); // SW
    }

    [Fact]
    public void QuadrantOf_PortTargets_ReturnsPort()
    {
        var ship = MakeShip(Facing.East);
        Assert.Equal(Q.Port, ship.QuadrantOf(new Hex(1, -1)));  // NE
        Assert.Equal(Q.Port, ship.QuadrantOf(new Hex(0, -1)));  // NW
    }

    [Fact]
    public void QuadrantOf_FacingWest_ForwardIsWest()
    {
        var ship = MakeShip(Facing.West);
        Assert.Equal(Q.Forward, ship.QuadrantOf(new Hex(-1, 0)));
    }

    [Fact]
    public void QuadrantOf_FacingWest_AftIsEast()
    {
        var ship = MakeShip(Facing.West);
        Assert.Equal(Q.Aft, ship.QuadrantOf(new Hex(1, 0)));
    }

    [Fact]
    public void QuadrantOf_FacingWest_StarboardIsNorth()
    {
        // Ship facing West. Starboard = right side = North (NW, NE).
        var ship = MakeShip(Facing.West);
        Assert.Equal(Q.Starboard, ship.QuadrantOf(new Hex(0, -1)));  // NW
        Assert.Equal(Q.Starboard, ship.QuadrantOf(new Hex(1, -1)));  // NE
    }

    [Fact]
    public void QuadrantOf_FacingWest_PortIsSouth()
    {
        // Ship facing West. Port = left side = South (SW, SE).
        var ship = MakeShip(Facing.West);
        Assert.Equal(Q.Port, ship.QuadrantOf(new Hex(-1, 1)));  // SW
        Assert.Equal(Q.Port, ship.QuadrantOf(new Hex(0, 1)));   // SE
    }

    [Fact]
    public void ArcOffsetTo_Forward_ReturnsZero()
    {
        var ship = MakeShip(Facing.East);
        Assert.Equal(0, ship.ArcOffsetTo(new Hex(1, 0)));
    }

    [Fact]
    public void ArcOffsetTo_Aft_ReturnsThree()
    {
        var ship = MakeShip(Facing.East);
        Assert.Equal(3, ship.ArcOffsetTo(new Hex(-1, 0)));
    }

    [Fact]
    public void ArcOffsetTo_Starboard_ReturnsOneOrTwo()
    {
        var ship = MakeShip(Facing.East);
        Assert.Equal(1, ship.ArcOffsetTo(new Hex(0, 1)));   // SE
        Assert.Equal(2, ship.ArcOffsetTo(new Hex(-1, 1)));  // SW
    }

    [Fact]
    public void ArcOffsetTo_Port_ReturnsFourOrFive()
    {
        var ship = MakeShip(Facing.East);
        Assert.Equal(4, ship.ArcOffsetTo(new Hex(0, -1)));  // NW
        Assert.Equal(5, ship.ArcOffsetTo(new Hex(1, -1)));  // NE
    }

    [Fact]
    public void QuadrantOf_NonAdjacentTarget_UsesDirection()
    {
        var ship = MakeShip(Facing.East);
        var target = new Hex(1, 3); // roughly SE direction
        Assert.Equal(Q.Starboard, ship.QuadrantOf(target));
    }

    [Fact]
    public void QuadrantOf_AllSixDirections_CoverAllArcs()
    {
        var ship = MakeShip(Facing.East);
        var targets = new[]
        {
            new Hex(0, -1),  // NW → Port
            new Hex(1, -1),  // NE → Port
            new Hex(1, 0),   // E  → Forward
            new Hex(0, 1),   // SE → Starboard
            new Hex(-1, 1),  // SW → Starboard
            new Hex(-1, 0),  // W  → Aft
        };

        var expected = new[]
        {
            Q.Port,
            Q.Port,
            Q.Forward,
            Q.Starboard,
            Q.Starboard,
            Q.Aft,
        };

        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(expected[i], ship.QuadrantOf(targets[i]));
        }
    }
}
