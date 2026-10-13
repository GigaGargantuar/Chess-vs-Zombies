using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class BlockInventoryTests
{
    [Fact]
    public void ToolsHaveSeparateMaterialPermissionsAndPurchasesCannotOverdrawTheWallet()
    {
        var game = new GameSession();
        Assert.False(game.Dig(new Cell(8, 15, 8)));
        Assert.False(game.Dig(new Cell(8, 11, 8)));
        Assert.False(game.Inventory.HasShovel);
        Assert.False(game.Inventory.HasPickaxe);
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        Assert.Equal(0, game.Economy.Currency);
        Assert.True(game.Inventory.HasPickaxe);
        Assert.False(game.Inventory.HasShovel);
        Assert.False(game.BuyTool(ToolKind.Shovel));
        Assert.False(game.BuyTool(ToolKind.Pickaxe));
        Assert.False(game.Dig(new Cell(8, 15, 8)));
        Assert.True(game.Dig(new Cell(8, 11, 8)));
        Assert.Equal(1, game.Inventory.GetCount(Material.Stone));
        Assert.Equal(0, game.Inventory.GetCount(Material.Dirt));
        Assert.Equal(0, game.Economy.Currency);
    }

    [Fact]
    public void ShovelCostsOneHundredAndCanBePurchasedOnlyOnce()
    {
        var game = new GameSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Inventory.HasShovel);
        Assert.Equal(900, game.Economy.Currency);
        Assert.False(game.BuyTool(ToolKind.Shovel));
        Assert.False(game.BuyTool(ToolKind.Pickaxe));
        Assert.Equal(900, game.Economy.Currency);
        Assert.False(game.Dig(new Cell(8, 11, 8)));
        Assert.Equal(Material.Stone, game.World.GetMaterial(new Cell(8, 11, 8)));
        Assert.True(game.StartLevel());
        game.Economy.Credit(1000);
        Assert.False(game.BuyTool(ToolKind.Pickaxe));
        Assert.Equal(1900, game.Economy.Currency);
    }

    [Fact]
    public void DirtExcavationPlacementAndRemovalConserveBlocksWithoutChargingCurrency()
    {
        var game = new GameSession();
        var terrain = new Cell(8, 15, 8);
        var placement = new Cell(12, 16, 12);
        Assert.False(game.BuildBarrier(placement));
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Dig(terrain));
        Assert.Equal(Material.Air, game.World.GetMaterial(terrain));
        Assert.Equal(1, game.Inventory.GetCount(Material.Dirt));
        Assert.False(game.Dig(terrain));
        Assert.False(game.BuildBarrier(new Cell(12, 18, 12)));
        Assert.False(game.BuildBarrier(placement, Material.Stone));
        Assert.Equal(1, game.Inventory.GetCount(Material.Dirt));
        Assert.True(game.BuildBarrier(placement));
        Assert.Equal(0, game.Inventory.GetCount(Material.Dirt));
        var barrier = Assert.Single(game.World.Barriers);
        Assert.Equal(Material.Dirt, barrier.Content);
        Assert.Equal(100, barrier.Hp);
        barrier.Hp -= 27;
        Assert.True(game.Remove(placement));
        Assert.Empty(game.World.Barriers);
        Assert.Equal(1, game.Inventory.GetCount(Material.Dirt));
        Assert.Equal(900, game.Economy.Currency);
        Assert.False(game.Remove(placement));
        Assert.Equal(1, game.Inventory.GetCount(Material.Dirt));
    }

    [Theory]
    [InlineData(Material.Dirt, 100)]
    [InlineData(Material.Stone, 500)]
    public void FortificationMaterialDeterminesDurabilityAndTheReturnedInventoryType(Material material, int hp)
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(material));
        Assert.True(game.BuildBarrier(new Cell(12, 16, 12), material));
        var barrier = Assert.Single(game.World.Barriers);
        Assert.Equal(material, barrier.Content);
        Assert.Equal(hp, barrier.Hp);
        Assert.Equal(hp, Balance.BlockHp(material));
        Assert.Equal(0, game.Inventory.GetCount(material));
        Assert.True(game.Remove(barrier.Cell));
        Assert.Equal(1, game.Inventory.GetCount(material));
        Assert.Equal(1000, game.Economy.Currency);
    }

    [Fact]
    public void InvalidInventoryOperationsAreAtomicAndCannotCreateNegativeOrOverflowedCounts()
    {
        var inventory = new BlockInventory();
        Assert.False(inventory.Add(Material.Air));
        Assert.False(inventory.Add(Material.Dirt, -1));
        Assert.False(inventory.TrySpend(Material.Dirt));
        Assert.True(inventory.Add(Material.Dirt, int.MaxValue));
        Assert.False(inventory.Add(Material.Dirt));
        Assert.False(inventory.TrySpend(Material.Dirt, -1));
        Assert.Equal(int.MaxValue, inventory.GetCount(Material.Dirt));
        Assert.True(inventory.TrySpend(Material.Dirt, int.MaxValue));
        Assert.Equal(0, inventory.GetCount(Material.Dirt));
        Assert.Equal(0, inventory.GetCount(Material.Stone));
    }
}
