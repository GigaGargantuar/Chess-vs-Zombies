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
    [InlineData(1.5707964f)]
    [InlineData(-1.5707964f)]
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
    public void MousePanningRetainsItsSensitivityAndReachesExactVertical()
    {
        var camera = new FlyCamera(yaw: 0, pitch: 0);

        camera.Look(100, 100);
        Assert.Equal(-.25f, camera.Yaw);
        Assert.Equal(-.25f, camera.Pitch);
        AssertNear(new Vector3(MathF.Sin(-.25f) * MathF.Cos(-.25f), MathF.Sin(-.25f), MathF.Cos(-.25f) * MathF.Cos(-.25f)), camera.Forward);
        Assert.InRange(camera.Forward.Length(), .9999f, 1.0001f);
        camera.Look(0, -100000);
        Assert.Equal(MathF.PI / 2, camera.Pitch);
        Assert.Equal(Vector3.UnitY, camera.Forward);
        camera.Look(0, 100000);
        Assert.Equal(-MathF.PI / 2, camera.Pitch);
        Assert.Equal(-Vector3.UnitY, camera.Forward);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(.8f, 1)]
    [InlineData(-2.7f, 1)]
    [InlineData(0, -1)]
    [InlineData(.8f, -1)]
    [InlineData(-2.7f, -1)]
    public void QuaternionBasisRemainsOrthonormalAndProducesAnInvertibleViewAtBothPoles(float yaw, int sign)
    {
        var camera = new FlyCamera(new Vector3(10, 25, 10), yaw, sign * MathF.PI / 2);

        Assert.Equal(sign * Vector3.UnitY, camera.Forward);
        Assert.InRange(camera.Orientation.Length(), .999999f, 1.000001f);
        Assert.InRange(camera.Up.Length(), .999999f, 1.000001f);
        Assert.InRange(MathF.Abs(Vector3.Dot(camera.Forward, camera.Up)), 0, .000001f);
        AssertNear(camera.Forward, Vector3.Transform(Vector3.UnitZ, camera.Orientation));
        AssertNear(camera.Up, Vector3.Transform(Vector3.UnitY, camera.Orientation));
        var view = Matrix4x4.CreateLookAt(camera.Position, camera.Position + camera.Forward, camera.Up);
        Assert.True(Matrix4x4.Invert(view, out var inverse));
        Assert.True(float.IsFinite(view.GetDeterminant()));
        AssertNear(camera.Position, inverse.Translation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void HorizontalPanningAtAPolePreservesHeadingAndLeavingItUsesTheSamePitchControl(int sign)
    {
        var camera = new FlyCamera(yaw: 0, pitch: sign * MathF.PI / 2);
        var previousUp = camera.Up;

        camera.Look(100, 0);

        Assert.Equal(sign * Vector3.UnitY, camera.Forward);
        Assert.Equal(-.25f, camera.Yaw);
        Assert.True(Vector3.Distance(previousUp, camera.Up) > .1f);
        camera.Look(0, sign * 100);
        var expectedPitch = sign * (MathF.PI / 2 - .25f);
        Assert.Equal(expectedPitch, camera.Pitch);
        AssertNear(new Vector3(MathF.Sin(-.25f) * MathF.Cos(expectedPitch), MathF.Sin(expectedPitch), MathF.Cos(-.25f) * MathF.Cos(expectedPitch)), camera.Forward);
    }

    [Fact]
    public void CombinedPanningDoesNotAccumulateRollAndEquivalentAnglesKeepTheSameOrientation()
    {
        var camera = new FlyCamera(yaw: .8f, pitch: .3f);
        for (int i = 0; i < 100; i++) { camera.Look(10, -2); camera.Look(-8, 3); }

        var expectedRight = new Vector3(-MathF.Cos(camera.Yaw), 0, MathF.Sin(camera.Yaw));
        AssertNear(expectedRight, Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)));
        var equivalent = new FlyCamera(yaw: camera.Yaw + MathF.Tau, pitch: camera.Pitch);
        Assert.InRange(MathF.Abs(Quaternion.Dot(camera.Orientation, equivalent.Orientation)), .999999f, 1.000001f);
        AssertNear(camera.Forward, equivalent.Forward);
        AssertNear(camera.Up, equivalent.Up);
    }

    [Fact]
    public void VerticalViewMovementKeepsThePannedHorizontalHeadingAndCollision()
    {
        var camera = new FlyCamera(new Vector3(10.5f, 10.5f, 10.5f), yaw: 0, pitch: MathF.PI / 2);
        camera.Look(-MathF.PI / 2 / .0025f, 0);

        camera.Move(Vector3.UnitZ, 20, cell => cell.X == 12);

        Assert.Equal(Vector3.UnitY, camera.Forward);
        Assert.InRange(camera.Position.X, 11.5f, 12 - FlyCamera.Radius);
        Assert.InRange(camera.Position.Y, 10.4999f, 10.5001f);
        Assert.InRange(camera.Position.Z, 10.4999f, 10.5001f);
        Assert.False(camera.Intersects(new Cell(12, 10, 10)));
    }

    [Fact]
    public void NonfiniteMouseDeltasLeaveTheViewUnchangedAndExtremeFiniteDeltasStayValid()
    {
        var camera = new FlyCamera();
        var orientation = camera.Orientation;
        camera.Look(float.NaN, 0);
        camera.Look(0, float.PositiveInfinity);
        Assert.Equal(orientation, camera.Orientation);

        for (int i = 0; i < 4; i++) camera.Look(float.MaxValue, -float.MaxValue);
        Assert.Equal(Vector3.UnitY, camera.Forward);
        Assert.InRange(camera.Orientation.Length(), .999999f, 1.000001f);
        Assert.InRange(camera.Up.Length(), .999999f, 1.000001f);
        Assert.InRange(MathF.Abs(Vector3.Dot(camera.Forward, camera.Up)), 0, .000001f);
    }

    [Theory]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    [InlineData(0, float.NaN)]
    [InlineData(0, float.NegativeInfinity)]
    public void ConstructorRejectsNonfiniteAngles(float yaw, float pitch)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlyCamera(yaw: yaw, pitch: pitch));
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
        => Assert.InRange(Vector3.Distance(expected, actual), 0, .0001f);
}
