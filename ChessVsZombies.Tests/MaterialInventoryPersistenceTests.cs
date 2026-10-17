using System.Text.Json.Nodes;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MaterialInventoryPersistenceTests
{
    [Fact]
    public void AllTerrainInventoryCountsAndToolsRoundTripWithoutSharingExportedState()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            var materials = Enum.GetValues<Material>().Where(Balance.IsTerrainMaterial).ToArray();
            foreach (var material in materials) Assert.True(game.Inventory.Add(material, (int)material + 1));
            game.Economy.Credit(100);
            Assert.True(game.BuyTool(ToolKind.Shovel));
            Assert.True(game.BuyTool(ToolKind.Pickaxe));
            var exported = game.Inventory.ExportState();
            exported.AdditionalMaterials![Material.Osmium] = int.MaxValue;
            Assert.Equal((int)Material.Osmium + 1, game.Inventory.GetCount(Material.Osmium));

            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.True(restored.Inventory.HasShovel);
            Assert.True(restored.Inventory.HasPickaxe);
            foreach (var material in materials)
                Assert.Equal(game.Inventory.GetCount(material), restored.Inventory.GetCount(material));
            Assert.Equal(game.Inventory.DirtCount, restored.Inventory.DirtCount);
            Assert.Equal(game.Inventory.StoneCount, restored.Inventory.StoneCount);

            restored.Reset();
            Assert.False(restored.Inventory.HasShovel);
            Assert.False(restored.Inventory.HasPickaxe);
            foreach (var material in materials) Assert.Equal(0, restored.Inventory.GetCount(material));
            Assert.Empty(restored.PendingBlockDrops);
        });
    }

    [Fact]
    public void VersionSevenMigrationPreservesTemplatesBlocksToolsAndCurrentObsidianHp()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["Version"] = 7;
            document["Inventory"]!.AsObject().Remove("AdditionalMaterials");
            document.AsObject().Remove("PendingBlockDrops");
            document["Inventory"]!["DirtCount"] = 7;
            document["Inventory"]!["StoneCount"] = 9;
            document["Inventory"]!["HasPickaxe"] = true;
            document["UpgradeTemplate"]!["Points"]!["ATK"] = 4;
            document["TerrainPlacements"] = JsonNode.Parse("""
                [{"Cell":{"X":9,"Y":16,"Z":8},"Material":"Stone"}]
                """);
            document["DamagedTerrain"] = JsonNode.Parse("""
                [{"Cell":{"X":9,"Y":16,"Z":8},"Hp":490}]
                """);
            document["Liquids"]!["Cells"] = JsonNode.Parse("""
                [{"Cell":{"X":8,"Y":16,"Z":8},"Water":0,"Mercury":0,"Lava":0,"ObsidianUnits":2,"CrustFace":"West"}]
                """);
            document["Liquids"]!["Obsidian"] = JsonNode.Parse("""
                [{"Cell":{"X":8,"Y":16,"Z":8},"Units":2,"Face":"West","Hp":7.5}]
                """);
            var legacy = document.ToJsonString();
            File.WriteAllText(path, legacy);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Equal(legacy, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".v7.bak"));
            Assert.Equal(7, restored.Inventory.DirtCount);
            Assert.Equal(9, restored.Inventory.StoneCount);
            Assert.True(restored.Inventory.HasPickaxe);
            Assert.Equal(4, restored.UpgradeTemplate.Points[UpgradeKind.ATK]);
            Assert.Equal(490, restored.World.GetTerrainHp(new Cell(9, 16, 8)));
            Assert.Equal(7.5f, Assert.Single(restored.World.ObsidianCells).Hp);
            Assert.All(restored.Inventory.ExportState().AdditionalMaterials!, pair => Assert.Equal(0, pair.Value));
            Assert.Empty(restored.PendingBlockDrops);

            SaveStore.Save(restored, path);
            Assert.Equal(legacy, File.ReadAllText(path + ".v7.bak"));
            Assert.Equal(9, JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>());
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.Equal(7.5f, Assert.Single(reloaded!.World.ObsidianCells).Hp);
            Assert.Equal(4, reloaded.UpgradeTemplate.Points[UpgradeKind.ATK]);
        });
    }

    [Fact]
    public void PendingDeathDropsRoundTripAsIndependentBlocksWithoutSharingExportedLists()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["PendingBlockDrops"] = JsonNode.Parse("""
                [
                  {"Cell":{"X":0,"Y":0,"Z":0},"Content":"Wood"},
                  {"Cell":{"X":0,"Y":0,"Z":0},"Content":"Wood"},
                  {"Cell":{"X":31,"Y":31,"Z":31},"Content":"Osmium"}
                ]
                """);
            File.WriteAllText(path, document.ToJsonString());
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Equal(3, restored.PendingBlockDrops.Count);
            var expected = restored.PendingBlockDrops.ToArray();
            restored.ExportState().PendingBlockDrops!.Clear();
            Assert.Equal(expected, restored.PendingBlockDrops);

            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.Equal(expected, reloaded!.PendingBlockDrops);
            Assert.Equal(0, reloaded.Inventory.GetCount(Material.Wood));
            Assert.Equal(0, reloaded.Inventory.GetCount(Material.Osmium));
        });
    }

    [Theory]
    [InlineData("missing inventory collection")]
    [InlineData("null inventory collection")]
    [InlineData("missing inventory kind")]
    [InlineData("unknown inventory kind")]
    [InlineData("repeated base material")]
    [InlineData("negative count")]
    [InlineData("fractional count")]
    [InlineData("legacy inventory extension")]
    [InlineData("missing drops")]
    [InlineData("null drops")]
    [InlineData("null drop")]
    [InlineData("drop outside grid")]
    [InlineData("drop nonterrain material")]
    [InlineData("drop missing material")]
    [InlineData("drop missing cell")]
    [InlineData("legacy drops")]
    public void MalformedMaterialInventoryOrPendingDropsLeaveCheckpointUntouched(string defect)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["PendingBlockDrops"] = JsonNode.Parse("""
                [{"Cell":{"X":8,"Y":16,"Z":8},"Content":"Copper"}]
                """);
            var inventory = document["Inventory"]!;
            var counts = inventory["AdditionalMaterials"]!;
            var drop = document["PendingBlockDrops"]![0]!;
            switch (defect)
            {
                case "missing inventory collection": inventory.AsObject().Remove("AdditionalMaterials"); break;
                case "null inventory collection": inventory["AdditionalMaterials"] = null; break;
                case "missing inventory kind": counts.AsObject().Remove("Wood"); break;
                case "unknown inventory kind": counts["Unknown"] = 1; break;
                case "repeated base material": counts["Dirt"] = 1; break;
                case "negative count": counts["Osmium"] = -1; break;
                case "fractional count": counts["Wood"] = .5; break;
                case "legacy inventory extension": document["Version"] = 7; document.AsObject().Remove("PendingBlockDrops"); break;
                case "missing drops": document.AsObject().Remove("PendingBlockDrops"); break;
                case "null drops": document["PendingBlockDrops"] = null; break;
                case "null drop": document["PendingBlockDrops"]![0] = null; break;
                case "drop outside grid": drop["Cell"]!["X"] = 32; break;
                case "drop nonterrain material": drop["Content"] = "Core"; break;
                case "drop missing material": drop.AsObject().Remove("Content"); break;
                case "drop missing cell": drop.AsObject().Remove("Cell"); break;
                case "legacy drops": document["Version"] = 7; inventory.AsObject().Remove("AdditionalMaterials"); break;
            }
            var invalid = document.ToJsonString();
            File.WriteAllText(path, invalid);
            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    private static void WithSavePath(Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "ChessVsZombiesMaterials-" + Guid.NewGuid().ToString("N") + ".json");
        try { test(path); }
        finally
        {
            File.Delete(path);
            for (var version = 1; version <= 7; version++) File.Delete(path + $".v{version}.bak");
        }
    }
}
