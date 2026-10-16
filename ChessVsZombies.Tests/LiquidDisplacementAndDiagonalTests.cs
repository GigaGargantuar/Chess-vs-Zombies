using System.Text.Json;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class LiquidDisplacementAndDiagonalTests
{
    [Fact]
    public void LiquidSpreadsToADiagonalNeighborThroughAnOpenCorner()
    {
        var source = new Cell(8, 5, 8);
        var diagonal = source + new Cell(1, 0, 1);
        var (_, liquids) = Cavity(source, source + new Cell(1, 0, 0), diagonal);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 10));

        liquids.Update(.1f);

        Assert.True(liquids.GetUnits(diagonal, LiquidKind.Water) > 0);
        Assert.Equal(10, Total(liquids));
    }

    [Fact]
    public void DiagonalFlowCannotPassBetweenTwoSolidCornerWalls()
    {
        var source = new Cell(8, 5, 8);
        var diagonal = source + new Cell(1, 0, 1);
        var (_, liquids) = Cavity(source, diagonal);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 10));

        liquids.Update(.5f);

        Assert.Equal(10, liquids.GetUnits(source, LiquidKind.Water));
        Assert.Equal(0, liquids.GetTotalUnits(diagonal));
    }

    [Theory]
    [InlineData(LiquidKind.Water)]
    [InlineData(LiquidKind.Mercury)]
    [InlineData(LiquidKind.Lava)]
    public void OneUnitMovesWholeIntoADeeperDiagonalPool(LiquidKind kind)
    {
        var source = new Cell(8, 5, 8);
        var diagonal = source + new Cell(1, 0, 1);
        var (_, liquids) = Cavity(source, source + new Cell(1, 0, 0), diagonal);
        Assert.True(liquids.TryAdd(source, kind, 1));
        Assert.True(liquids.TryAdd(diagonal, kind, 3));

        liquids.Update(.1f);

        Assert.Equal(0, liquids.GetTotalUnits(source));
        Assert.Equal(4, liquids.GetUnits(diagonal, kind));
        Assert.Equal(4, Total(liquids));
    }

    [Fact]
    public void OneUnitRecognizesTheFullColumnBeneathANeighboringSurface()
    {
        var source = new Cell(8, 5, 8);
        var target = source + new Cell(1, 0, 0);
        var lower = target + new Cell(0, -1, 0);
        var (_, liquids) = Cavity(source, target, lower);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 1));
        Assert.True(liquids.TryAdd(target, LiquidKind.Water, 1));
        Assert.True(liquids.TryAdd(lower, LiquidKind.Water, 10));

        liquids.Update(.1f);

        Assert.Equal(0, liquids.GetTotalUnits(source));
        Assert.Equal(2, liquids.GetUnits(target, LiquidKind.Water));
        Assert.Equal(10, liquids.GetUnits(lower, LiquidKind.Water));
    }

    [Fact]
    public void EqualDepthOneUnitNeighborsRemainStill()
    {
        var source = new Cell(8, 5, 8);
        var other = source + new Cell(1, 0, 0);
        var (_, liquids) = Cavity(source, other);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 1));
        Assert.True(liquids.TryAdd(other, LiquidKind.Water, 1));
        long revision = liquids.Revision;
        liquids.Update(1);

        Assert.Equal(1, liquids.GetUnits(source, LiquidKind.Water));
        Assert.Equal(1, liquids.GetUnits(other, LiquidKind.Water));
        Assert.Equal(revision, liquids.Revision);
    }

    [Fact]
    public void AMergedPoolLevelsIntoStablePairsInsteadOfRepeatedlySplittingAndMerging()
    {
        var source = new Cell(8, 5, 8);
        var middle = source + new Cell(1, 0, 0);
        var diagonal = source + new Cell(1, 0, 1);
        var (_, liquids) = Cavity(source, middle, diagonal);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 1));
        Assert.True(liquids.TryAdd(diagonal, LiquidKind.Water, 3));
        liquids.Update(.1f);
        Assert.Equal(4, liquids.GetUnits(diagonal, LiquidKind.Water));

        liquids.Update(.1f);

        Assert.Equal(new[] { 2, 2 }, liquids.Cells.Select(cell => cell.Water).Order().ToArray());
        long revision = liquids.Revision;
        liquids.Update(2);
        Assert.Equal(revision, liquids.Revision);
        Assert.Equal(4, Total(liquids));
    }

    [Fact]
    public void AnIsolatedTwoUnitPoolCanSplitIntoEqualStableOneUnitNeighbors()
    {
        var source = new Cell(8, 5, 8);
        var other = source + new Cell(1, 0, 0);
        var (_, liquids) = Cavity(source, other);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 2));

        liquids.Update(.1f);

        Assert.Equal(1, liquids.GetUnits(source, LiquidKind.Water));
        Assert.Equal(1, liquids.GetUnits(other, LiquidKind.Water));
        long revision = liquids.Revision;
        liquids.Update(1);
        Assert.Equal(revision, liquids.Revision);
    }

    [Fact]
    public void PlacementPressureConservesEveryPhaseAndItsDryRunDoesNotMutateAnything()
    {
        var source = new Cell(8, 5, 8);
        var above = source + new Cell(0, 1, 0);
        var (_, liquids) = Cavity(source, above);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 4));
        Assert.True(liquids.TryAdd(source, LiquidKind.Mercury, 6));
        string before = JsonSerializer.Serialize(liquids.ExportState());
        long revision = liquids.Revision;

        Assert.True(liquids.CanDisplaceLiquid(source));
        Assert.Equal(before, JsonSerializer.Serialize(liquids.ExportState()));
        Assert.Equal(revision, liquids.Revision);
        Assert.True(liquids.TryDisplaceLiquid(source));

        Assert.Equal(0, liquids.GetTotalUnits(source));
        Assert.Equal(4, liquids.GetUnits(above, LiquidKind.Water));
        Assert.Equal(6, liquids.GetUnits(above, LiquidKind.Mercury));
        Assert.Empty(liquids.Hazards);
    }

    [Fact]
    public void PressureCanTravelThroughAFullColumnToItsAvailableSpace()
    {
        var source = new Cell(8, 5, 8);
        var above = source + new Cell(0, 1, 0);
        var top = above + new Cell(0, 1, 0);
        var (_, liquids) = Cavity(source, above, top);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 10));
        Assert.True(liquids.TryAdd(above, LiquidKind.Water, 10));

        Assert.True(liquids.TryDisplaceLiquid(source));

        Assert.Equal(0, liquids.GetTotalUnits(source));
        Assert.Equal(10, liquids.GetUnits(above, LiquidKind.Water));
        Assert.Equal(10, liquids.GetUnits(top, LiquidKind.Water));
    }

    [Fact]
    public void PlacementPressureUsesADownwardOutletWhenTheOtherFacesAreBlocked()
    {
        var source = new Cell(8, 5, 8);
        var below = source + new Cell(0, -1, 0);
        var (_, liquids) = Cavity(source, below);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 10));

        Assert.True(liquids.TryDisplaceLiquid(source));

        Assert.Equal(0, liquids.GetTotalUnits(source));
        Assert.Equal(10, liquids.GetUnits(below, LiquidKind.Water));
    }

    [Fact]
    public void InsufficientEnclosedSpaceLeavesTheEntireDisplacementUntouched()
    {
        var source = new Cell(8, 5, 8);
        var above = source + new Cell(0, 1, 0);
        var (_, liquids) = Cavity(source, above);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 8));
        Assert.True(liquids.TryAdd(above, LiquidKind.Water, 8));
        string before = JsonSerializer.Serialize(liquids.ExportState());
        long revision = liquids.Revision;

        Assert.False(liquids.CanDisplaceLiquid(source));
        Assert.False(liquids.TryDisplaceLiquid(source));

        Assert.Equal(before, JsonSerializer.Serialize(liquids.ExportState()));
        Assert.Equal(revision, liquids.Revision);
    }

    [Fact]
    public void PlacementPressureCannotCrossASealedCrustFace()
    {
        var source = new Cell(8, 5, 8);
        var (_, liquids) = Cavity(source, source + new Cell(0, 1, 0));
        liquids.RestoreState(new LiquidState
        {
            Cells = [new(source, 8, 0, 0, 2, CrustFace.Top)],
            Obsidian = [new(source, 2, CrustFace.Top, 10)]
        });
        string before = JsonSerializer.Serialize(liquids.ExportState());

        Assert.False(liquids.TryDisplaceLiquid(source));

        Assert.Equal(before, JsonSerializer.Serialize(liquids.ExportState()));
    }

    [Fact]
    public void BoundaryDisplacementRecordsEscapedUnitsWithoutDeletingTheirAccounting()
    {
        var source = new Cell(0, 16, 8);
        var liquids = new LiquidSystem(new WorldGrid());
        Assert.True(liquids.TryAdd(source, LiquidKind.Mercury, 7));

        Assert.True(liquids.TryDisplaceLiquid(source));

        Assert.Equal(0, liquids.GetTotalUnits(source));
        Assert.Equal(7, liquids.GetEscapedUnits(LiquidKind.Mercury));
        Assert.Equal(7, Total(liquids) + liquids.GetEscapedUnits(LiquidKind.Mercury));
    }

    [Fact]
    public void ProposedCrustRetainsFittingLiquidAndDisplacesOnlyItsOverflow()
    {
        var source = new Cell(8, 5, 8);
        var above = source + new Cell(0, 1, 0);
        var (_, liquids) = Cavity(source, above);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 10));
        var crust = new ObsidianCell(source, 1, CrustFace.Bottom, 5);

        Assert.True(liquids.TryDisplaceLiquid(source, 9, crust));

        Assert.Equal(9, liquids.GetUnits(source, LiquidKind.Water));
        Assert.Equal(1, liquids.GetUnits(above, LiquidKind.Water));
        Assert.Equal(10, Total(liquids));
    }

    [Fact]
    public void ProposedInternalCrustChecksBothSeparateLiquidRegions()
    {
        var source = new Cell(8, 5, 8);
        var side = source + new Cell(1, 0, 0);
        var (_, liquids) = Cavity(source, side);
        Assert.True(liquids.TryAdd(source, LiquidKind.Mercury, 5));
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 5));
        var crust = new ObsidianCell(source, 2, CrustFace.Bottom, 10, 2);

        Assert.True(liquids.TryDisplaceLiquid(source, 8, crust));

        Assert.Equal(2, liquids.GetUnits(source, LiquidKind.Mercury));
        Assert.Equal(3, liquids.GetUnits(source, LiquidKind.Water));
        Assert.Equal(3, liquids.GetUnits(side, LiquidKind.Mercury));
        Assert.Equal(2, liquids.GetUnits(side, LiquidKind.Water));
    }

    [Fact]
    public void PressureReservesTheOnlyMercuryOpeningAndUsesTheSeparateWaterOpening()
    {
        var source = new Cell(8, 5, 8);
        var shared = source + new Cell(1, 0, 0);
        var waterOnly = source + new Cell(0, 0, 1);
        var (_, liquids) = Cavity(source, shared, waterOnly);
        liquids.RestoreState(new LiquidState
        {
            Cells = [new(source, 1, 1, 0)],
            Obsidian = [new(shared, 9, CrustFace.East, 45), new(waterOnly, 9, CrustFace.Top, 45, 1)]
        });

        Assert.True(liquids.CanDisplaceLiquid(source));
        Assert.True(liquids.TryDisplaceLiquid(source));

        Assert.Equal(0, liquids.GetTotalUnits(source));
        Assert.Equal(1, liquids.GetUnits(shared, LiquidKind.Mercury));
        Assert.Equal(1, liquids.GetUnits(waterOnly, LiquidKind.Water));
        Assert.Equal(2, Total(liquids));
    }

    [Fact]
    public void PressureCanRetainWaterAndMoveMercuryWhenOnlyMercuryHasAnOutlet()
    {
        var source = new Cell(8, 5, 8);
        var outlet = source + new Cell(1, 0, 0);
        var (_, liquids) = Cavity(source, outlet);
        liquids.RestoreState(new LiquidState
        {
            Cells = [new(source, 1, 5, 0)],
            Obsidian = [new(outlet, 9, CrustFace.Bottom, 45, 1)]
        });

        Assert.True(liquids.CanDisplaceLiquid(source, 5));
        Assert.True(liquids.TryDisplaceLiquid(source, 5));

        Assert.Equal(1, liquids.GetUnits(source, LiquidKind.Water));
        Assert.Equal(4, liquids.GetUnits(source, LiquidKind.Mercury));
        Assert.Equal(1, liquids.GetUnits(outlet, LiquidKind.Mercury));
        Assert.Equal(6, Total(liquids));
    }

    [Fact]
    public void TurretCapacityIncludesCrustForPouringFlowAndRestore()
    {
        var source = new Cell(8, 5, 8);
        var turret = source + new Cell(1, 0, 0);
        var (_, liquids) = Cavity(source, turret);
        liquids.IsTurretCell = cell => cell == turret;
        liquids.RestoreState(new LiquidState
        {
            Cells = [new(source, 10, 0, 0)],
            Obsidian = [new(turret, 2, CrustFace.North, 10)]
        });
        Assert.False(liquids.CanAdd(turret, LiquidKind.Water, 4));

        liquids.Update(.1f);

        Assert.Equal(3, liquids.GetUnits(turret, LiquidKind.Water));
        Assert.False(liquids.TryAdd(turret, LiquidKind.Mercury, 1));
        string before = JsonSerializer.Serialize(liquids.ExportState());
        Assert.Throws<InvalidDataException>(() => liquids.RestoreState(new LiquidState
        {
            Cells = [new(turret, 4, 0, 0, 2, CrustFace.North)],
            Obsidian = [new(turret, 2, CrustFace.North, 10)]
        }));
        Assert.Equal(before, JsonSerializer.Serialize(liquids.ExportState()));
    }

    [Fact]
    public void CapacityReductionMovesOnlyTheExcessOutOfATurretCell()
    {
        var source = new Cell(8, 5, 8);
        var above = source + new Cell(0, 1, 0);
        var (_, liquids) = Cavity(source, above);
        Assert.True(liquids.TryAdd(source, LiquidKind.Water, 10));

        Assert.True(liquids.TryDisplaceLiquid(source, LiquidSystem.TurretCapacityUnits));
        liquids.IsTurretCell = cell => cell == source;

        Assert.Equal(5, liquids.GetUnits(source, LiquidKind.Water));
        Assert.Equal(5, liquids.GetUnits(above, LiquidKind.Water));
        Assert.False(liquids.TryAdd(source, LiquidKind.Water, 1));
    }

    private static int Total(LiquidSystem liquids) => liquids.Cells.Sum(cell => cell.Water + cell.Mercury + cell.Lava);

    private static (WorldGrid World, LiquidSystem Liquids) Cavity(params Cell[] cells)
    {
        var game = new GameSession();
        game.Economy.Credit(1000);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        foreach (var cell in cells) Assert.True(game.Dig(cell));
        return (game.World, game.Liquids);
    }
}
