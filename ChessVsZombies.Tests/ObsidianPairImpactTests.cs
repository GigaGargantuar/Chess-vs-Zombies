using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ObsidianPairImpactTests
{
    [Fact]
    public void LoadedPairOnlyShattersTheSlabThatHitsGroundAndPreservesTheOtherSlabsFall()
    {
        var game = new GameSession();
        var source = new Cell(8, 20, 8);
        var landing = new Cell(8, 18, 8);
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian =
            [
                new(source, 4, CrustFace.West, 17, OppositeUnits: 2, OppositeHp: 7),
                new(landing, 2, CrustFace.West, 9)
            ]
        });
        // The lower West slab is anchored; the opposite side anchor catches the
        // East slab only after it passes the first collision without shattering.
        PlaceTerrain(game, landing - new Cell(1, 0, 0));
        PlaceTerrain(game, landing + new Cell(1, 0, 0));
        Assert.True(game.Inventory.Add(Material.Stone));
        Assert.True(game.BuildBarrier(source + new Cell(0, 1, 0), Material.Stone), game.Message);
        var witness = new Zombie
        {
            Id = 100, WaveIndex = -1, Kind = ZombieKind.Armored, Tier = GearTier.Iron,
            Position = new Vector3(8.5f, 19.5f, 9.5f)
        };
        game.Zombies.Add(witness);

        game.Update(.01f);

        Assert.Null(game.World.GetObsidian(source));
        var settled = Assert.IsType<ObsidianCell>(game.World.GetObsidian(landing));
        Assert.Equal(4, settled.Units);
        Assert.Equal(16, settled.Hp);
        Assert.Equal(9, settled.Parts.Single(part => part.Face == CrustFace.West).Hp);
        Assert.Equal(7, settled.Parts.Single(part => part.Face == CrustFace.East).Hp);
        Assert.Equal(500, game.World.GetBarrier(landing + new Cell(0, 1, 0))!.Hp);
        Assert.Equal(140, witness.Hp);
        Assert.Single(game.Feedback.Hits);
        var movingCrust = Assert.Single(game.Falls.Blocks, block => block.Crust is not null);
        Assert.Equal(CrustFace.East, movingCrust.Crust!.Face);
        Assert.Equal(source.Center, movingCrust.Position);
        Assert.Equal(landing, movingCrust.Destination);
        Assert.Equal((source + new Cell(0, 1, 0)).Center,
            Assert.Single(game.Falls.Blocks, block => block.Material == Material.Barrier).Position);

        game.Update(.01f);

        Assert.Equal(140, witness.Hp);
        Assert.Single(game.Feedback.Hits);
        Assert.Equal(4, game.World.ObsidianCells.Sum(crust => crust.Units));
    }

    private static void PlaceTerrain(GameSession game, Cell cell)
    {
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)place.Invoke(game.World, [cell, Material.Stone])!);
    }
}
