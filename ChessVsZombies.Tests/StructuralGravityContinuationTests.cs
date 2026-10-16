using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class StructuralGravityContinuationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AFallingBlockContinuesPastAWallAfterItsTemporaryTowerSupportFalls(int towerCount)
    {
        var game = new GameSession();
        int temporaryLandingY = 20 + towerCount;
        for (int y = 16; y <= temporaryLandingY; y++)
            Place(game, new Cell(9, y, 8), Material.Stone);
        var source = new Cell(8, temporaryLandingY + 2, 8);
        var destination = new Cell(8, 16 + towerCount, 8);
        Place(game, source, Material.Dirt, 33);
        var towers = Enumerable.Range(0, towerCount)
            .Select(index => new Turret
            {
                Id = 100 + index,
                Kind = PieceKind.Pawn,
                Cell = new Cell(8, 20 + index, 8),
                Hp = 17 + index
            }).ToArray();
        game.Turrets.AddRange(towers);

        game.Update(.001f);

        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(8, temporaryLandingY, 8)));
        Assert.Equal(Material.Dirt, game.World.GetMaterial(destination));
        Assert.Equal(33, game.World.GetTerrainHp(destination));
        for (int index = 0; index < towerCount; index++)
        {
            Assert.Equal(new Cell(8, 16 + index, 8), towers[index].Cell);
            Assert.Equal(17 + index, towers[index].Hp);
        }
        var fall = Assert.Single(game.Falls.Blocks);
        Assert.Equal(destination, fall.Destination);
        Assert.Equal(source.Center, fall.Position);
        for (int y = 16; y <= temporaryLandingY; y++)
            Assert.Equal(Material.Stone, game.World.GetMaterial(new Cell(9, y, 8)));

        game.Update(3);

        Assert.Empty(game.Falls.Blocks);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(destination));
        Assert.Equal(33, game.World.GetTerrainHp(destination));
    }

    [Fact]
    public void ADetachedClusterKeepsFallingWhenAnotherFallingClusterVacatesItsTemporaryFloor()
    {
        var game = new GameSession();
        for (int y = 16; y <= 19; y++)
            Place(game, new Cell(7, y, 8), Material.Stone);
        var cluster = Enumerable.Range(20, 6).Select(y => new Cell(8, y, 8))
            .Concat([new Cell(9, 25, 8), new Cell(10, 25, 8)]).ToArray();
        foreach (var cell in cluster) Place(game, cell, Material.Dirt, 33);
        var temporaryFloor = new Cell(10, 23, 8);
        Place(game, temporaryFloor, Material.Copper, 63);
        // The low leg makes the hooked cluster settle first. Its high end lands
        // briefly on the separate copper block while its leg touches the wall.
        // When the copper falls, that new side contact must not stop the hook.

        game.Update(.001f);

        Assert.Equal(Material.Air, game.World.GetMaterial(temporaryFloor));
        Assert.Equal(Material.Copper, game.World.GetMaterial(new Cell(10, 16, 8)));
        Assert.Equal(63, game.World.GetTerrainHp(new Cell(10, 16, 8)));
        Assert.Equal(cluster.Length + 1, game.Falls.Blocks.Count);
        foreach (var origin in cluster)
        {
            var destination = origin - new Cell(0, 4, 0);
            Assert.Equal(Material.Dirt, game.World.GetMaterial(destination));
            Assert.Equal(33, game.World.GetTerrainHp(destination));
            Assert.Equal(origin.Center,
                Assert.Single(game.Falls.Blocks, fall => fall.Destination == destination).Position);
        }
        for (int y = 16; y <= 19; y++)
            Assert.Equal(Material.Stone, game.World.GetMaterial(new Cell(7, y, 8)));

        game.Update(3);

        Assert.Empty(game.Falls.Blocks);
        foreach (var origin in cluster)
            Assert.Equal(Material.Dirt, game.World.GetMaterial(origin - new Cell(0, 4, 0)));
        Assert.Equal(Material.Copper, game.World.GetMaterial(new Cell(10, 16, 8)));
    }

    private static void Place(GameSession game, Cell cell, Material material, float? hp = null)
    {
        Assert.True((bool)typeof(WorldGrid)
            .GetMethod("RestoreTerrainPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game.World, [cell, material])!);
        if (hp.HasValue)
            Assert.True((bool)typeof(WorldGrid)
                .GetMethod("RestoreTerrainHp", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(game.World, [cell, hp.Value])!);
    }
}
