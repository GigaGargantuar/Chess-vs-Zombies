using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class WorldPickerTests
{
    [Theory]
    [InlineData(LiquidKind.Water, 1)]
    [InlineData(LiquidKind.Water, 7)]
    [InlineData(LiquidKind.Water, 10)]
    [InlineData(LiquidKind.Mercury, 7)]
    [InlineData(LiquidKind.Lava, 7)]
    public void AVisibleLiquidAlwaysHighlightsAndPlacesInTheCellAbove(LiquidKind kind, int units)
    {
        var game = new GameSession();
        var liquid = new Cell(8, 16, 8);
        Assert.True(game.Liquids.TryAdd(liquid, kind, units));
        var aim = WorldPicker.Pick(game, new Vector3(8.5f, 19, 8.5f), -Vector3.UnitY);
        Assert.True(aim.HasValue);
        Assert.True(aim.Value.IsLiquid);
        Assert.Equal(liquid, aim.Value.Hit);
        Assert.Equal(new Cell(8, 17, 8), aim.Value.Placement);
        Assert.Equal(aim.Value.Placement, aim.Value.Highlight);
    }

    [Fact]
    public void AimingAtTheSideOfLiquidStillSelectsAboveInsteadOfTheAdjacentRayCell()
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(new Cell(8, 16, 8), LiquidKind.Water, 7));
        var aim = WorldPicker.Pick(game, new Vector3(6.5f, 16.4f, 8.5f), Vector3.UnitX)!.Value;
        Assert.True(aim.IsLiquid);
        Assert.Equal(new Cell(8, 17, 8), aim.Highlight);
        Assert.Equal(new Cell(8, 17, 8), aim.Placement);
    }

    [Fact]
    public void ACameraInsideLiquidSelectsItsCurrentPoolAndHighlightsAboveIt()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Water, 7));

        var aim = WorldPicker.Pick(game, new Vector3(8.5f, 16.3f, 8.5f), Vector3.UnitX)!.Value;

        Assert.True(aim.IsLiquid);
        Assert.Equal(cell, aim.Hit);
        Assert.Equal(cell + new Cell(0, 1, 0), aim.Placement);
        Assert.Equal(aim.Placement, aim.Highlight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrazingRayFindsAnIntersectionShorterThanTheOldSampleSpacing(bool crust)
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        if (crust)
            game.Liquids.RestoreState(new LiquidState { Obsidian = [new(cell, 2, CrustFace.West, 10)] });
        else
            Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Water, 7));

        // The ray crosses this cell only between about .014 and .021 metres.
        var aim = WorldPicker.Pick(game, new Vector3(7.99f, 16.5f, 8.985f), new Vector3(1, 0, 1))!.Value;

        Assert.Equal(cell, aim.Hit);
        Assert.Equal(!crust, aim.IsLiquid);
        Assert.Equal(crust ? new Cell(7, 16, 8) : new Cell(8, 17, 8), aim.Placement);
        Assert.Equal(crust ? cell : new Cell(8, 17, 8), aim.Highlight);
    }

    [Fact]
    public void DistanceLimitUsesMetresForAnUnnormalizedDirection()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Water, 1));
        var origin = new Vector3(6.5f, 16.05f, 8.5f);
        var direction = new Vector3(100, 0, 0);

        Assert.Null(WorldPicker.Pick(game, origin, direction, 1.49f));
        Assert.Equal(cell, WorldPicker.Pick(game, origin, direction, 1.5f)!.Value.Hit);
    }

    [Fact]
    public void EntryFaceSolidHasNoOutOfBoundsPlacement()
    {
        var game = new GameSession();

        var aim = WorldPicker.Pick(game, new Vector3(8.5f, 14.5f, 33), -Vector3.UnitZ)!.Value;

        Assert.Equal(new Cell(8, 14, 31), aim.Hit);
        Assert.False(aim.IsLiquid);
        Assert.Null(aim.Placement);
        Assert.Equal(aim.Hit, aim.Highlight);
    }

    [Fact]
    public void CornerTouchDoesNotHitACellTheRayNeverEnters()
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(new Cell(8, 16, 8), LiquidKind.Water, 7));

        Assert.Null(WorldPicker.Pick(game, new Vector3(7, 16.4f, 8), new Vector3(1, 0, 1), 3));
    }

    [Fact]
    public void RayParallelToGridBoundaryUsesTheCellOnItsInsideFace()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Water, 7));

        var aim = WorldPicker.Pick(game, new Vector3(8, 16.4f, 6.5f), Vector3.UnitZ)!.Value;

        Assert.True(aim.IsLiquid);
        Assert.Equal(cell, aim.Hit);
        Assert.Null(WorldPicker.Pick(game, new Vector3(32, 16.4f, 6.5f), Vector3.UnitZ));
    }

    [Fact]
    public void AnOccupiedCellAboveLiquidStaysThePreviewTargetButRejectsThePour()
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Dirt, 3));
        Assert.True(game.BuildBarrier(new Cell(13, 16, 12)));
        Assert.True(game.BuildBarrier(new Cell(13, 17, 12)));
        var ceiling = new Cell(12, 17, 12);
        Assert.True(game.BuildBarrier(ceiling));
        Assert.True(game.BuyLiquid(LiquidKind.Water));
        var pool = new Cell(12, 16, 12);
        Assert.True(game.PourLiquid(pool, LiquidKind.Water, 7));

        var aim = WorldPicker.Pick(game, new Vector3(10.5f, 16.3f, 12.5f), Vector3.UnitX)!.Value;

        Assert.True(aim.IsLiquid);
        Assert.Equal(pool, aim.Hit);
        Assert.Equal(ceiling, aim.Placement);
        Assert.Equal(ceiling, aim.Highlight);
        Assert.False(game.PourLiquid(aim.Placement!.Value, LiquidKind.Water, 1));
        Assert.Equal(3, game.LiquidInventory[LiquidKind.Water]);
        Assert.Equal(7, game.Liquids.GetTotalUnits(pool));
        Assert.Equal(0, game.Liquids.GetTotalUnits(ceiling));
        Assert.Equal(Material.Barrier, game.World.GetMaterial(ceiling));
    }

    [Fact]
    public void AimingAtTheTopGridLayerDoesNotWrapOrInventAnOutOfBoundsPlacement()
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(new Cell(8, 31, 8), LiquidKind.Water, 10));
        var aim = WorldPicker.Pick(game, new Vector3(8.5f, 33, 8.5f), -Vector3.UnitY)!.Value;
        Assert.Equal(new Cell(8, 31, 8), aim.Hit);
        Assert.True(aim.IsLiquid);
        Assert.Null(aim.Placement);
        Assert.Null(aim.Highlight);
    }

    [Fact]
    public void RayPassesThroughAirAbovePartialLiquidAndStillSelectsASolidBehindIt()
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(new Cell(13, 16, 12), LiquidKind.Water, 3));
        Assert.True(game.Inventory.Add(Material.Dirt));
        Assert.True(game.BuildBarrier(new Cell(14, 16, 12)));
        var aim = WorldPicker.Pick(game, new Vector3(11.5f, 16.8f, 12.5f), Vector3.UnitX)!.Value;
        Assert.False(aim.IsLiquid);
        Assert.Equal(new Cell(14, 16, 12), aim.Hit);
        Assert.Equal(aim.Hit, aim.Highlight);
        Assert.Equal(new Cell(13, 16, 12), aim.Placement);
    }

    [Fact]
    public void TowerInFrontOfLiquidRemainsTheInteractionTarget()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(7, 16, 8)));
        Assert.True(game.Liquids.TryAdd(new Cell(8, 16, 8), LiquidKind.Water, 10));
        var aim = WorldPicker.Pick(game, new Vector3(6.5f, 16.5f, 8.5f), Vector3.UnitX)!.Value;
        Assert.False(aim.IsLiquid);
        Assert.Equal(new Cell(7, 16, 8), aim.Hit);
        Assert.Equal(new Cell(6, 16, 8), aim.Placement);
    }

    [Fact]
    public void SharedObsidianAndLiquidCellUsesTheActualSurfaceHit()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        game.Liquids.RestoreState(new LiquidState
        {
            Cells = [new(cell, 4, 0, 0, 2, CrustFace.West)],
            Obsidian = [new(cell, 2, CrustFace.West, 10)]
        });
        var crust = WorldPicker.Pick(game, new Vector3(7.5f, 16.3f, 8.5f), Vector3.UnitX)!.Value;
        Assert.False(crust.IsLiquid);
        Assert.Equal(cell, crust.Highlight);
        var liquid = WorldPicker.Pick(game, new Vector3(8.7f, 18, 8.5f), -Vector3.UnitY)!.Value;
        Assert.True(liquid.IsLiquid);
        Assert.Equal(cell + new Cell(0, 1, 0), liquid.Highlight);
    }

    [Fact]
    public void PourUsesTheHighlightedCellAndThenFlowsIntoThePool()
    {
        var game = new GameSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        var cell = new Cell(8, 15, 8);
        Assert.True(game.Dig(cell));
        Assert.True(game.BuyLiquid(LiquidKind.Water));
        Assert.True(game.PourLiquid(cell, LiquidKind.Water, 6));
        var aim = WorldPicker.Pick(game, new Vector3(8.5f, 18, 8.5f), -Vector3.UnitY)!.Value;
        Assert.Equal(cell + new Cell(0, 1, 0), aim.Highlight);
        Assert.True(game.PourLiquid(aim.Placement!.Value, LiquidKind.Water, 4));
        Assert.Equal(6, game.Liquids.GetUnits(cell, LiquidKind.Water));
        Assert.Equal(4, game.Liquids.GetUnits(aim.Placement.Value, LiquidKind.Water));
        game.Update(.1f);
        Assert.Equal(10, game.Liquids.GetUnits(cell, LiquidKind.Water));
        Assert.Equal(0, game.Liquids.GetUnits(aim.Placement.Value, LiquidKind.Water));
        Assert.Equal(0, game.LiquidInventory[LiquidKind.Water]);
    }

    [Fact]
    public void MissingOrInvalidRaysHaveNoTarget()
    {
        var game = new GameSession();
        Assert.Null(WorldPicker.Pick(game, new Vector3(8, 20, 8), Vector3.UnitY));
        Assert.Null(WorldPicker.Pick(game, new Vector3(8, 20, 8), Vector3.Zero));
        Assert.Null(WorldPicker.Pick(game, new Vector3(float.NaN), Vector3.UnitY));
    }

    [Fact]
    public void StatusVisibilityIgnoresLiquidButStillDetectsOneUnitCrust()
    {
        var game = new GameSession();
        var crust = new Cell(8, 16, 8);
        game.Liquids.RestoreState(new LiquidState
        {
            Cells = [new(new Cell(7, 16, 8), 10, 0, 0, 0)],
            Obsidian = [new(crust, 1, CrustFace.West, 5)]
        });
        var origin = new Vector3(7.5f, 16.85f, 8.5f);
        Assert.True(WorldPicker.Pick(game, origin, Vector3.UnitX, 2)!.Value.IsLiquid);
        var solid = WorldPicker.Pick(game, origin, Vector3.UnitX, 2, includeLiquids: false)!.Value;
        Assert.False(solid.IsLiquid);
        Assert.Equal(crust, solid.Hit);
    }
}
