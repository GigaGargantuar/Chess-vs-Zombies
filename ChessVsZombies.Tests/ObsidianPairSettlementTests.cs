using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ObsidianPairSettlementTests
{
    private static readonly Cell Ground = new(8, 16, 8);

    [Theory]
    [InlineData(CrustFace.West, CrustFace.East)]
    [InlineData(CrustFace.East, CrustFace.West)]
    [InlineData(CrustFace.North, CrustFace.South)]
    [InlineData(CrustFace.South, CrustFace.North)]
    public void DisjointVerticalCrustsFallPastEachOtherAndRetainBothSlabs(CrustFace lowerFace, CrustFace upperFace)
    {
        var lower = new ObsidianCell(Ground + new Cell(0, 3, 0), 2, lowerFace, 7);
        var upper = new ObsidianCell(Ground + new Cell(0, 4, 0), 3, upperFace, 11);
        var game = WithCrusts(lower, upper);

        game.Update(.01f);

        var settled = Assert.Single(game.World.ObsidianCells);
        Assert.Equal(Ground, settled.Cell);
        Assert.Equal(5, settled.Units);
        Assert.Equal(18, settled.Hp);
        AssertPart(settled, lower with { Cell = Ground });
        AssertPart(settled, upper with { Cell = Ground });
        Assert.False(game.World.IsSolidAt(Ground.Center));
        Assert.Empty(game.Feedback.Hits);
        Assert.Equal(0, game.Inventory.ObsidianLayers);
        Assert.Equal(2, game.Falls.Blocks.Count);
        Assert.Contains(game.Falls.Blocks, fall => fall.Destination == Ground && fall.Crust?.Face == lowerFace && fall.Position == lower.Cell.Center);
        Assert.Contains(game.Falls.Blocks, fall => fall.Destination == Ground && fall.Crust?.Face == upperFace && fall.Position == upper.Cell.Center);
        var revision = game.World.Revision;

        game.Update(.01f);

        Assert.Equal(revision, game.World.Revision);
        Assert.Equal(settled, Assert.Single(game.World.ObsidianCells));
    }

    [Theory]
    [InlineData(CrustFace.West, CrustFace.East)]
    [InlineData(CrustFace.North, CrustFace.South)]
    public void FallingCrustFitsBesideItsGroundedOppositeWithoutMovingThatPartner(CrustFace groundedFace, CrustFace fallingFace)
    {
        var grounded = new ObsidianCell(Ground, 2, groundedFace, 6);
        var falling = new ObsidianCell(Ground + new Cell(0, 3, 0), 3, fallingFace, 12);
        var game = WithCrusts(grounded, falling);

        game.Update(.01f);

        var settled = Assert.Single(game.World.ObsidianCells);
        AssertPart(settled, grounded);
        AssertPart(settled, falling with { Cell = Ground });
        var animation = Assert.Single(game.Falls.Blocks);
        Assert.Equal(fallingFace, animation.Crust!.Face);
        Assert.Equal(Ground, animation.Destination);
        Assert.Equal(falling.Cell.Center, animation.Position);
        Assert.Empty(game.Feedback.Hits);
    }

    [Theory]
    [InlineData(CrustFace.West, CrustFace.East)]
    [InlineData(CrustFace.North, CrustFace.South)]
    public void LoadedFallingSlabShattersOnGroundWithoutDestroyingItsStationaryPartner(CrustFace stationaryFace, CrustFace fallingFace)
    {
        var stationary = new ObsidianCell(Ground, 2, stationaryFace, 7);
        var source = Ground + new Cell(0, 3, 0);
        var game = WithCrusts(stationary, new(source, 3, fallingFace, 11));
        AddBarrier(game, source + new Cell(0, 1, 0), Material.Iron, 1337);
        var witness = new Zombie
        {
            Id = 99, WaveIndex = -1, Kind = ZombieKind.Armored, Tier = GearTier.Iron,
            Position = Ground.Center + Vector3.UnitZ
        };
        game.Zombies.Add(witness);

        game.Update(.01f);

        Assert.Equal(stationary, Assert.Single(game.World.ObsidianCells));
        var load = Assert.Single(game.World.Barriers);
        Assert.Equal(Ground + new Cell(0, 1, 0), load.Cell);
        Assert.Equal(Material.Iron, load.Content);
        Assert.Equal(1337, load.Hp);
        Assert.Equal(140, witness.Hp);
        var hit = Assert.Single(game.Feedback.Hits);
        Assert.Equal(DamageKind.Pierce, hit.Kind);
        Assert.Equal(20, hit.Amount);
        Assert.Equal(0, game.Inventory.ObsidianLayers);
        Assert.DoesNotContain(game.Falls.Blocks, fall => fall.Crust?.Face == stationaryFace);

        game.Update(.01f);

        Assert.Equal(stationary, Assert.Single(game.World.ObsidianCells));
        Assert.Equal(140, witness.Hp);
        Assert.Single(game.Feedback.Hits);
    }

    [Theory]
    [InlineData(CrustFace.West, 2, CrustFace.West, 2)]
    [InlineData(CrustFace.West, 2, CrustFace.East, 9)]
    [InlineData(CrustFace.North, 2, CrustFace.South, 9)]
    [InlineData(CrustFace.West, 2, CrustFace.North, 2)]
    public void ActualFootprintOverlapStillStopsTheUpperCrust(CrustFace lowerFace, int lowerUnits, CrustFace upperFace, int upperUnits)
    {
        var lower = new ObsidianCell(Ground, lowerUnits, lowerFace, lowerUnits * 5);
        var upper = new ObsidianCell(Ground + new Cell(0, 1, 0), upperUnits, upperFace, upperUnits * 5);
        var game = WithCrusts(lower, upper);

        game.Update(.01f);

        Assert.Equal(lower, game.World.GetObsidian(lower.Cell));
        Assert.Equal(upper, game.World.GetObsidian(upper.Cell));
        Assert.Equal(2, game.World.ObsidianCells.Count());
        Assert.Empty(game.Falls.Blocks);
        Assert.Empty(game.Feedback.Hits);
    }

    [Theory]
    [InlineData(CrustFace.West, CrustFace.East, -1, 0)]
    [InlineData(CrustFace.North, CrustFace.South, 0, -1)]
    public void AFreeSlabFallsIndependentlyOfItsSideAnchoredPartner(CrustFace anchoredFace, CrustFace fallingFace, int dx, int dz)
    {
        var source = Ground + new Cell(0, 3, 0);
        var game = WithCrusts(new ObsidianCell(source, 5, anchoredFace, 18, OppositeUnits: 3, OppositeHp: 11));
        PlaceTerrain(game, source + new Cell(dx, 0, dz), Material.Stone);

        game.Update(.01f);

        Assert.Equal(new(source, 2, anchoredFace, 7), game.World.GetObsidian(source));
        Assert.Equal(new(Ground, 3, fallingFace, 11), game.World.GetObsidian(Ground));
        Assert.Equal(2, game.World.ObsidianCells.Count());
        Assert.Equal(5, game.World.ObsidianCells.Sum(crust => crust.Units));
        Assert.Equal(18, game.World.ObsidianCells.Sum(crust => crust.Hp));
        var animation = Assert.Single(game.Falls.Blocks);
        Assert.Equal(fallingFace, animation.Crust!.Face);
        Assert.Equal(source.Center, animation.Position);
        Assert.Empty(game.Feedback.Hits);
    }

    [Theory]
    [InlineData(CrustFace.West)]
    [InlineData(CrustFace.North)]
    public void AnUnsupportedPairedCellFallsUnderItsLoadAndShattersOnceOnGround(CrustFace face)
    {
        var source = Ground + new Cell(0, 3, 0);
        var game = WithCrusts(new ObsidianCell(source, 5, face, 18, OppositeUnits: 3, OppositeHp: 11));
        AddBarrier(game, source + new Cell(0, 1, 0), Material.Stone, 412);
        var witness = new Zombie
        {
            Id = 99, WaveIndex = -1, Kind = ZombieKind.Armored, Tier = GearTier.Iron,
            Position = Ground.Center + Vector3.UnitZ
        };
        game.Zombies.Add(witness);

        game.Update(.01f);

        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        Assert.Equal(Material.Air, game.World.GetMaterial(source + new Cell(0, 1, 0)));
        var load = Assert.Single(game.World.Barriers);
        Assert.Equal(Ground, load.Cell);
        Assert.Equal(Material.Stone, load.Content);
        Assert.Equal(412, load.Hp);
        Assert.Empty(game.World.ObsidianCells);
        Assert.Equal(0, game.Inventory.ObsidianLayers);
        Assert.Equal(140, witness.Hp);
        var hit = Assert.Single(game.Feedback.Hits);
        Assert.Equal(DamageKind.Pierce, hit.Kind);
        Assert.Equal(20, hit.Amount);
        var revision = game.World.Revision;

        game.Update(.01f);

        Assert.Equal(revision, game.World.Revision);
        Assert.Equal(140, witness.Hp);
        Assert.Single(game.Feedback.Hits);
    }

    private static GameSession WithCrusts(params ObsidianCell[] crusts)
    {
        var game = new GameSession();
        game.Liquids.RestoreState(new LiquidState { Obsidian = crusts.ToList() });
        return game;
    }

    private static void AssertPart(ObsidianCell cell, ObsidianCell expected) =>
        Assert.Equal(expected, Assert.Single(cell.Parts, part => part.Face == expected.Face));

    private static void AddBarrier(GameSession game, Cell cell, Material material, float hp)
    {
        Assert.True(game.Inventory.Add(material));
        Assert.True(game.BuildBarrier(cell, material), game.Message);
        game.World.GetBarrier(cell)!.Hp = hp;
    }

    private static void PlaceTerrain(GameSession game, Cell cell, Material material)
    {
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)place.Invoke(game.World, [cell, material])!);
    }
}
