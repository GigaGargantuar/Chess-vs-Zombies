using System.Text.Json;
using System.Text.Json.Serialization;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class BlockSalesTests
{
    [Theory]
    [InlineData(Material.Wood, 100)]
    [InlineData(Material.Stone, 200)]
    [InlineData(Material.Copper, 300)]
    [InlineData(Material.Iron, 500)]
    [InlineData(Material.Gold, 800)]
    [InlineData(Material.Diamond, 1300)]
    [InlineData(Material.Osmium, 2100)]
    public void EachSaleConsumesExactlyOneBlockAndCreditsItsFullValue(Material material, int value)
    {
        var game = new GameSession();
        Assert.Equal(value, Balance.BlockSellValue(material));
        Assert.False(game.SellBlock(material));
        Assert.Equal(1000, game.Economy.Currency);
        Assert.True(game.Inventory.Add(Material.Dirt, 3));
        Assert.True(game.Inventory.Add(material, 2));

        Assert.True(game.SellBlock(material), game.Message);
        Assert.Equal(1, game.Inventory.GetCount(material));
        Assert.Equal(1000 + value, game.Economy.Currency);
        Assert.True(game.SellBlock(material));
        Assert.Equal(0, game.Inventory.GetCount(material));
        Assert.Equal(1000 + 2 * value, game.Economy.Currency);
        Assert.False(game.SellBlock(material));
        Assert.Equal(1000 + 2 * value, game.Economy.Currency);
        Assert.Equal(3, game.Inventory.GetCount(Material.Dirt));
    }

    [Theory]
    [InlineData(Material.Dirt)]
    [InlineData(Material.Air)]
    [InlineData(Material.Core)]
    [InlineData(Material.Barrier)]
    [InlineData(Material.Obsidian)]
    [InlineData((Material)999)]
    public void DirtAndInvalidSaleMaterialsDoNotConsumeInventoryOrChangeCurrency(Material material)
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Dirt, 2));
        Assert.True(game.Inventory.Add(Material.Wood));

        Assert.False(game.SellBlock(material));

        Assert.Equal(1000, game.Economy.Currency);
        Assert.Equal(2, game.Inventory.GetCount(Material.Dirt));
        Assert.Equal(1, game.Inventory.GetCount(Material.Wood));
        Assert.Equal(0, Balance.BlockSellValue(Material.Dirt));
    }

    [Fact]
    public void AnOverflowingSalePreservesTheBlockAndTheWallet()
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Osmium, 2));
        game.Economy.Credit(int.MaxValue - 1000 - 2100);
        Assert.True(game.SellBlock(Material.Osmium));
        Assert.Equal(int.MaxValue, game.Economy.Currency);

        Assert.False(game.SellBlock(Material.Osmium));

        Assert.Equal(int.MaxValue, game.Economy.Currency);
        Assert.Equal(1, game.Inventory.GetCount(Material.Osmium));
    }

    [Fact]
    public void SalesAreUnavailableThroughoutCombatAndDefeat()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Kind = ZombieKind.Armed, Tier = GearTier.Osmium }] }] });
        var game = new GameSession(levels: levels);
        Assert.True(game.Inventory.Add(Material.Gold, 2));
        Assert.True(game.StartLevel());
        Assert.False(game.SellBlock(Material.Gold));
        Assert.Equal(2, game.Inventory.GetCount(Material.Gold));
        Assert.Equal(1000, game.Economy.Currency);
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = new Cell(14, 16, 15).Center;
        zombie.NextCell = null;
        zombie.Cooldown = 0;
        game.Update(6);
        Assert.Equal(GamePhase.Defeat, game.Phase);

        Assert.False(game.SellBlock(Material.Gold));

        Assert.Equal(2, game.Inventory.GetCount(Material.Gold));
        Assert.Equal(1000, game.Economy.Currency);
    }

    [Theory]
    [InlineData(Material.Wood, 250, 100)]
    [InlineData(Material.Stone, 500, 200)]
    [InlineData(Material.Copper, 1000, 300)]
    [InlineData(Material.Iron, 2000, 500)]
    [InlineData(Material.Gold, 3000, 800)]
    [InlineData(Material.Diamond, 5000, 1300)]
    [InlineData(Material.Osmium, 8000, 2100)]
    public void PickaxeExcavatesEveryRecoveredMaterialAndTheCollectedBlockCanBeSold(Material material, int hp, int value)
    {
        var cell = new Cell(8, 16, 8);
        var game = WithRecoveredTerrain(material, cell);
        Assert.Equal(material, game.World.GetMaterial(cell));
        Assert.Equal(hp, game.World.GetTerrainHp(cell));
        Assert.False(game.Dig(cell));
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.False(game.Dig(cell)); // The shovel does not acquire any non-dirt permission.
        Assert.Equal(0, game.Inventory.GetCount(material));
        Assert.Equal(material, game.World.GetMaterial(cell));
        game.Economy.Credit(100);
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        Assert.Equal(0, game.Economy.Currency);

        Assert.True(game.Dig(cell), game.Message);

        Assert.Equal(Material.Air, game.World.GetMaterial(cell));
        Assert.Equal(0, game.World.GetTerrainHp(cell));
        Assert.Equal(1, game.Inventory.GetCount(material));
        Assert.Equal(0, game.Economy.Currency);
        Assert.False(game.Dig(cell));
        Assert.Equal(1, game.Inventory.GetCount(material));
        Assert.True(game.SellBlock(material));
        Assert.Equal(0, game.Inventory.GetCount(material));
        Assert.Equal(value, game.Economy.Currency);
    }

    [Theory]
    [InlineData(Material.Wood, 250, 100)]
    [InlineData(Material.Stone, 500, 200)]
    [InlineData(Material.Copper, 1000, 300)]
    [InlineData(Material.Iron, 2000, 500)]
    [InlineData(Material.Gold, 3000, 800)]
    [InlineData(Material.Diamond, 5000, 1300)]
    [InlineData(Material.Osmium, 8000, 2100)]
    public void RecoveredBlocksCanBeReusedAsFortificationsSavedAndRemovedWithoutLosingTheirMaterial(Material material, int hp, int value)
    {
        var source = new Cell(8, 16, 8);
        var destination = new Cell(12, 16, 12);
        var game = WithRecoveredTerrain(material, source);
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        Assert.True(game.Dig(source));
        Assert.Equal(1, game.Inventory.GetCount(material));

        Assert.True(game.BuildBarrier(destination, material), game.Message);

        var block = Assert.Single(game.World.Barriers);
        Assert.Equal(material, block.Content);
        Assert.Equal(hp, block.Hp);
        Assert.Equal(0, game.Inventory.GetCount(material));
        Assert.Equal(0, game.Economy.Currency);
        block.Hp -= 7;
        var path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-reused-block-{Guid.NewGuid():N}.json");
        try
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            var savedBlock = Assert.Single(restored!.World.Barriers);
            Assert.Equal(destination, savedBlock.Cell);
            Assert.Equal(material, savedBlock.Content);
            Assert.Equal(hp - 7, savedBlock.Hp);
            Assert.Equal(0, restored.Inventory.GetCount(material));

            Assert.True(restored.Remove(destination));
            Assert.Empty(restored.World.Barriers);
            Assert.Equal(1, restored.Inventory.GetCount(material));
            Assert.Equal(0, restored.Economy.Currency);
            Assert.True(restored.SellBlock(material));
            Assert.Equal(0, restored.Inventory.GetCount(material));
            Assert.Equal(value, restored.Economy.Currency);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static GameSession WithRecoveredTerrain(Material material, Cell cell)
    {
        var state = new GameSession().ExportState();
        state.TerrainPlacements!.Add(new SavedTerrainPlacement { Cell = cell, Material = material });
        var path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-block-sales-{Guid.NewGuid():N}.json");
        try
        {
            var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true, Converters = { new JsonStringEnumConverter() } };
            File.WriteAllText(path, JsonSerializer.Serialize(state, options));
            Assert.True(SaveStore.TryLoad(path, out var game, out var error), error);
            return game!;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
