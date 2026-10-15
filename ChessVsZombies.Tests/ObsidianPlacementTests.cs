using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ObsidianPlacementTests
{
    private static readonly Cell GroundCell = new(8, 16, 8);

    [Theory]
    [InlineData(CrustFace.West)]
    [InlineData(CrustFace.East)]
    [InlineData(CrustFace.Bottom)]
    [InlineData(CrustFace.Top)]
    [InlineData(CrustFace.North)]
    [InlineData(CrustFace.South)]
    public void FirstLayerAttachesToTheChosenFaceAndFurtherLayersKeepThatOrientation(CrustFace face)
    {
        var game = new GameSession();
        var cell = new Cell(8, 18, 8);
        PlaceTerrain(game, cell + Direction(face), Material.Stone);
        Assert.True(game.Inventory.Add(Material.Obsidian, 2));
        Assert.True(game.CanBuildObsidian(cell, face));

        Assert.True(game.BuildObsidian(cell, face), game.Message);

        var first = game.World.GetObsidian(cell)!;
        Assert.Equal(new ObsidianCell(cell, 1, face, 5), first);
        var size = first.Max - first.Min;
        Assert.InRange(size.X * size.Y * size.Z, .09999f, .10001f);
        Assert.True(game.BuildObsidian(cell, face == CrustFace.Top ? CrustFace.West : CrustFace.Top));
        Assert.Equal(new ObsidianCell(cell, 2, face, 10), game.World.GetObsidian(cell));
        Assert.Equal(0, game.Inventory.ObsidianLayers);
        Assert.Equal(1000, game.Economy.Currency);
    }

    [Fact]
    public void LayersAddOnlyTheirOwnHpPreservingDamageAndStopAtOneFullBlock()
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Obsidian, 11));
        Assert.True(game.BuildObsidian(GroundCell, CrustFace.Bottom));
        Assert.False(game.World.DamageObsidian(GroundCell, 2));
        for (int i = 1; i < 10; i++) Assert.True(game.BuildObsidian(GroundCell, CrustFace.East));

        Assert.Equal(new ObsidianCell(GroundCell, 10, CrustFace.Bottom, 48), game.World.GetObsidian(GroundCell));
        var revision = game.World.Revision;
        Assert.False(game.CanBuildObsidian(GroundCell, CrustFace.Bottom));
        Assert.False(game.BuildObsidian(GroundCell, CrustFace.Bottom));
        Assert.Equal(revision, game.World.Revision);
        Assert.Equal(1, game.Inventory.ObsidianLayers);
    }

    [Fact]
    public void ExtendingAnInternalCrustRetainsItsOffsetAndExistingDamage()
    {
        var game = WithCrust(GroundCell, 2, CrustFace.Bottom, 7, offset: 4);
        Assert.True(game.Inventory.Add(Material.Obsidian, 5));
        for (int i = 0; i < 4; i++) Assert.True(game.BuildObsidian(GroundCell, CrustFace.West));

        Assert.Equal(new ObsidianCell(GroundCell, 6, CrustFace.Bottom, 27, 4), game.World.GetObsidian(GroundCell));
        Assert.False(game.BuildObsidian(GroundCell, CrustFace.Bottom));
        Assert.Equal(1, game.Inventory.ObsidianLayers);
    }

    [Fact]
    public void InvalidPlacementsAreAtomicAndAFirstLayerNeedsActualFaceContact()
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Obsidian, 10));
        var revision = game.World.Revision;
        foreach (var (cell, face) in new[]
        {
            (new Cell(8, 18, 8), CrustFace.Bottom), // No support.
            (GroundCell, CrustFace.Top), // Ground exists, but on the other face.
            (new Cell(15, 16, 15), CrustFace.Bottom),
            (new Cell(0, 16, 8), CrustFace.Bottom),
            (GroundCell, (CrustFace)999)
        })
        {
            Assert.False(game.CanBuildObsidian(cell, face));
            Assert.False(game.BuildObsidian(cell, face));
        }
        Assert.Equal(revision, game.World.Revision);
        Assert.Equal(10, game.Inventory.ObsidianLayers);

        Assert.True(game.Deploy(PieceKind.Pawn, GroundCell));
        Assert.False(game.BuildObsidian(GroundCell, CrustFace.Bottom));
        var wet = GroundCell + new Cell(0, 0, 2);
        Assert.True(game.Liquids.TryAdd(wet, LiquidKind.Water, 1));
        Assert.False(game.BuildObsidian(wet, CrustFace.Bottom));
        Assert.Equal(1, game.Liquids.GetUnits(wet, LiquidKind.Water));
        Assert.Equal(10, game.Inventory.ObsidianLayers);
        Assert.True(game.StartLevel());
        Assert.False(game.BuildObsidian(GroundCell + new Cell(2, 0, 0), CrustFace.Bottom));
        Assert.Equal(10, game.Inventory.ObsidianLayers);
    }

    [Fact]
    public void DisjointNeighborCrustDoesNotProvideAFalseAttachmentFace()
    {
        var cell = new Cell(8, 18, 8);
        var game = WithCrust(cell + new Cell(1, 0, 0), 2, CrustFace.East, 10);
        Assert.True(game.Inventory.Add(Material.Obsidian));

        Assert.False(game.CanBuildObsidian(cell, CrustFace.East));
        Assert.False(game.BuildObsidian(cell, CrustFace.East));
        Assert.Equal(1, game.Inventory.ObsidianLayers);
        Assert.Null(game.World.GetObsidian(cell));
    }

    [Fact]
    public void PlacementChecksTheNewSlabVolumeAgainstLivingBodies()
    {
        var game = new GameSession();
        var zombie = new Zombie { Id = 99, WaveIndex = -1, Position = GroundCell.Center };
        game.Zombies.Add(zombie);
        Assert.True(game.Inventory.Add(Material.Obsidian, 2));

        Assert.True(game.BuildObsidian(GroundCell, CrustFace.Bottom)); // Top16.1 remains below body16.11.
        Assert.False(game.BuildObsidian(GroundCell, CrustFace.Bottom)); // Top16.2 would intersect.
        Assert.Equal(1, game.Inventory.ObsidianLayers);
        Assert.Equal(1, game.World.GetObsidian(GroundCell)!.Units);
        zombie.Hp = 0;
        Assert.True(game.BuildObsidian(GroundCell, CrustFace.Bottom));
    }

    [Fact]
    public void UnsupportedLoadedCrustShattersOnceAndPreservesTheFallingLoad()
    {
        var source = new Cell(8, 19, 8);
        var game = WithCrust(source, 2, CrustFace.Bottom, 7);
        AddBarrier(game, source + new Cell(0, 1, 0), Material.Iron, 1337);
        AddBarrier(game, source + new Cell(0, 2, 0), Material.Stone, 321);
        var witness = AddWitness(game);

        game.Update(.01f);

        Assert.Empty(game.World.ObsidianCells);
        var iron = game.World.GetBarrier(GroundCell)!;
        var stone = game.World.GetBarrier(GroundCell + new Cell(0, 1, 0))!;
        Assert.Equal(Material.Iron, iron.Content);
        Assert.Equal(1337, iron.Hp);
        Assert.Equal(Material.Stone, stone.Content);
        Assert.Equal(321, stone.Hp);
        Assert.Equal(190, witness.Hp);
        var hit = Assert.Single(game.Feedback.Hits);
        Assert.Equal(DamageKind.Pierce, hit.Kind);
        Assert.Equal(20, hit.Amount);
        Assert.Equal(0, game.Inventory.ObsidianLayers);
        Assert.Equal(0, game.Inventory.GetCount(Material.Iron));
        Assert.Equal(2, game.Falls.Blocks.Count);
        Assert.Equal((source + new Cell(0, 1, 0)).Center, game.Falls.Blocks.Single(fall => fall.Destination == GroundCell).Position);
        Assert.Equal((source + new Cell(0, 2, 0)).Center, game.Falls.Blocks.Single(fall => fall.Destination == GroundCell + new Cell(0, 1, 0)).Position);

        game.Update(.01f);
        Assert.Equal(190, witness.Hp);
        Assert.Single(game.Feedback.Hits);
    }

    [Theory]
    [InlineData(CrustFace.Top, 0)]
    [InlineData(CrustFace.Bottom, 4)]
    public void ARealFractionalGapUnderLoadedCrustCompressesEvenWithinOneGridCell(CrustFace face, int offset)
    {
        var game = WithCrust(GroundCell, 2, face, 10, offset);
        AddBarrier(game, GroundCell + new Cell(0, 1, 0), Material.Stone, 412);
        var witness = AddWitness(game);

        game.Update(.01f);

        Assert.Null(game.World.GetObsidian(GroundCell));
        Assert.Equal(412, game.World.GetBarrier(GroundCell)!.Hp);
        Assert.Equal(190, witness.Hp);
        Assert.Single(game.Falls.Blocks);
    }

    [Theory]
    [InlineData(2, 16)] // A partial crust already resting on ground.
    [InlineData(10, 19)] // A whole obsidian block is not a fragile crust.
    public void SupportedCrustAndWholeObsidianDoNotShatterUnderAStationaryLoad(int units, int y)
    {
        var source = new Cell(8, y, 8);
        var game = WithCrust(source, units, CrustFace.Bottom, units * 5);
        AddBarrier(game, source + new Cell(0, 1, 0), Material.Stone, 500);
        var witness = AddWitness(game);

        game.Update(.01f);

        Assert.Equal(units, game.World.GetObsidian(source)!.Units);
        Assert.NotNull(game.World.GetBarrier(source + new Cell(0, 1, 0)));
        Assert.Equal(210, witness.Hp);
        Assert.Empty(game.Feedback.Hits);
        Assert.Empty(game.Falls.Blocks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void LateralAttachmentAnywhereInTheLoadedColumnPreventsCompression(int anchoredHeight)
    {
        var source = new Cell(8, 19, 8);
        var game = WithCrust(source, 2, CrustFace.Bottom, 10);
        AddBarrier(game, source + new Cell(0, 1, 0), Material.Stone, 500);
        AddBarrier(game, source + new Cell(0, 2, 0), Material.Stone, 500);
        PlaceTerrain(game, source + new Cell(1, anchoredHeight, 0), Material.Stone);

        game.Update(.01f);

        Assert.NotNull(game.World.GetObsidian(source));
        Assert.NotNull(game.World.GetBarrier(source + new Cell(0, 2, 0)));
        Assert.Empty(game.Falls.Blocks);
    }

    [Fact]
    public void FallingStackStopsAtARealSideAnchorBeforeReachingTheGround()
    {
        var source = new Cell(8, 19, 8);
        var game = WithCrust(source, 2, CrustFace.Bottom, 10);
        AddBarrier(game, source + new Cell(0, 1, 0), Material.Stone, 500);
        PlaceTerrain(game, new Cell(9, 17, 8), Material.Stone);

        game.Update(.01f);

        var landing = new Cell(8, 17, 8);
        Assert.Equal(new ObsidianCell(landing, 2, CrustFace.Bottom, 10), game.World.GetObsidian(landing));
        Assert.NotNull(game.World.GetBarrier(landing + new Cell(0, 1, 0)));
        Assert.Empty(game.Feedback.Hits);
        Assert.Equal(2, game.Falls.Blocks.Count);
    }

    [Fact]
    public void AnUnloadedCrustFallsNormallyWithoutShattering()
    {
        var game = WithCrust(new Cell(8, 19, 8), 2, CrustFace.Bottom, 8);
        var witness = AddWitness(game);

        game.Update(.01f);

        Assert.Equal(new ObsidianCell(GroundCell, 2, CrustFace.Bottom, 8), game.World.GetObsidian(GroundCell));
        Assert.Equal(210, witness.Hp);
        Assert.Empty(game.Feedback.Hits);
    }

    [Fact]
    public void FallingAndShatteringAFluidBearingCrustConservesEveryLiquidUnit()
    {
        var source = new Cell(8, 19, 8);
        var game = new GameSession();
        game.Liquids.RestoreState(new LiquidState
        {
            Cells = [new(source, 2, 0, 0, 2, CrustFace.Bottom), new(source - new Cell(0, 1, 0), 10, 0, 0)],
            Obsidian = [new(source, 2, CrustFace.Bottom, 10)]
        });
        AddBarrier(game, source + new Cell(0, 1, 0), Material.Stone, 500);

        game.Update(.01f); // Below the flow tick: only settlement may move the liquid.

        Assert.Empty(game.World.ObsidianCells);
        Assert.Equal(12, game.Liquids.Cells.Sum(cell => cell.Water));
        Assert.Equal(0, game.Liquids.GetTotalUnits(GroundCell));
        Assert.All(game.Liquids.Cells, cell => Assert.Equal(Material.Air, game.World.GetMaterial(cell.Cell)));
    }

    private static GameSession WithCrust(Cell cell, int units, CrustFace face, float hp, int offset = 0)
    {
        var game = new GameSession();
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(cell, units, face, hp, offset)] });
        return game;
    }

    private static void AddBarrier(GameSession game, Cell cell, Material material, float hp)
    {
        Assert.True(game.Inventory.Add(material));
        Assert.True(game.BuildBarrier(cell, material), game.Message);
        game.World.GetBarrier(cell)!.Hp = hp;
    }

    private static Zombie AddWitness(GameSession game)
    {
        var zombie = new Zombie
        {
            Id = 100, WaveIndex = -1, Kind = ZombieKind.Armored, Tier = GearTier.Iron,
            Position = GroundCell.Center + Vector3.UnitX
        };
        game.Zombies.Add(zombie);
        Assert.Equal(210, zombie.Hp);
        return zombie;
    }

    private static void PlaceTerrain(GameSession game, Cell cell, Material material)
    {
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)place.Invoke(game.World, [cell, material])!);
    }

    private static Cell Direction(CrustFace face) => face switch
    {
        CrustFace.West => new(-1, 0, 0), CrustFace.East => new(1, 0, 0),
        CrustFace.Bottom => new(0, -1, 0), CrustFace.Top => new(0, 1, 0),
        CrustFace.North => new(0, 0, -1), CrustFace.South => new(0, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(face))
    };
}
