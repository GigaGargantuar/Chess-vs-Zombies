using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class TowerSelectionCursorTests
{
    [Fact]
    public void DepthSelectsEmptySpaceAtAnyHeight()
    {
        var origin = new Vector3(8.25f, 25.75f, 4.25f);

        Assert.Equal(new Cell(8, 25, 5), TowerSelectionCursor.Pick(origin, Vector3.UnitZ, 1));
        Assert.Equal(new Cell(8, 25, 12), TowerSelectionCursor.Pick(origin, Vector3.UnitZ, 8));
        Assert.Equal(new Cell(8, 17, 4), TowerSelectionCursor.Pick(origin, -Vector3.UnitY, 8));
    }

    [Theory]
    [InlineData(.001f)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(float.MaxValue)]
    [InlineData(float.Epsilon)]
    public void DepthUsesMetresRegardlessOfDirectionMagnitude(float magnitude)
    {
        Assert.Equal(new Cell(8, 24, 12), TowerSelectionCursor.Pick(new Vector3(8.5f, 24.5f, 4.5f), new Vector3(0, 0, magnitude), 8));
    }

    [Fact]
    public void DiagonalRayUsesAllThreeDimensionsAndFloorsTheResult()
    {
        Assert.Equal(new Cell(3, 24, 9), TowerSelectionCursor.Pick(new Vector3(5.25f, 26.75f, 8.5f), new Vector3(-2, -3, 2), 3));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 1, 1, 0, 0, 1)]
    [InlineData(31, 31, 31, 1, 0, 0, .5f, 31, 31, 31)]
    [InlineData(1, 16, 16, -1, 0, 0, 1, 0, 16, 16)]
    [InlineData(33, 16, 16, -1, 0, 0, 2, 31, 16, 16)]
    public void ExactInsideBoundariesAndRaysEnteringFromOutsideRemainValid(float ox, float oy, float oz,
        float dx, float dy, float dz, float distance, int x, int y, int z)
    {
        Assert.Equal(new Cell(x, y, z), TowerSelectionCursor.Pick(new Vector3(ox, oy, oz), new Vector3(dx, dy, dz), distance));
    }

    [Theory]
    [InlineData(31, 16, 16, 1, 0, 0, 1)]
    [InlineData(1, 16, 16, -1, 0, 0, 1.001f)]
    [InlineData(16, 31, 16, 0, 1, 0, 1)]
    [InlineData(16, 1, 16, 0, -1, 0, 1.001f)]
    [InlineData(16, 16, 31, 0, 0, 1, 1)]
    [InlineData(16, 16, 1, 0, 0, -1, 1.001f)]
    [InlineData(16, 16, 16, 1, 0, 0, float.MaxValue)]
    public void OutsideCornersDoNotClampToTheWorldBoundary(float ox, float oy, float oz,
        float dx, float dy, float dz, float distance)
    {
        Assert.Null(TowerSelectionCursor.Pick(new Vector3(ox, oy, oz), new Vector3(dx, dy, dz), distance));
    }

    [Fact]
    public void InvalidRaysHaveNoCorner()
    {
        var origin = new Vector3(8, 24, 8);
        Assert.Null(TowerSelectionCursor.Pick(origin, Vector3.Zero, 8));
        Assert.Null(TowerSelectionCursor.Pick(new Vector3(float.NaN, 24, 8), Vector3.UnitZ, 8));
        Assert.Null(TowerSelectionCursor.Pick(origin, new Vector3(0, float.PositiveInfinity, 1), 8));
        Assert.Null(TowerSelectionCursor.Pick(origin, Vector3.UnitZ, float.NaN));
        Assert.Null(TowerSelectionCursor.Pick(origin, Vector3.UnitZ, float.PositiveInfinity));
        Assert.Null(TowerSelectionCursor.Pick(origin, Vector3.UnitZ, 0));
        Assert.Null(TowerSelectionCursor.Pick(origin, Vector3.UnitZ, -1));
    }
}
