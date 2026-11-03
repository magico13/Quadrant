namespace Quadrant.RulesEngine.Tests;

public class HexTests
{
    [Fact]
    public void DistanceTo_SameHex_ReturnsZero()
    {
        var hex = new Hex(3, 4);
        Assert.Equal(0, hex.DistanceTo(hex));
    }

    [Theory]
    [InlineData(0, 0, 1, 0, 1)]   // East neighbor
    [InlineData(0, 0, 0, -1, 1)]  // NW neighbor
    [InlineData(0, 0, -1, 0, 1)]  // W neighbor
    [InlineData(0, 0, 1, -1, 1)]  // NE neighbor
    [InlineData(0, 0, 0, 1, 1)]   // SE neighbor
    [InlineData(0, 0, -1, 1, 1)]  // SW neighbor
    public void DistanceTo_AdjacentHex_ReturnsOne(int q1, int r1, int q2, int r2, int expected)
    {
        var a = new Hex(q1, r1);
        var b = new Hex(q2, r2);
        Assert.Equal(expected, a.DistanceTo(b));
    }

    [Fact]
    public void DistanceTo_TwoHexesApart_ReturnsTwo()
    {
        var origin = new Hex(0, 0);
        var twoEast = new Hex(2, 0);
        Assert.Equal(2, origin.DistanceTo(twoEast));
    }

    [Fact]
    public void DistanceTo_IsSymmetric()
    {
        var a = new Hex(-3, -1);
        var b = new Hex(3, -1);
        Assert.Equal(a.DistanceTo(b), b.DistanceTo(a));
    }

    [Fact]
    public void Neighbor_East_IncreasesQ()
    {
        var hex = new Hex(0, 0);
        var east = hex.Neighbor((int)Facing.East);
        Assert.Equal(new Hex(1, 0), east);
    }

    [Fact]
    public void Neighbor_West_DecreasesQ()
    {
        var hex = new Hex(0, 0);
        var west = hex.Neighbor((int)Facing.West);
        Assert.Equal(new Hex(-1, 0), west);
    }

    [Fact]
    public void Neighbor_AllDirections_AreDistanceOne()
    {
        var origin = new Hex(0, 0);
        for (int dir = 0; dir < 6; dir++)
        {
            var neighbor = origin.Neighbor(dir);
            Assert.Equal(1, origin.DistanceTo(neighbor));
        }
    }

    [Fact]
    public void Step_MultipleHexes_TravelsCorrectly()
    {
        var hex = new Hex(0, 0);
        var result = hex.Step((int)Facing.East, 3);
        Assert.Equal(new Hex(3, 0), result);
    }

    [Fact]
    public void DirectionTo_AdjacentEast_Returns2()
    {
        var origin = new Hex(0, 0);
        var east = new Hex(1, 0);
        Assert.Equal((int)Facing.East, origin.DirectionTo(east));
    }

    [Fact]
    public void DirectionTo_AdjacentWest_Returns5()
    {
        var origin = new Hex(0, 0);
        var west = new Hex(-1, 0);
        Assert.Equal((int)Facing.West, origin.DirectionTo(west));
    }

    [Fact]
    public void DirectionTo_NonAdjacent_ReturnsNearestDirection()
    {
        var origin = new Hex(0, 0);
        // Two hexes east — should still resolve to East (2)
        var twoEast = new Hex(2, 0);
        Assert.Equal((int)Facing.East, origin.DirectionTo(twoEast));
    }

    [Fact]
    public void RecordStruct_Equality_Works()
    {
        Assert.Equal(new Hex(1, 2), new Hex(1, 2));
        Assert.NotEqual(new Hex(1, 2), new Hex(2, 1));
    }
}
