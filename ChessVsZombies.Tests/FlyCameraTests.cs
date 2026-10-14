using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class FlyCameraTests
{
    [Fact]
    public void ThinObsidianCrustBlocksOnlyItsPhysicalSlab()
    {
        var world = new WorldGrid();
        var liquids = new LiquidSystem(world);
        liquids.RestoreState(new LiquidState
        {
            Obsidian = [new ObsidianCell(new Cell(7, 16, 7), 2, CrustFace.West, 10)]
        });
        var clearSide = new FlyCamera(new Vector3(7.7f, 16.5f, 7.2f), 0, 0);
        clearSide.MovePrecise(Vector3.UnitZ, .5f, world.IsSolidAt);
        AssertNear(new Vector3(7.7f, 16.5f, 7.7f), clearSide.Position);

        var intoCrust = new FlyCamera(new Vector3(6.5f, 16.5f, 7.5f), 0, 0);
        intoCrust.MovePrecise(-Vector3.UnitX, 2, world.IsSolidAt);
        Assert.InRange(intoCrust.Position.X, 6.5f, 6.8f);
    }

    [Theory]
    [InlineData(1.2f)]
    [InlineData(-1.2f)]
    public void ForwardMovementStaysHorizontalWhenLookingUpOrDown(float pitch)
    {
        var camera = new FlyCamera(new Vector3(10, 10, 10), MathF.PI / 2, pitch);

        camera.Move(Vector3.UnitZ, 2, _ => false);

        AssertNear(new Vector3(12, 10, 10), camera.Position);
    }

    [Theory]
    [InlineData(0, -2, 0)]
    [InlineData(1.5707964f, 0, 2)]
    public void RightStrafingFollowsTheHorizontalYawFrame(float yaw, float deltaX, float deltaZ)
    {
        var start = new Vector3(10, 10, 10);
        var camera = new FlyCamera(start, yaw, .9f);

        camera.Move(Vector3.UnitX, 2, _ => false);

        AssertNear(start + new Vector3(deltaX, 0, deltaZ), camera.Position);
    }

    [Fact]
    public void AscendAndDescendAreOppositeWorldVerticalMovements()
    {
        var start = new Vector3(10, 10, 10);
        var camera = new FlyCamera(start, 1.3f, 1.2f);

        camera.Move(Vector3.UnitY, 3, _ => false);
        AssertNear(start + new Vector3(0, 3, 0), camera.Position);
        camera.Move(-Vector3.UnitY, 3, _ => false);
        AssertNear(start, camera.Position);
    }

    [Fact]
    public void DiagonalInputDoesNotIncreaseMovementSpeed()
    {
        var start = new Vector3(10, 10, 10);
        var camera = new FlyCamera(start, .8f, -1.1f);

        camera.Move(Vector3.One, 3, _ => false);

        Assert.InRange(Vector3.Distance(start, camera.Position), 2.9999f, 3.0001f);
    }

    [Fact]
    public void ALongMovementCannotTunnelThroughAOneCellWall()
    {
        var camera = new FlyCamera(new Vector3(10.5f, 10.5f, 10.5f), MathF.PI / 2, 0);

        camera.Move(Vector3.UnitZ, 20, cell => cell.X == 12);

        Assert.InRange(camera.Position.X, 11.5f, 12 - FlyCamera.Radius);
        Assert.InRange(camera.Position.Y, 10.4999f, 10.5001f);
        Assert.InRange(camera.Position.Z, 10.4999f, 10.5001f);
        Assert.False(camera.Intersects(new Cell(12, 10, 10)));
    }

    [Fact]
    public void PlacementIntersectionIncludesTheCamerasBodyBeyondItsCenterCell()
    {
        var camera = new FlyCamera(new Vector3(10.9f, 10.5f, 10.5f));

        Assert.True(camera.Intersects(new Cell(10, 10, 10)));
        Assert.True(camera.Intersects(new Cell(11, 10, 10)));
        Assert.False(camera.Intersects(new Cell(12, 10, 10)));
        Assert.False(camera.Intersects(new Cell(10, 11, 10)));
    }

    [Fact]
    public void MousePanningChangesTheViewDirectionAndPitchStopsBeforeVertical()
    {
        var camera = new FlyCamera(yaw: 0, pitch: 0);

        camera.Look(100, 100);
        Assert.True(camera.Forward.X < 0);
        Assert.True(camera.Forward.Y < 0);
        Assert.InRange(camera.Forward.Length(), .9999f, 1.0001f);
        camera.Look(0, -100000);
        Assert.InRange(camera.Pitch, 0, MathF.PI / 2 - .01f);
        camera.Look(0, 100000);
        Assert.InRange(camera.Pitch, -MathF.PI / 2 + .01f, 0);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
        => Assert.InRange(Vector3.Distance(expected, actual), 0, .0001f);
}
