using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ObsidianPairImpactTests
{
    [Fact]
    public void LoadedPairOnlyShattersTheSlabThatHitsThePedestalAndReleasesItsOtherSlabToFall()
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
        // The lower West slab has a real path to ground. Walls alongside the
        // descent cannot catch the East slab after it survives the first impact.
        for (int y = 16; y <= landing.Y; y++)
        {
            PlaceTerrain(game, new Cell(landing.X - 1, y, landing.Z));
            PlaceTerrain(game, new Cell(landing.X + 1, y, landing.Z));
        }
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
        Assert.Equal(new ObsidianCell(landing, 2, CrustFace.West, 9), settled);
        var eastLanding = new Cell(8, 16, 8);
        Assert.Equal(new ObsidianCell(eastLanding, 2, CrustFace.East, 7), game.World.GetObsidian(eastLanding));
        Assert.Equal(500, game.World.GetBarrier(landing + new Cell(0, 1, 0))!.Hp);
        Assert.Equal(190, witness.Hp);
        Assert.Single(game.Feedback.Hits);
        var movingCrust = Assert.Single(game.Falls.Blocks, block => block.Crust is not null);
        Assert.Equal(CrustFace.East, movingCrust.Crust!.Face);
        Assert.Equal(source.Center, movingCrust.Position);
        Assert.Equal(eastLanding, movingCrust.Destination);
        Assert.Equal((source + new Cell(0, 1, 0)).Center,
            Assert.Single(game.Falls.Blocks, block => block.Material == Material.Barrier).Position);

        game.Update(.01f);

        Assert.Equal(190, witness.Hp);
        Assert.Single(game.Feedback.Hits);
        Assert.Equal(4, game.World.ObsidianCells.Sum(crust => crust.Units));
    }

    private static void PlaceTerrain(GameSession game, Cell cell)
    {
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)place.Invoke(game.World, [cell, Material.Stone])!);
    }
}
