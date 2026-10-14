using System.Numerics;
using System.Text.Json;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class LiquidSystemTests
{
    [Fact]
    public void GravityConservesEveryUnitAndOccupiedCellsRejectFlow()
    {
        var world = new WorldGrid();
        var liquids = new LiquidSystem(world);
        var upper = new Cell(8, 18, 8);
        Assert.True(liquids.TryAdd(upper, LiquidKind.Water, 10));
        bool ColumnOnly(Cell c) => c.X != 8 || c.Z != 8;
        liquids.Update(.1f, ColumnOnly);
        Assert.Equal(10, liquids.GetUnits(new Cell(8, 17, 8), LiquidKind.Water));
        Assert.Equal(0, liquids.GetTotalUnits(upper));
        liquids.Update(.1f, c => ColumnOnly(c) || c.Y == 16);
        Assert.Equal(10, liquids.GetUnits(new Cell(8, 17, 8), LiquidKind.Water));
        liquids.Update(.1f, ColumnOnly);
        Assert.Equal(10, liquids.GetUnits(new Cell(8, 16, 8), LiquidKind.Water));
        Assert.Equal(10, Total(liquids));
    }

    [Fact]
    public void LateralFlowLevelsAClosedReservoirWithoutLosingVolume()
    {
        var a = new Cell(8, 5, 8);
        var b = a + new Cell(1, 0, 0);
        var (_, liquids) = Cavity(a, b);
        Assert.True(liquids.TryAdd(a, LiquidKind.Water, 10));
        liquids.Update(.1f);
        Assert.Equal(5, liquids.GetUnits(a, LiquidKind.Water));
        Assert.Equal(5, liquids.GetUnits(b, LiquidKind.Water));
        liquids.Update(2);
        Assert.Equal(10, Total(liquids));
        Assert.Empty(liquids.Hazards);
    }

    [Fact]
    public void MercuryAndWaterSwapFullVerticalCellsAndStayStratifiedWithoutReacting()
    {
        var lower = new Cell(8, 5, 8);
        var upper = lower + new Cell(0, 1, 0);
        var (world, liquids) = Cavity(lower, upper);
        Assert.True(liquids.TryAdd(lower, LiquidKind.Water, 10));
        Assert.True(liquids.TryAdd(upper, LiquidKind.Mercury, 10));
        liquids.Update(.1f);
        Assert.Equal(10, liquids.GetUnits(lower, LiquidKind.Mercury));
        Assert.Equal(10, liquids.GetUnits(upper, LiquidKind.Water));
        liquids.Update(1);
        Assert.Equal(20, Total(liquids));
        Assert.Empty(liquids.Hazards);
        Assert.Empty(world.ObsidianCells);
    }

    [Fact]
    public void MixedImmiscibleLiquidVolumesMatchDensityAndPointQueries()
    {
        var cell = new Cell(8, 5, 8);
        var (_, liquids) = Cavity(cell);
        Assert.True(liquids.TryAdd(cell, LiquidKind.Water, 4));
        Assert.True(liquids.TryAdd(cell, LiquidKind.Mercury, 6));
        var volumes = liquids.GetVolumes(cell).ToArray();
        Assert.Equal(LiquidKind.Mercury, volumes[0].Kind);
        Assert.Equal(5.6f, volumes[0].Max.Y, 4);
        Assert.Equal(LiquidKind.Water, volumes[1].Kind);
        Assert.Equal(LiquidKind.Mercury, liquids.GetLiquidAt(new Vector3(8.5f, 5.4f, 8.5f)));
        Assert.Equal(LiquidKind.Water, liquids.GetLiquidAt(new Vector3(8.5f, 5.8f, 8.5f)));
        Assert.Null(liquids.GetLiquidAt(new Vector3(8.5f, 6, 8.5f)));
        Assert.Equal(.2f, liquids.GetDepth(new Vector3(8.5f, 5.8f, 8.5f)), 4);
    }

    [Fact]
    public void MercuryReactionMakesARealThinInterfaceAndThenConductsUntilTheWholeLavaBlockSolidifies()
    {
        var lava = new Cell(8, 5, 8);
        var mercury = lava + new Cell(1, 0, 0);
        var (world, liquids) = Cavity(lava, mercury);
        Assert.True(liquids.TryAdd(lava, LiquidKind.Lava, 10));
        Assert.True(liquids.TryAdd(mercury, LiquidKind.Mercury, 5));
        liquids.Update(.1f);
        var crust = Assert.Single(world.ObsidianCells);
        Assert.Equal(2, crust.Units);
        Assert.Equal(CrustFace.East, crust.Face);
        Assert.Equal(.2f, crust.Max.X - crust.Min.X, 4);
        Assert.Equal(100, crust.Hp);
        Assert.True(world.IsSolidAt(new Vector3(8.9f, 5.5f, 8.5f)));
        Assert.False(world.IsSolidAt(new Vector3(8.5f, 5.5f, 8.5f)));
        Assert.False(world.HasSupport(lava + new Cell(0, 1, 0)));
        Assert.True(world.BlocksFace(lava, mercury));
        Assert.Equal(8, liquids.GetUnits(lava, LiquidKind.Lava));
        Assert.Equal(4, liquids.GetUnits(mercury, LiquidKind.Mercury));
        var firstCloud = Assert.Single(liquids.Hazards);
        Assert.Equal(2, firstCloud.Radius);
        Assert.Equal(DamageKind.Chemical, firstCloud.Kind);
        liquids.Update(.4f);
        crust = Assert.Single(world.ObsidianCells);
        Assert.Equal(10, crust.Units);
        Assert.Equal(500, crust.Hp);
        Assert.Equal(0, Total(liquids));
        var cloud = Assert.Single(liquids.Hazards);
        Assert.Equal(5, cloud.VaporizedUnits);
        Assert.True(cloud.Dps > firstCloud.Dps);
        Assert.True(cloud.RemainingSeconds > firstCloud.RemainingSeconds);
        Assert.Equal(15, Total(liquids) + crust.Units + cloud.VaporizedUnits);
    }

    [Fact]
    public void WaterReactionProducesTenUnitsOfSteamAndOneWholeObsidianBlock()
    {
        var lava = new Cell(8, 5, 8);
        var water = lava + new Cell(1, 0, 0);
        var (world, liquids) = Cavity(lava, water);
        Assert.True(liquids.TryAdd(lava, LiquidKind.Lava, 10));
        Assert.True(liquids.TryAdd(water, LiquidKind.Water, 10));
        liquids.Update(1);
        Assert.Equal(0, Total(liquids));
        Assert.Equal(10, Assert.Single(world.ObsidianCells).Units);
        var cloud = Assert.Single(liquids.Hazards);
        Assert.Equal(DamageKind.Thermal, cloud.Kind);
        Assert.Equal(1, cloud.Radius);
        Assert.Equal(10, cloud.VaporizedUnits);
    }

    [Fact]
    public void SameCellMercuryReactionRetainsItsActualInternalInterface()
    {
        var cell = new Cell(8, 5, 8);
        var (world, liquids) = Cavity(cell);
        Assert.True(liquids.TryAdd(cell, LiquidKind.Mercury, 1));
        Assert.True(liquids.TryAdd(cell, LiquidKind.Lava, 2));
        liquids.Update(.1f);
        var crust = Assert.Single(world.ObsidianCells);
        Assert.Equal(1, crust.OffsetUnits);
        Assert.Equal(5.1f, crust.Min.Y, 4);
        Assert.Equal(5.3f, crust.Max.Y, 4);
        Assert.False(world.IsSolidAt(new Vector3(8.5f, 5.05f, 8.5f)));
        Assert.True(world.IsSolidAt(new Vector3(8.5f, 5.2f, 8.5f)));
        Assert.Equal(0, Total(liquids));
        Assert.Equal(1, Assert.Single(liquids.Hazards).VaporizedUnits);
    }

    [Fact]
    public void InternalCrustPartitionsRemainingPhasesAndBlocksFlowThroughItsThickness()
    {
        var cell = new Cell(8, 5, 8);
        var below = cell + new Cell(0, -1, 0);
        var (world, liquids) = Cavity(cell, below);
        Assert.True(liquids.TryAdd(cell, LiquidKind.Mercury, 3));
        Assert.True(liquids.TryAdd(cell, LiquidKind.Lava, 6));
        liquids.Update(.1f);
        var crust = Assert.Single(world.ObsidianCells);
        Assert.Equal(3, crust.OffsetUnits);
        Assert.Equal(2, crust.Units);
        Assert.Equal(2, liquids.GetUnits(below, LiquidKind.Mercury));
        Assert.Equal(0, liquids.GetUnits(below, LiquidKind.Lava));
        Assert.Equal(4, liquids.GetUnits(cell, LiquidKind.Lava));
        Assert.Equal(crust.Max.Y, Assert.Single(liquids.GetVolumes(cell)).Min.Y, 4);
        Assert.Equal(9, Total(liquids) + crust.Units + Assert.Single(liquids.Hazards).VaporizedUnits);
    }

    [Fact]
    public void SameCellWaterReactionGrowsDownwardFromTheLavaWaterBoundary()
    {
        var cell = new Cell(8, 5, 8);
        var (world, liquids) = Cavity(cell);
        Assert.True(liquids.TryAdd(cell, LiquidKind.Lava, 5));
        Assert.True(liquids.TryAdd(cell, LiquidKind.Water, 5));
        liquids.Update(.1f);
        var crust = Assert.Single(world.ObsidianCells);
        Assert.Equal(CrustFace.Top, crust.Face);
        Assert.Equal(5, crust.OffsetUnits);
        Assert.Equal(5.4f, crust.Min.Y, 4);
        Assert.Equal(5.5f, crust.Max.Y, 4);
        Assert.Equal(LiquidKind.Lava, liquids.GetLiquidAt(new Vector3(8.5f, 5.2f, 8.5f)));
        Assert.Null(liquids.GetLiquidAt(new Vector3(8.5f, 5.45f, 8.5f)));
        Assert.Equal(LiquidKind.Water, liquids.GetLiquidAt(new Vector3(8.5f, 5.7f, 8.5f)));
        liquids.Update(.4f);
        Assert.Equal(0, Total(liquids));
        Assert.Equal(5, Assert.Single(world.ObsidianCells).Units);
        Assert.Equal(5, Assert.Single(liquids.Hazards).VaporizedUnits);
    }

    [Fact]
    public void ARecedingMercurySurfaceStopsHeatingUntilFreshMercuryReachesTheCrust()
    {
        var cell = new Cell(8, 5, 8);
        var (world, liquids) = Cavity(cell);
        Assert.True(liquids.TryAdd(cell, LiquidKind.Mercury, 3));
        Assert.True(liquids.TryAdd(cell, LiquidKind.Lava, 6));
        liquids.Update(.1f);
        Assert.Equal(2, world.GetObsidian(cell)!.Units);
        liquids.Update(.3f);
        Assert.Equal(2, world.GetObsidian(cell)!.Units);
        Assert.Equal(1, Assert.Single(liquids.Hazards).VaporizedUnits);
        Assert.True(liquids.TryAdd(cell, LiquidKind.Mercury, 1));
        liquids.Update(.1f);
        Assert.Equal(4, world.GetObsidian(cell)!.Units);
        Assert.Equal(2, Assert.Single(liquids.Hazards).VaporizedUnits);
        Assert.Equal(10, Total(liquids) + world.GetObsidian(cell)!.Units + Assert.Single(liquids.Hazards).VaporizedUnits);
    }

    [Fact]
    public void ASealedCrustFacePreventsFlowUntilTheCrustIsDestroyed()
    {
        var a = new Cell(8, 5, 8);
        var b = a + new Cell(1, 0, 0);
        var (world, liquids) = Cavity(a, b);
        liquids.RestoreState(new LiquidState
        {
            Cells = [new(a, 8, 0, 0, 2, CrustFace.East)],
            Obsidian = [new(a, 2, CrustFace.East, 100)]
        });
        liquids.Update(.2f);
        Assert.Equal(8, liquids.GetUnits(a, LiquidKind.Water));
        Assert.Equal(0, liquids.GetTotalUnits(b));
        Assert.False(world.DamageObsidian(a, 30));
        Assert.Equal(70, world.GetObsidian(a)!.Hp);
        Assert.True(world.DamageObsidian(a, 70));
        liquids.Update(.1f);
        Assert.Equal(4, liquids.GetUnits(a, LiquidKind.Water));
        Assert.Equal(4, liquids.GetUnits(b, LiquidKind.Water));
    }

    [Fact]
    public void DisjointUnsealedFaceOpeningsStillBlockFlow()
    {
        var a = new Cell(8, 5, 8);
        var b = a + new Cell(0, 0, 1);
        var (world, liquids) = Cavity(a, b);
        liquids.RestoreState(new LiquidState
        {
            Cells = [new(a, 1, 0, 0, 9, CrustFace.East)],
            Obsidian = [new(a, 9, CrustFace.East, 450), new(b, 9, CrustFace.West, 450)]
        });
        Assert.True(world.BlocksFace(a, b));
        liquids.Update(1);
        Assert.Equal(1, liquids.GetUnits(a, LiquidKind.Water));
        Assert.Equal(0, liquids.GetTotalUnits(b));
    }

    [Fact]
    public void PourIsAtomicAndCannotEnterTerrainOrExceedRemainingCrustVolume()
    {
        var cell = new Cell(8, 5, 8);
        var (_, liquids) = Cavity(cell);
        Assert.False(liquids.TryAdd(new Cell(7, 5, 8), LiquidKind.Water, 1));
        liquids.RestoreState(new LiquidState { Obsidian = [new(cell, 2, CrustFace.West, 100)] });
        Assert.False(liquids.TryAdd(cell, LiquidKind.Water, 9));
        Assert.Equal(0, liquids.GetTotalUnits(cell));
        Assert.True(liquids.TryAdd(cell, LiquidKind.Water, 8));
        Assert.False(liquids.TryAdd(cell, LiquidKind.Water, 1));
        Assert.Equal(8, liquids.GetTotalUnits(cell));
    }

    [Fact]
    public void InvalidRestoreLeavesExistingFluidsSolidsAndClockUntouched()
    {
        var cell = new Cell(8, 5, 8);
        var (world, liquids) = Cavity(cell);
        Assert.True(liquids.TryAdd(cell, LiquidKind.Water, 8));
        liquids.Update(.037f);
        var before = JsonSerializer.Serialize(liquids.ExportState());
        Assert.Throws<InvalidDataException>(() => liquids.RestoreState(new LiquidState
        {
            Cells = [new(cell, 9, 0, 0, 2, CrustFace.West)],
            Obsidian = [new(cell, 2, CrustFace.West, 100)]
        }));
        Assert.Empty(world.ObsidianCells);
        Assert.Equal(before, JsonSerializer.Serialize(liquids.ExportState()));
    }

    private static int Total(LiquidSystem liquids) => liquids.Cells.Sum(c => c.Water + c.Mercury + c.Lava);
    private static (WorldGrid World, LiquidSystem Liquids) Cavity(params Cell[] cells)
    {
        var game = new GameSession();
        game.Economy.Credit(100);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        foreach (var cell in cells) Assert.True(game.Dig(cell));
        return (game.World, game.Liquids);
    }
}
