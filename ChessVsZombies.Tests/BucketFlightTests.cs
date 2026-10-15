using System.Numerics;
using System.Reflection;
using System.Text.Json;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class BucketFlightTests
{
    [Fact]
    public void ScoopingRemovesOnlyTheExposedKindAndPreservesTheOtherPhase()
    {
        var world = new WorldGrid();
        var liquids = new LiquidSystem(world);
        var cell = new Cell(8, 16, 8);
        Assert.True(liquids.TryAdd(cell, LiquidKind.Mercury, 6));
        Assert.True(liquids.TryAdd(cell, LiquidKind.Water, 4));

        Assert.Equal(4, liquids.TakeSurfaceLiquid(cell, 10, out var kind));
        Assert.Equal(LiquidKind.Water, kind);
        Assert.Equal(6, liquids.GetUnits(cell, LiquidKind.Mercury));
        Assert.Equal(0, liquids.GetUnits(cell, LiquidKind.Water));
        Assert.Equal(6, liquids.TakeSurfaceLiquid(cell, 10, out kind));
        Assert.Equal(LiquidKind.Mercury, kind);
        Assert.Equal(0, Total(liquids));
    }

    [Fact]
    public void ASealedCrustPreventsScoopingTheLiquidBehindIt()
    {
        var world = new WorldGrid();
        var liquids = new LiquidSystem(world);
        var cell = new Cell(8, 16, 8);
        liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(cell, 2, CrustFace.Top, 10)],
            Cells = [new(cell, 7, 0, 0, 2, CrustFace.Top)]
        });

        Assert.False(liquids.TryGetSurfaceLiquid(cell, out _, out _));
        Assert.Equal(0, liquids.TakeSurfaceLiquid(cell, 10, out _));
        Assert.Equal(7, Total(liquids));
    }

    [Fact]
    public void ScoopingCannotReachThroughAnOverlyingLiquidCell()
    {
        var (_, liquids, _) = Create();
        var lower = new Cell(8, 16, 8);
        var upper = lower + new Cell(0, 1, 0);
        Assert.True(liquids.TryAdd(lower, LiquidKind.Lava, 10));
        Assert.True(liquids.TryAdd(upper, LiquidKind.Lava, 6));

        Assert.Equal(0, liquids.TakeSurfaceLiquid(lower, 10, out _));
        Assert.Equal(6, liquids.TakeSurfaceLiquid(upper, 10, out var kind));
        Assert.Equal(LiquidKind.Lava, kind);
        Assert.Equal(10, liquids.TakeSurfaceLiquid(lower, 10, out _));
        Assert.Equal(0, Total(liquids));
    }

    [Fact]
    public void OneBucketLandsThreeBlocksAwayAndFormsABalancedThreeByThreePuddle()
    {
        var (world, liquids, buckets) = Create();
        Assert.True(buckets.Launch(new Vector3(5.5f, 16, 5.5f), Vector3.UnitX, LiquidKind.Water, 10));

        buckets.Update(2);

        Assert.Empty(buckets.Flights);
        Assert.Empty(buckets.PendingSplashes);
        Assert.Equal(10, Total(liquids));
        var puddle = liquids.Cells.Where(cell => cell.Water > 0).ToArray();
        Assert.Equal(9, puddle.Length);
        Assert.All(puddle, cell =>
        {
            Assert.InRange(cell.Cell.X, 7, 9);
            Assert.InRange(cell.Cell.Z, 4, 6);
            Assert.Equal(16, cell.Cell.Y);
            Assert.InRange(cell.Water, 1, 2);
            Assert.Equal(Material.Air, world.GetMaterial(cell.Cell));
        });
        Assert.Equal(2, liquids.GetUnits(new Cell(8, 16, 5), LiquidKind.Water));
    }

    [Fact]
    public void AThrowContinuesBeyondItsNominalRangeWhenTheGroundIsLower()
    {
        var (world, liquids, buckets) = Create();
        Place(world, new Cell(5, 19, 5), Material.Stone);
        Assert.True(buckets.Launch(new Vector3(5.5f, 20, 5.5f), Vector3.UnitX, LiquidKind.Lava, 10));

        buckets.Update(1.01f);

        var flight = Assert.Single(buckets.Flights);
        Assert.True(flight.Position.X > 8.5f);
        Assert.True(flight.Position.Y > 19.9f);
        Assert.Equal(0, Total(liquids));
        buckets.Update(2);
        Assert.Empty(buckets.Flights);
        Assert.Equal(10, Total(liquids) + buckets.PendingUnits);
        Assert.True(liquids.Cells.Where(cell => cell.Lava > 0).Average(cell => cell.Cell.X) > 8.5);
    }

    [Fact]
    public void AHandHeightThrowReachesTheStandingGroundExactlyThreeBlocksAway()
    {
        var (_, liquids, buckets) = Create();
        Assert.True(buckets.Launch(new Vector3(5.5f, 16.65f, 5.5f), Vector3.UnitX,
            LiquidKind.Water, 10, standingHeight: 16));
        var flight = Assert.Single(buckets.Flights);
        Assert.Equal(16, flight.GetPositionAt(1).Y, 4);
        Assert.Equal(8.5f, flight.GetPositionAt(1).X, 4);

        buckets.Update(2);

        Assert.Empty(buckets.Flights);
        Assert.Equal(10, Total(liquids));
        Assert.Equal(2, liquids.GetUnits(new Cell(8, 16, 5), LiquidKind.Water));
        Assert.All(liquids.Cells.Where(cell => cell.Water > 0), cell => Assert.InRange(cell.Cell.X, 7, 9));
    }

    [Fact]
    public void AHandHeightThrowKeepsItsArcPastThreeBlocksOverLowerGround()
    {
        var (world, liquids, buckets) = Create();
        Place(world, new Cell(5, 19, 5), Material.Stone);
        Assert.True(buckets.Launch(new Vector3(5.5f, 20.65f, 5.5f), Vector3.UnitX,
            LiquidKind.Lava, 10, standingHeight: 20));

        buckets.Update(1.01f);

        var flight = Assert.Single(buckets.Flights);
        Assert.True(flight.Position.X > 8.5f);
        Assert.True(flight.Position.Y < 20);
        Assert.Equal(0, Total(liquids));
        buckets.Update(2);
        Assert.Equal(10, Total(liquids) + buckets.PendingUnits);
        Assert.True(liquids.Cells.Where(cell => cell.Lava > 0).Average(cell => cell.Cell.X) > 8.5);
    }

    [Fact]
    public void AFlightPassesThroughTheActualOpeningBetweenOppositeCrusts()
    {
        var (_, liquids, buckets) = Create();
        liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(new Cell(8, 17, 5), 4, CrustFace.West, 20, OppositeUnits: 2, OppositeHp: 10)]
        });
        Assert.True(buckets.Launch(new Vector3(8.5f, 17, 2.5f), Vector3.UnitZ, LiquidKind.Water, 10));

        buckets.Update(.9f);

        var flight = Assert.Single(buckets.Flights);
        Assert.True(flight.Position.Z > 5);
        buckets.Update(2);
        Assert.Empty(buckets.Flights);
        Assert.Equal(10, Total(liquids) + buckets.PendingUnits);
    }

    [Fact]
    public void AFlightHitsASolidCrustInsteadOfPassingThroughItsCell()
    {
        var (_, liquids, buckets) = Create();
        liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(new Cell(8, 17, 5), 10, CrustFace.West, 50)]
        });
        Assert.True(buckets.Launch(new Vector3(8.5f, 17, 2.5f), Vector3.UnitZ, LiquidKind.Water, 10));

        buckets.Update(.9f);

        Assert.Empty(buckets.Flights);
        Assert.Equal(10, Total(liquids) + buckets.PendingUnits);
        Assert.All(liquids.Cells.Where(cell => cell.Water > 0), cell => Assert.True(cell.Cell.Z < 5));
    }

    [Fact]
    public void BlockedSplashRetainsItsPayloadAndReleasesItWhenSpaceOpens()
    {
        var (_, liquids, buckets) = Create();
        Assert.True(buckets.Spill(new Vector3(8.5f, 16.5f, 8.5f), LiquidKind.Mercury, 10));

        buckets.Update(0, _ => true);

        Assert.Equal(10, buckets.PendingUnits);
        Assert.Equal(0, Total(liquids));
        buckets.Update(0);
        Assert.Equal(0, buckets.PendingUnits);
        Assert.Equal(10, Total(liquids));
        Assert.Equal(9, liquids.Cells.Count(cell => cell.Mercury > 0));
    }

    [Fact]
    public void OverflowNeverDeletesLiquidOrFillsAnAlreadyFullCell()
    {
        var (_, liquids, buckets) = Create();
        for (int x = 7; x <= 9; x++)
            for (int z = 7; z <= 9; z++) Assert.True(liquids.TryAdd(new Cell(x, 16, z), LiquidKind.Water, 10));
        Assert.True(buckets.Spill(new Vector3(8.5f, 16.5f, 8.5f), LiquidKind.Water, 10));

        buckets.Update(0);

        Assert.Equal(90, Total(liquids));
        Assert.Equal(10, buckets.PendingUnits);
        int carried = liquids.TakeSurfaceLiquid(new Cell(8, 16, 8), 10, out _);
        buckets.Update(0);
        Assert.Equal(10, carried);
        Assert.Equal(100, Total(liquids) + carried + buckets.PendingUnits);
        Assert.Equal(0, buckets.PendingUnits);
    }

    [Fact]
    public void ATopSplashCannotFillTheHiddenVoidBelowAnInternalCrust()
    {
        var (world, liquids, buckets) = Create();
        var cell = new Cell(8, 16, 8);
        liquids.RestoreState(new LiquidState { Obsidian = [new(cell, 2, CrustFace.Bottom, 10, 4)] });
        Assert.True(buckets.Spill(new Vector3(8.5f, 16.8f, 8.5f), LiquidKind.Mercury, 10));

        buckets.Update(0, candidate => candidate.X != 8 || candidate.Z != 8);

        Assert.Equal(0, Total(liquids));
        Assert.Equal(10, buckets.PendingUnits);
        Assert.Equal(new ObsidianCell(cell, 2, CrustFace.Bottom, 10, 4), world.GetObsidian(cell));
    }

    [Fact]
    public void CrossingTheBattlefieldEdgeConservesTheBucketAtTheBoundary()
    {
        var (_, liquids, buckets) = Create();
        Assert.True(buckets.Launch(new Vector3(31.5f, 20, 8.5f), Vector3.UnitX, LiquidKind.Water, 10));

        buckets.Update(2);

        Assert.Empty(buckets.Flights);
        Assert.Equal(10, Total(liquids) + buckets.PendingUnits);
        Assert.All(liquids.Cells.Where(cell => cell.Water > 0), cell => Assert.InRange(cell.Cell.X, 30, 31));
    }

    [Fact]
    public void FlightAndPendingPayloadSurviveJsonRoundTripWithTheirActualCoordinates()
    {
        var (_, _, buckets) = Create();
        Assert.True(buckets.Launch(new Vector3(8.5f, 20.5f, 9.5f), Vector3.UnitX, LiquidKind.Lava, 7));
        buckets.Update(.25f);
        Assert.True(buckets.Spill(new Vector3(6.25f, 16.75f, 12.5f), LiquidKind.Mercury, 3));
        var state = buckets.ExportState();

        string json = JsonSerializer.Serialize(state);
        var (_, _, restored) = Create();
        restored.RestoreState(JsonSerializer.Deserialize<BucketSystemState>(json)!);

        var expected = Assert.Single(state.Flights);
        var actual = Assert.Single(restored.ExportState().Flights);
        Assert.Equal(expected, actual);
        Assert.Equal(state.PendingSplashes, restored.ExportState().PendingSplashes);
        Assert.Equal(buckets.Flights[0].Position, restored.Flights[0].Position);
        Assert.Equal(3, restored.PendingUnits);
    }

    [Fact]
    public void InvalidRestoreIsAtomicAndRejectsMissingVectorComponents()
    {
        var (_, liquids, buckets) = Create();
        Assert.True(buckets.Launch(new Vector3(8.5f, 20.5f, 9.5f), Vector3.UnitX, LiquidKind.Water, 10));
        var before = buckets.ExportState();
        var invalid = new BucketSystemState
        {
            Flights = before.Flights,
            PendingSplashes = [new(new Vector3(8.5f, 16.5f, 8.5f), LiquidKind.Water, 11)]
        };

        Assert.Throws<InvalidDataException>(() => buckets.RestoreState(invalid));

        Assert.Equal(before.Flights, buckets.ExportState().Flights);
        Assert.Empty(buckets.PendingSplashes);
        Assert.Equal(0, Total(liquids));
        string json = JsonSerializer.Serialize(before).Replace("\"X\":8.5,", "");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BucketSystemState>(json));
    }

    private static (WorldGrid World, LiquidSystem Liquids, LiquidBucketSystem Buckets) Create()
    {
        var world = new WorldGrid();
        var liquids = new LiquidSystem(world);
        return (world, liquids, new LiquidBucketSystem(world, liquids));
    }

    private static int Total(LiquidSystem liquids) => liquids.Cells.Sum(cell => cell.Water + cell.Mercury + cell.Lava);

    private static void Place(WorldGrid world, Cell cell, Material material)
    {
        var method = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)method.Invoke(world, [cell, material])!);
    }
}
