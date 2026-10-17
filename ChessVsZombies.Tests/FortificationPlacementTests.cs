using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class FortificationPlacementTests
{
    [Fact]
    public void BlocksCanFortifyTheTurretAreaAndTheInnerEnclosure()
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Dirt, 4));
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(8, 16, 8)));
        Assert.True(game.BuildBarrier(new Cell(9, 16, 8)));
        Assert.True(game.BuildBarrier(new Cell(9, 17, 8)));
        Assert.True(game.BuildBarrier(new Cell(12, 16, 12)));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(9, 18, 8)));
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(12, 17, 12)));
        Assert.False(game.BuildBarrier(new Cell(8, 16, 8))); // Occupied tower cell.
        Assert.Equal(1, game.Inventory.DirtCount);
        Assert.Equal(3, game.World.Barriers.Count());
    }

    [Theory]
    [InlineData(0,16,8)]
    [InlineData(31,16,8)]
    [InlineData(8,16,0)]
    [InlineData(8,16,31)]
    [InlineData(15,16,15)]
    [InlineData(-1,16,8)]
    public void CoreAndHorizontalSpawnBordersRemainUnavailableForNewFortifications(int x, int y, int z)
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Stone));
        Assert.False(game.BuildBarrier(new Cell(x,y,z), Material.Stone));
        Assert.Equal(1, game.Inventory.StoneCount);
    }

    [Theory]
    [InlineData(1,1)]
    [InlineData(30,30)]
    [InlineData(8,24)]
    public void FortificationsOutsideTheInnerCubeAndTheirFallenPositionsRoundTrip(int x, int z)
    {
        var game = new GameSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Inventory.Add(Material.Stone));
        Assert.True(game.BuildBarrier(new Cell(x,16,z), Material.Stone));
        Assert.True(game.Dig(new Cell(x,15,z)));
        var block = Assert.Single(game.World.Barriers);
        Assert.Equal(new Cell(x,15,z), block.Cell);
        block.Hp = 321;
        var path = Path.Combine(Path.GetTempPath(), $"cvz-block-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(SaveStore.TrySave(game, path, out var error), error);
            Assert.True(SaveStore.TryLoad(path, out var restored, out error), error);
            var loaded = Assert.Single(restored!.World.Barriers);
            Assert.Equal(block.Cell, loaded.Cell);
            Assert.Equal(Material.Stone, loaded.Content);
            Assert.Equal(321, loaded.Hp);
        }
        finally { File.Delete(path); }
    }
}
