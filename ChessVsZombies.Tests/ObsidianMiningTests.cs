using System.Text.Json.Nodes;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ObsidianMiningTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(10)]
    public void PickaxeRecoversEveryLayerOfADamagedCrustWithoutShatterOrLiquidLoss(int layers)
    {
        var cell = new Cell(8, 16, 8);
        var game = WithCrust(cell, layers);
        var bystander = new Zombie { Id = 1, WaveIndex = -1, Position = new Cell(8, 16, 9).Center };
        game.Zombies.Add(bystander);
        Assert.False(game.Dig(cell));
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.False(game.Dig(cell));
        Assert.Equal(layers, game.World.GetObsidian(cell)!.Units);
        game.Economy.Credit(100);
        Assert.True(game.BuyTool(ToolKind.Pickaxe));

        Assert.True(game.Dig(cell), game.Message);

        Assert.Null(game.World.GetObsidian(cell));
        Assert.Equal(Material.Air, game.World.GetMaterial(cell));
        Assert.Equal(layers, game.Inventory.ObsidianLayers);
        Assert.Equal(layers, game.Inventory.GetCount(Material.Obsidian));
        Assert.Equal(10 - layers, game.Liquids.GetUnits(cell, LiquidKind.Water));
        Assert.Equal(10, bystander.BodyHp);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Equal(0, game.Inventory.StoneCount);
        Assert.False(game.Dig(cell));
        Assert.Equal(layers, game.Inventory.ObsidianLayers);
    }

    [Fact]
    public void MiningAnInternalCrustPreservesLiquidsOnBothSides()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        game.Liquids.RestoreState(new LiquidState
        {
            Cells = [new(cell, 5, 3, 0, 2, CrustFace.Bottom)],
            Obsidian = [new(cell, 2, CrustFace.Bottom, 7, OffsetUnits: 3)]
        });
        Assert.True(game.BuyTool(ToolKind.Pickaxe));

        Assert.True(game.Dig(cell));

        Assert.Equal(2, game.Inventory.ObsidianLayers);
        Assert.Equal(5, game.Liquids.GetUnits(cell, LiquidKind.Water));
        Assert.Equal(3, game.Liquids.GetUnits(cell, LiquidKind.Mercury));
        Assert.Null(game.World.GetObsidian(cell));
    }

    [Fact]
    public void MiningInventoryOverflowLeavesTheWholeCrustAndItsLiquidsIntact()
    {
        var cell = new Cell(8, 16, 8);
        var game = WithCrust(cell, 2);
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        Assert.True(game.Inventory.Add(Material.Obsidian, int.MaxValue - 1));
        var before = game.World.GetObsidian(cell);

        Assert.False(game.Dig(cell));

        Assert.Equal(before, game.World.GetObsidian(cell));
        Assert.Equal(8, game.Liquids.GetUnits(cell, LiquidKind.Water));
        Assert.Equal(int.MaxValue - 1, game.Inventory.ObsidianLayers);
        Assert.Equal(0, game.Economy.Currency);
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(9, false, 9)]
    [InlineData(10, true, 0)]
    [InlineData(13, true, 3)]
    [InlineData(20, true, 10)]
    public void SellingConsumesExactlyTenLayersForTwoHundredFiftyCurrency(int layers, bool success, int remaining)
    {
        var game = new GameSession();
        Assert.Equal(250, Balance.BlockSellValue(Material.Obsidian));
        Assert.True(game.Inventory.Add(Material.Obsidian, layers));

        Assert.Equal(success, game.SellBlock(Material.Obsidian));

        Assert.Equal(remaining, game.Inventory.ObsidianLayers);
        Assert.Equal(1000 + (success ? 250 : 0), game.Economy.Currency);
    }

    [Fact]
    public void OverflowingSalesAndCombatOperationsDoNotConsumeLayers()
    {
        var cell = new Cell(8, 16, 8);
        var game = WithCrust(cell, 2);
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        Assert.True(game.Inventory.Add(Material.Obsidian, 20));
        game.Economy.Credit(int.MaxValue - 250);
        Assert.True(game.SellBlock(Material.Obsidian));
        Assert.Equal(int.MaxValue, game.Economy.Currency);
        Assert.False(game.SellBlock(Material.Obsidian));
        Assert.Equal(10, game.Inventory.ObsidianLayers);
        Assert.True(game.StartLevel());

        Assert.False(game.SellBlock(Material.Obsidian));
        Assert.False(game.Dig(cell));
        Assert.False(game.BuildObsidian(new Cell(9, 16, 8), CrustFace.Bottom));

        Assert.Equal(10, game.Inventory.ObsidianLayers);
        Assert.Equal(2, game.World.GetObsidian(cell)!.Units);
        Assert.Equal(int.MaxValue, game.Economy.Currency);
    }

    [Fact]
    public void LayerCountsAndPlacedOrientationRoundTripAndOlderSavesDefaultToZero()
    {
        WithPath(path =>
        {
            var game = new GameSession();
            var cell = new Cell(8, 16, 8);
            AddSupport(game, cell, CrustFace.East);
            Assert.True(game.Inventory.Add(Material.Obsidian, 15));
            Assert.True(game.BuildObsidian(cell, CrustFace.East));
            Assert.True(game.BuildObsidian(cell, CrustFace.Top));
            SaveStore.Save(game, path);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.Equal(13, restored!.Inventory.ObsidianLayers);
            Assert.Equal(CrustFace.East, restored.World.GetObsidian(cell)!.Face);
            Assert.Equal(2, restored.World.GetObsidian(cell)!.Units);
            Assert.True(restored.SellBlock(Material.Obsidian));
            Assert.Equal(3, restored.Inventory.ObsidianLayers);
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.Equal(3, reloaded!.Inventory.ObsidianLayers);
            Assert.Equal(1250, reloaded.Economy.Currency);

            var legacy = JsonNode.Parse(File.ReadAllText(path))!;
            legacy["Inventory"]!.AsObject().Remove("ObsidianLayers");
            File.WriteAllText(path, legacy.ToJsonString());
            Assert.True(SaveStore.TryLoad(path, out var older, out error), error);
            Assert.Equal(0, older!.Inventory.ObsidianLayers);
            Assert.Equal(CrustFace.East, older.World.GetObsidian(cell)!.Face);
        });
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("fractional")]
    [InlineData("overflow")]
    [InlineData("null")]
    public void InvalidLayerCountsPreserveTheOriginalCheckpoint(string defect)
    {
        WithPath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["Inventory"]!["ObsidianLayers"] = defect switch
            {
                "negative" => JsonValue.Create(-1), "fractional" => JsonValue.Create(.5),
                "overflow" => JsonValue.Create((long)int.MaxValue + 1), _ => null
            };
            var invalid = document.ToJsonString();
            File.WriteAllText(path, invalid);
            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    [Fact]
    public void LayerInventoryGuardsNegativeAndOverflowingOperationsAndResets()
    {
        var inventory = new BlockInventory();
        Assert.False(inventory.Add(Material.Obsidian, -1));
        Assert.True(inventory.Add(Material.Obsidian, int.MaxValue));
        Assert.False(inventory.Add(Material.Obsidian));
        Assert.False(inventory.TrySpend(Material.Obsidian, -1));
        Assert.Equal(int.MaxValue, inventory.ObsidianLayers);
        Assert.True(inventory.TrySpend(Material.Obsidian, int.MaxValue));
        Assert.False(inventory.TrySpend(Material.Obsidian));
        Assert.True(inventory.Add(Material.Obsidian, 3));
        inventory.Reset();
        Assert.Equal(0, inventory.ObsidianLayers);
    }

    private static GameSession WithCrust(Cell cell, int layers)
    {
        var game = new GameSession();
        game.Liquids.RestoreState(new LiquidState
        {
            Cells = [new(cell, 10 - layers, 0, 0, layers, CrustFace.West)],
            Obsidian = [new(cell, layers, CrustFace.West, layers * LiquidSystem.ObsidianHpPerUnit - 2)]
        });
        return game;
    }

    private static void AddSupport(GameSession game, Cell cell, CrustFace face)
    {
        if (face == CrustFace.Bottom) return;
        Assert.True(game.Inventory.Add(Material.Dirt, 3));
        if (face == CrustFace.Top)
        {
            Assert.True(game.BuildBarrier(cell + new Cell(1, 0, 0)));
            Assert.True(game.BuildBarrier(cell + new Cell(1, 1, 0)));
            Assert.True(game.BuildBarrier(cell + new Cell(0, 1, 0)));
            return;
        }
        var offset = face switch
        {
            CrustFace.West => new Cell(-1, 0, 0), CrustFace.East => new Cell(1, 0, 0),
            CrustFace.North => new Cell(0, 0, -1), _ => new Cell(0, 0, 1)
        };
        Assert.True(game.BuildBarrier(cell + offset));
    }

    private static void WithPath(Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-obsidian-mining-{Guid.NewGuid():N}.json");
        try { test(path); }
        finally { File.Delete(path); }
    }
}
