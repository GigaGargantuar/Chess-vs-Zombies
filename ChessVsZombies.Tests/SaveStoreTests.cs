using ChessVsZombies.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace ChessVsZombies.Tests;

public class SaveStoreTests
{
    [Fact]
    public void CheckpointRestoresCampaignEconomyDefensesTerrainAndTheNextGachaResult()
    {
        WithSavePath(path =>
        {
            var catalog = new LevelCatalog();
            catalog.Set(new LevelDefinition
            {
                Number = 1,
                Name = "Checkpoint encounter",
                Waves = [new() { Groups = [new() { Count = 1 }] }]
            });
            var original = new GameSession(812, catalog);
            original.Economy.Credit(2000);
            Assert.True(original.BuyTool(ToolKind.Shovel));
            Assert.True(original.BuyTool(ToolKind.Pickaxe));
            Assert.True(original.Dig(new Cell(8, 15, 8)));
            Assert.True(original.Dig(new Cell(8, 11, 8)));
            Assert.True(original.BuildBarrier(new Cell(12, 16, 12)));
            Assert.True(original.Deploy(PieceKind.Knight, new Cell(11, 16, 12)));
            var knight = Assert.Single(original.Turrets);
            foreach (var kind in new[] { UpgradeKind.HP, UpgradeKind.Jump })
            {
                original.Economy.GetProgress(kind).PullCount = kind == UpgradeKind.Jump ? 21 : 11;
                original.Economy.GetProgress(kind).Available = kind == UpgradeKind.Jump ? 3 : 2;
                Assert.True(original.Economy.Apply(knight, kind));
            }
            Assert.True(original.Economy.TrySetJumpPattern(knight, [new Cell(3, 1, 0), new Cell(-2, 0, 1)], out _));
            knight.Hp -= 7;
            Assert.Single(original.World.Barriers).Hp -= 6;
            original.Economy.Pull(original.Turrets);
            Assert.True(original.StartLevel());
            original.Update(.01f);
            var zombie = Assert.Single(original.Zombies);
            zombie.Position = new Cell(14, 16, 15).Center;
            zombie.NextCell = null;
            original.Update(.01f);
            original.DamageZombie(zombie, 1000);
            original.Update(.01f);
            Assert.Equal(2, original.LevelNumber);
            Assert.Equal(199, original.CoreHp);

            SaveStore.Save(original, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error, catalog), error);
            Assert.NotNull(restored);
            Assert.Equal(GamePhase.Building, restored.Phase);
            Assert.Equal(original.LevelNumber, restored.LevelNumber);
            Assert.Equal(original.CoreHp, restored.CoreHp);
            Assert.Equal(original.Economy.Currency, restored.Economy.Currency);
            Assert.Equal(Material.Air, restored.World.GetMaterial(new Cell(8, 15, 8)));
            Assert.Equal(94, Assert.Single(restored.World.Barriers).Hp);
            Assert.Equal(Material.Dirt, Assert.Single(restored.World.Barriers).Content);
            Assert.True(restored.Inventory.HasShovel);
            Assert.True(restored.Inventory.HasPickaxe);
            Assert.Equal(0, restored.Inventory.GetCount(Material.Dirt));
            Assert.Equal(1, restored.Inventory.GetCount(Material.Stone));
            var restoredKnight = Assert.Single(restored.Turrets);
            Assert.Equal(knight.Id, restoredKnight.Id);
            Assert.Equal(knight.Cell, restoredKnight.Cell);
            Assert.Equal(knight.Hp, restoredKnight.Hp);
            Assert.Equal(knight.UpgradePoints.OrderBy(pair => pair.Key), restoredKnight.UpgradePoints.OrderBy(pair => pair.Key));
            Assert.Equal(knight.JumpPattern, restoredKnight.JumpPattern);
            foreach (var kind in Enum.GetValues<UpgradeKind>())
            {
                Assert.Equal(original.Economy.GetProgress(kind).PullCount, restored.Economy.GetProgress(kind).PullCount);
                Assert.Equal(original.Economy.GetProgress(kind).Available, restored.Economy.GetProgress(kind).Available);
            }
            Assert.Equal(original.Economy.Pull(original.Turrets), restored.Economy.Pull(restored.Turrets));
            Assert.True(restored.Deploy(PieceKind.Pawn, new Cell(3, 16, 3)));
            Assert.Equal(2, restored.Turrets.Select(turret => turret.Id).Distinct().Count());
        });
    }

    [Fact]
    public void TwoPointHighCapStatsRoundTripAndWaitForATwoPointTopUp()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
            var tower = Assert.Single(game.Turrets);
            tower.Hp -= 13;
            foreach (var kind in new[] { UpgradeKind.HP, UpgradeKind.ATK })
            {
                var progress = game.Economy.GetProgress(kind);
                progress.PullCount = progress.Available = 291;
                Assert.True(game.Economy.Apply(tower, kind));
                Assert.Equal(30, game.Economy.GetAllocated(tower, kind));
                Assert.Equal(289, progress.Available);
            }

            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            var savedTower = Assert.Single(restored!.Turrets);
            Assert.Equal(330, restored.Economy.GetMaxHp(savedTower));
            Assert.Equal(317, savedTower.Hp);
            Assert.Equal(31, restored.Economy.GetAttack(savedTower));
            var hp = restored.Economy.GetProgress(UpgradeKind.HP);
            hp.PullCount += 10;
            hp.Available += 10;
            Assert.Equal(330, restored.Economy.GetMaxHp(savedTower));
            Assert.Equal(317, savedTower.Hp);
            Assert.True(restored.Economy.Apply(savedTower, UpgradeKind.HP));
            Assert.Equal(340, restored.Economy.GetMaxHp(savedTower));
            Assert.Equal(327, savedTower.Hp);
            Assert.Equal(297, hp.Available);
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var toppedUp, out error), error);
            Assert.Equal(31, toppedUp!.Economy.GetAllocated(Assert.Single(toppedUp.Turrets), UpgradeKind.HP));
        });
    }

    [Theory]
    [InlineData(8, 5, 2)]
    [InlineData(9, 5, 1)]
    [InlineData(9, 1, 0)]
    public void SavedAllocationsRequireEnoughPreviouslyConsumedComponents(int version, int allocation, int spent)
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["Version"] = version;
            document["Turrets"]![0]!["UpgradePoints"]!["ATK"] = allocation;
            SetLegacyProgress(document, "ATK", 41, 41 - spent);
            AssertRejectedWithoutRewrite(path, document);
        });
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    public void VersionEightQueuedTargetsKeepTheirStatsAndGraceWithDiscountedPrices(int existing, int newCost)
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["Version"] = 8;
            if (existing > 0) document["Turrets"]![0]!["UpgradePoints"]!["ATK"] = existing;
            SetLegacyProgress(document, "ATK", 41, 41 - existing);
            document["QueuedUpgradeBatch"] = new JsonObject
            {
                ["Targets"] = new JsonArray(new JsonObject
                {
                    ["TurretId"] = Assert.Single(game.Turrets).Id,
                    ["Expected"] = new JsonObject { ["ATK"] = existing },
                    ["Additions"] = new JsonObject { ["ATK"] = 5 - existing }
                }),
                ["Costs"] = new JsonObject { ["ATK"] = 5 - existing },
                ["GraceRemaining"] = 2.5f
            };
            string legacy = document.ToJsonString();
            File.WriteAllText(path, legacy);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.Equal(existing, restored!.Economy.GetAllocated(Assert.Single(restored.Turrets), UpgradeKind.ATK));
            Assert.Equal(41 - existing, restored.Economy.GetProgress(UpgradeKind.ATK).Available);
            Assert.Equal(newCost, restored.QueuedUpgradeBatch!.Costs[UpgradeKind.ATK]);
            Assert.Equal(5 - existing, Assert.Single(restored.QueuedUpgradeBatch.Targets).Additions[UpgradeKind.ATK]);
            Assert.Equal(2.5f, restored.QueuedUpgradeBatch.GraceRemaining);
            Assert.Equal(legacy, File.ReadAllText(path));
            SaveStore.Save(restored, path);
            Assert.Equal(legacy, File.ReadAllText(path + ".v8.bak"));
            Assert.Equal(9, JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>());
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            reloaded!.Economy.GetProgress(UpgradeKind.ATK).PullCount += 10;
            reloaded.Economy.GetProgress(UpgradeKind.ATK).Available += 10;
            reloaded.Update(2.6f);
            Assert.Null(reloaded.QueuedUpgradeBatch);
            Assert.Equal(5, reloaded.Economy.GetAllocated(Assert.Single(reloaded.Turrets), UpgradeKind.ATK));
            Assert.Equal(51 - existing - newCost, reloaded.Economy.GetProgress(UpgradeKind.ATK).Available);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void VersionEightQueueIsCancelledWithoutChargeWhenAtomicPricingWouldIncreaseItsQuote(int existing)
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["Version"] = 8;
            if (existing > 0) document["Turrets"]![0]!["UpgradePoints"]!["ATK"] = existing;
            SetLegacyProgress(document, "ATK", existing > 0 ? 11 : 2, 1);
            document["QueuedUpgradeBatch"] = new JsonObject
            {
                ["Targets"] = new JsonArray(new JsonObject
                {
                    ["TurretId"] = Assert.Single(game.Turrets).Id,
                    ["Expected"] = new JsonObject { ["ATK"] = existing },
                    ["Additions"] = new JsonObject { ["ATK"] = 1 }
                }),
                ["Costs"] = new JsonObject { ["ATK"] = 1 },
                ["GraceRemaining"] = 2.5f
            };
            string legacy = document.ToJsonString();
            File.WriteAllText(path, legacy);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.Null(restored!.QueuedUpgradeBatch);
            Assert.Equal(1, restored.Economy.GetProgress(UpgradeKind.ATK).Available);
            Assert.Equal(existing, restored.Economy.GetAllocated(Assert.Single(restored.Turrets), UpgradeKind.ATK));
            Assert.Contains("cancelled without charge", restored.MigrationNotice);
            Assert.Equal(legacy, File.ReadAllText(path));
            restored.Update(3.1f);
            Assert.Equal(1, restored.Economy.GetProgress(UpgradeKind.ATK).Available);
            Assert.Equal(existing, restored.Economy.GetAllocated(Assert.Single(restored.Turrets), UpgradeKind.ATK));
        });
    }

    [Fact]
    public void CombatCannotOverwriteTheLastBuildCheckpoint()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            SaveStore.Save(game, path);
            string checkpoint = File.ReadAllText(path);
            Assert.True(game.StartLevel());
            Assert.False(SaveStore.TrySave(game, path, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(checkpoint, File.ReadAllText(path));
        });
    }

    [Fact]
    public void CorruptCheckpointIsReportedAndLeftIntact()
    {
        WithSavePath(path =>
        {
            const string broken = "{ broken checkpoint";
            File.WriteAllText(path, broken);
            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(broken, File.ReadAllText(path));
        });
    }

    [Fact]
    public void InvalidSavedStateIsReportedWithoutThrowingOrOverwritingIt()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            document["LevelNumber"] = -1;
            string invalid = document.ToJsonString();
            File.WriteAllText(path, invalid);
            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    [Fact]
    public void VersionFiveCheckpointRejectsATurretInsideTheReservedBlockVolume()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 16, 12)));
            SaveStore.Save(game, path);
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            SetCheckpointVersion(document, 5);
            document.AsObject().Remove("TerrainPlacements");
            document["Turrets"]![0]!["Cell"]!["X"] = 12;
            string invalid = document.ToJsonString();
            File.WriteAllText(path, invalid);
            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    [Fact]
    public void LegacyMigrationConvertsConsumedComponentsAndRefundsNewlyInvalidDefensesWithoutFabricatingPoints()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            SetCheckpointVersion(document, 1);
            RemoveLiquidFields(document);
            document.AsObject().Remove("Inventory");
            document.AsObject().Remove("DamagedTerrain");
            document["Economy"]!["Currency"] = 900;
            SetLegacyProgress(document, "HP", 21, 19);
            SetLegacyProgress(document, "ATK", 11, 10);
            SetLegacyProgress(document, "Orthogonal", 1, 0);
            SetLegacyProgress(document, "Jump", 21, 18);
            document["Turrets"] = System.Text.Json.Nodes.JsonNode.Parse("""
                [
                  { "Id": 1, "Kind": "Pawn", "Cell": { "X":12,"Y":16,"Z":12 }, "Hp":60,
                    "Upgrades":["HP","Jump"], "JumpPattern":[{"X":2,"Y":0,"Z":0}] },
                  { "Id": 2, "Kind": "Rook", "Cell": { "X":11,"Y":16,"Z":12 }, "Hp":53,
                    "Upgrades":["HP","ATK","Orthogonal"], "JumpPattern":[{"X":2,"Y":0,"Z":0}] },
                  { "Id": 3, "Kind": "Pawn", "Cell": { "X":10,"Y":16,"Z":12 }, "Hp":30,
                    "Upgrades":["Jump"], "JumpPattern":[{"X":4,"Y":0,"Z":0},{"X":2,"Y":0,"Z":0}] },
                  { "Id": 4, "Kind": "Knight", "Cell": { "X":9,"Y":16,"Z":12 }, "Hp":30,
                    "Upgrades":["Jump"], "JumpPattern":[{"X":4,"Y":0,"Z":0}] }
                ]
                """);
            string legacy = document.ToJsonString();
            File.WriteAllText(path, legacy);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.False(string.IsNullOrWhiteSpace(restored.MigrationNotice));
            Assert.Equal(legacy, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".v1.bak"));
            Assert.Equal(901, restored.Economy.Currency);
            Assert.Equal(20, restored.Economy.GetProgress(UpgradeKind.HP).Available);
            Assert.Equal(19, restored.Economy.GetProgress(UpgradeKind.Jump).Available);
            Assert.Equal(1, restored.Economy.GetProgress(UpgradeKind.Orthogonal).Available);
            Assert.Equal(3, restored.Turrets.Count);
            Assert.DoesNotContain(restored.Turrets, turret => WorldGrid.IsBuildZone(turret.Cell));
            Assert.Equal(4, restored.Turrets.Sum(turret => turret.UpgradePoints.Values.Sum()));
            var rook = Assert.Single(restored.Turrets, turret => turret.Kind == PieceKind.Rook);
            Assert.Equal(33, rook.Hp);
            Assert.Equal(40, restored.Economy.GetMaxHp(rook));
            Assert.Equal(2, restored.Economy.GetAttack(rook));
            Assert.Equal(int.MaxValue, restored.Economy.GetRange(rook, UpgradeKind.Orthogonal));
            var pawn = Assert.Single(restored.Turrets, turret => turret.Kind == PieceKind.Pawn);
            Assert.Equal(new Cell(2, 0, 0), Assert.Single(pawn.JumpPattern));
            var knight = Assert.Single(restored.Turrets, turret => turret.Kind == PieceKind.Knight);
            Assert.Equal(8, knight.JumpPattern.Count);
            Assert.Equal(3, restored.Economy.GetRange(knight, UpgradeKind.Jump));

            SaveStore.Save(restored, path);
            Assert.Equal(legacy, File.ReadAllText(path + ".v1.bak"));
            var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(9, saved["Version"]!.GetValue<int>());
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.NotNull(reloaded);
            Assert.Equal(901, reloaded.Economy.Currency);
            Assert.Equal(3, reloaded.Turrets.Count);
            Assert.Equal(legacy, File.ReadAllText(path + ".v1.bak"));
        });
    }

    [Fact]
    public void InvalidLegacyDataIsRejectedBeforeMigrationCanRemoveTheOffendingTower()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 16, 12)));
            SaveStore.Save(game, path);
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            SetCheckpointVersion(document, 1);
            RemoveLiquidFields(document);
            document.AsObject().Remove("Inventory");
            document.AsObject().Remove("DamagedTerrain");
            var turret = document["Turrets"]![0]!.AsObject();
            turret.Remove("UpgradePoints");
            turret["Upgrades"] = new System.Text.Json.Nodes.JsonArray();
            turret["Cell"]!["X"] = 12;
            turret["Hp"] = 1000;
            string invalid = document.ToJsonString();
            File.WriteAllText(path, invalid);
            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalid, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".v1.bak"));
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyExcavationsBecomeStoredMaterialsAndOldFortificationsPreserveDamage(int version)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            SetCheckpointVersion(document, version);
            RemoveLiquidFields(document);
            document.AsObject().Remove("Inventory");
            document.AsObject().Remove("DamagedTerrain");
            document["Barriers"] = System.Text.Json.Nodes.JsonNode.Parse("""
                [{"Cell":{"X":12,"Y":16,"Z":12},"Hp":25}]
                """);
            document["Excavated"] = System.Text.Json.Nodes.JsonNode.Parse("""
                [{"X":8,"Y":15,"Z":8},{"X":8,"Y":11,"Z":8}]
                """);
            string legacy = document.ToJsonString();
            File.WriteAllText(path, legacy);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.False(restored.Inventory.HasShovel);
            Assert.False(restored.Inventory.HasPickaxe);
            Assert.Equal(1, restored.Inventory.GetCount(Material.Dirt));
            Assert.Equal(1, restored.Inventory.GetCount(Material.Stone));
            var barrier = Assert.Single(restored.World.Barriers);
            Assert.Equal(Material.Dirt, barrier.Content);
            Assert.Equal(85, barrier.Hp);
            Assert.Equal(1000, restored.Economy.Currency);
            Assert.Equal(legacy, File.ReadAllText(path));
            string backupPath = path + $".v{version}.bak";
            Assert.False(File.Exists(backupPath));
            SaveStore.Save(restored, path);
            Assert.Equal(legacy, File.ReadAllText(backupPath));
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.NotNull(reloaded);
            Assert.Equal(1, reloaded.Inventory.GetCount(Material.Dirt));
            Assert.Equal(1, reloaded.Inventory.GetCount(Material.Stone));
            Assert.Equal(85, Assert.Single(reloaded.World.Barriers).Hp);
        });
    }

    [Theory]
    [InlineData(Material.Dirt, 73)]
    [InlineData(Material.Stone, 473)]
    public void CheckpointPreservesFortificationMaterialAndCurrentDurability(Material material, int hp)
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            game.Inventory.Add(material, 2);
            Assert.True(game.BuildBarrier(new Cell(12, 16, 12), material));
            Assert.Single(game.World.Barriers).Hp -= 27;
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            var barrier = Assert.Single(restored.World.Barriers);
            Assert.Equal(material, barrier.Content);
            Assert.Equal(hp, barrier.Hp);
            Assert.Equal(1, restored.Inventory.GetCount(material));
        });
    }

    [Fact]
    public void CheckpointPreservesDamagedAndDestroyedNaturalTerrainWithoutGrantingInventory()
    {
        WithSavePath(path =>
        {
            var dirt = new Cell(8, 15, 8);
            var stone = new Cell(8, 11, 8);
            var destroyedDirt = new Cell(9, 15, 8);
            var destroyedStone = new Cell(9, 11, 8);
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["DamagedTerrain"] = new JsonArray(TerrainDamage(dirt, 73), TerrainDamage(stone, 473));
            document["Excavated"] = new JsonArray(SavedCell(destroyedDirt), SavedCell(destroyedStone));
            File.WriteAllText(path, document.ToJsonString());

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Equal(Material.Dirt, restored.World.GetMaterial(dirt));
            Assert.Equal(Material.Stone, restored.World.GetMaterial(stone));
            Assert.Equal(73, restored.World.GetTerrainHp(dirt));
            Assert.Equal(473, restored.World.GetTerrainHp(stone));
            Assert.Equal(2, restored.World.DamagedTerrain.Count());
            Assert.Equal(Material.Air, restored.World.GetMaterial(destroyedDirt));
            Assert.Equal(Material.Air, restored.World.GetMaterial(destroyedStone));
            Assert.Equal(0, restored.World.GetTerrainHp(destroyedDirt));
            Assert.Equal(0, restored.World.GetTerrainHp(destroyedStone));
            Assert.Equal(0, restored.Inventory.DirtCount);
            Assert.Equal(0, restored.Inventory.StoneCount);

            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.NotNull(reloaded);
            Assert.Equal(73, reloaded.World.GetTerrainHp(dirt));
            Assert.Equal(473, reloaded.World.GetTerrainHp(stone));
            Assert.Equal(Material.Air, reloaded.World.GetMaterial(destroyedDirt));
            Assert.Equal(Material.Air, reloaded.World.GetMaterial(destroyedStone));
            Assert.Equal(0, reloaded.Inventory.DirtCount);
            Assert.Equal(0, reloaded.Inventory.StoneCount);

            Assert.True(reloaded.BuyTool(ToolKind.Shovel));
            Assert.True(reloaded.Dig(dirt));
            Assert.Equal(1, reloaded.Inventory.DirtCount);
            Assert.Equal(stone, Assert.Single(reloaded.World.DamagedTerrain).Key);
            SaveStore.Save(reloaded, path);
            Assert.True(SaveStore.TryLoad(path, out var excavated, out error), error);
            Assert.NotNull(excavated);
            Assert.Equal(0, excavated.World.GetTerrainHp(dirt));
            Assert.Equal(Material.Air, excavated.World.GetMaterial(dirt));
            Assert.Equal(stone, Assert.Single(excavated.World.DamagedTerrain).Key);
            Assert.Equal(1, excavated.Inventory.DirtCount);
            Assert.Equal(0, excavated.Inventory.StoneCount);
        });
    }

    [Fact]
    public void VersionThreeMigrationPreservesInventoryAndRemovedTerrainAndBacksUpOriginalOnSave()
    {
        WithSavePath(path =>
        {
            var dirt = new Cell(8, 15, 8);
            var stone = new Cell(8, 11, 8);
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            SetCheckpointVersion(document, 3);
            RemoveLiquidFields(document);
            document.AsObject().Remove("DamagedTerrain");
            document["Inventory"]!["DirtCount"] = 7;
            document["Inventory"]!["StoneCount"] = 3;
            document["Inventory"]!["HasShovel"] = true;
            document["Excavated"] = new JsonArray(SavedCell(dirt), SavedCell(stone));
            string legacy = document.ToJsonString();
            File.WriteAllText(path, legacy);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.False(string.IsNullOrWhiteSpace(restored.MigrationNotice));
            Assert.Equal(legacy, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".v3.bak"));
            Assert.Empty(restored.World.DamagedTerrain);
            Assert.Equal(100, restored.World.GetTerrainHp(new Cell(9, 15, 8)));
            Assert.Equal(500, restored.World.GetTerrainHp(new Cell(9, 11, 8)));
            Assert.Equal(Material.Air, restored.World.GetMaterial(dirt));
            Assert.Equal(Material.Air, restored.World.GetMaterial(stone));
            Assert.Equal(7, restored.Inventory.DirtCount);
            Assert.Equal(3, restored.Inventory.StoneCount);
            Assert.True(restored.Inventory.HasShovel);
            Assert.False(restored.Inventory.HasPickaxe);
            Assert.Equal(1000, restored.Economy.Currency);

            SaveStore.Save(restored, path);
            Assert.Equal(legacy, File.ReadAllText(path + ".v3.bak"));
            var upgraded = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(9, upgraded["Version"]!.GetValue<int>());
            Assert.Empty(upgraded["DamagedTerrain"]!.AsArray());
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.NotNull(reloaded);
            Assert.Equal(7, reloaded.Inventory.DirtCount);
            Assert.Equal(3, reloaded.Inventory.StoneCount);
            Assert.Equal(Material.Air, reloaded.World.GetMaterial(dirt));
            Assert.Equal(Material.Air, reloaded.World.GetMaterial(stone));
            Assert.Empty(reloaded.World.DamagedTerrain);
            Assert.Equal(legacy, File.ReadAllText(path + ".v3.bak"));
        });
    }

    [Fact]
    public void VersionFourMigrationAddsEmptyLiquidsWithoutChangingExistingMaterialsOrDamage()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            SetCheckpointVersion(document, 4);
            RemoveLiquidFields(document);
            document["Inventory"]!["DirtCount"] = 7;
            document["Inventory"]!["StoneCount"] = 3;
            document["Inventory"]!["HasShovel"] = true;
            document["Excavated"] = new JsonArray(SavedCell(new Cell(8, 15, 8)));
            document["DamagedTerrain"] = new JsonArray(TerrainDamage(new Cell(9, 15, 8), 73));
            string legacy = document.ToJsonString();
            File.WriteAllText(path, legacy);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Equal(legacy, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".v4.bak"));
            Assert.Equal(7, restored.Inventory.DirtCount);
            Assert.Equal(3, restored.Inventory.StoneCount);
            Assert.True(restored.Inventory.HasShovel);
            Assert.Equal(73, restored.World.GetTerrainHp(new Cell(9, 15, 8)));
            Assert.Equal(Material.Air, restored.World.GetMaterial(new Cell(8, 15, 8)));
            Assert.Equal(1000, restored.Economy.Currency);
            Assert.All(restored.LiquidInventory, pair => Assert.Equal(0, pair.Value));
            Assert.Equal(3, restored.LiquidInventory.Count);
            Assert.Empty(restored.Liquids.ExportState().Cells);
            Assert.Empty(restored.Liquids.ExportState().Obsidian);
            Assert.Empty(restored.Liquids.ExportState().Hazards);

            SaveStore.Save(restored, path);
            Assert.Equal(legacy, File.ReadAllText(path + ".v4.bak"));
            Assert.Equal(9, JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>());
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.NotNull(reloaded);
            Assert.Equal(7, reloaded.Inventory.DirtCount);
            Assert.Equal(3, reloaded.Inventory.StoneCount);
            Assert.Equal(73, reloaded.World.GetTerrainHp(new Cell(9, 15, 8)));
            Assert.All(reloaded.LiquidInventory, pair => Assert.Equal(0, pair.Value));
        });
    }

    [Fact]
    public void FallenNaturalTerrainPreservesItsNewMaterialAndRemainingHpAcrossCheckpoints()
    {
        WithSavePath(path =>
        {
            var game = SessionWithBothTools();
            var original = new Cell(8, 12, 8);
            var landing = new Cell(8, 11, 8);
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["DamagedTerrain"] = new JsonArray(TerrainDamage(original, 73));
            File.WriteAllText(path, document.ToJsonString());
            Assert.True(SaveStore.TryLoad(path, out var damaged, out var error), error);
            Assert.NotNull(damaged);
            game = damaged;

            foreach (var offset in new[] { new Cell(0, 1, 0), new Cell(1, 0, 0), new Cell(-1, 0, 0), new Cell(0, 0, 1), new Cell(0, 0, -1) })
                Assert.True(game.Dig(original + offset));
            Assert.True(game.Dig(landing));

            Assert.Equal(Material.Air, game.World.GetNaturalMaterial(original));
            Assert.Equal(Material.Stone, WorldGrid.InitialMaterial(landing));
            Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(landing));
            Assert.Equal(73, game.World.GetTerrainHp(landing));
            Assert.Equal(5, game.Inventory.DirtCount);
            Assert.Equal(1, game.Inventory.StoneCount);
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out error), error);
            Assert.NotNull(restored);
            Assert.Equal(Material.Air, restored.World.GetNaturalMaterial(original));
            Assert.Equal(Material.Dirt, restored.World.GetNaturalMaterial(landing));
            Assert.Equal(73, restored.World.GetTerrainHp(landing));
            Assert.Equal(5, restored.Inventory.DirtCount);
            Assert.Equal(1, restored.Inventory.StoneCount);
            Assert.Contains(restored.ExportState().Excavated, cell => cell == landing);
            var moved = Assert.Single(restored.ExportState().TerrainPlacements!);
            Assert.Equal(landing, moved.Cell);
            Assert.Equal(Material.Dirt, moved.Material);
        });
    }

    [Theory]
    [InlineData(Material.Dirt, 100)]
    [InlineData(Material.Wood, 250)]
    [InlineData(Material.Stone, 500)]
    [InlineData(Material.Copper, 1000)]
    [InlineData(Material.Iron, 2000)]
    [InlineData(Material.Gold, 3000)]
    [InlineData(Material.Diamond, 5000)]
    [InlineData(Material.Osmium, 8000)]
    public void ZombieBridgeTerrainOutsidePlayerBuildingBoundsPreservesDamageAndExcavationOnReload(Material material, int maxHp)
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var bridge = new[] { new Cell(0, 16, 8), new Cell(1, 16, 8), new Cell(2, 16, 8) };
            var gap = new[] { new Cell(1, 15, 8), new Cell(2, 15, 8) };
            document["Excavated"] = new JsonArray(gap.Select(cell => (JsonNode)SavedCell(cell)).ToArray());
            document["TerrainPlacements"] = new JsonArray(bridge.Select(cell => (JsonNode)new JsonObject
            {
                ["Cell"] = SavedCell(cell), ["Material"] = material.ToString()
            }).ToArray());
            document["DamagedTerrain"] = new JsonArray(TerrainDamage(bridge[1], 37));
            File.WriteAllText(path, document.ToJsonString());

            Assert.False(WorldGrid.IsFortificationZone(bridge[0]));
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.NotNull(reloaded);

            foreach (var cell in bridge)
                Assert.Equal(material, reloaded.World.GetNaturalMaterial(cell));
            foreach (var cell in gap) Assert.Equal(Material.Air, reloaded.World.GetNaturalMaterial(cell));
            Assert.Equal(37, reloaded.World.GetTerrainHp(bridge[1]));
            Assert.True(Balance.IsTerrainMaterial(material));
            Assert.Equal(maxHp, Balance.BlockHp(material));
            Assert.Equal(maxHp, reloaded.World.GetTerrainHp(bridge[0]));
            Assert.Equal(bridge.OrderBy(cell => cell.X), reloaded.ExportState().TerrainPlacements!.Select(placement => placement.Cell).OrderBy(cell => cell.X));
            Assert.Equal(Balance.StartingCurrency, reloaded.Economy.Currency);
            Assert.Equal(0, reloaded.Inventory.DirtCount);
            Assert.Equal(0, reloaded.Inventory.StoneCount);
        });
    }

    [Theory]
    [InlineData(GearTier.Wood, Material.Wood)]
    [InlineData(GearTier.Stone, Material.Stone)]
    [InlineData(GearTier.Copper, Material.Copper)]
    [InlineData(GearTier.Iron, Material.Iron)]
    [InlineData(GearTier.Gold, Material.Gold)]
    [InlineData(GearTier.Diamond, Material.Diamond)]
    [InlineData(GearTier.Osmium, Material.Osmium)]
    public void EveryZombieStartsWithOneBlockMatchingItsOriginalEquipment(GearTier tier, Material equippedMaterial)
    {
        foreach (var kind in Enum.GetValues<ZombieKind>())
        {
            var zombie = new Zombie { Kind = kind, Tier = tier };
            var expected = kind == ZombieKind.Basic ? Material.Dirt : equippedMaterial;
            Assert.Equal(1, zombie.CarriedBlocks);
            Assert.Equal(expected, zombie.CarriedBlockMaterial);
            zombie.Hp = Balance.ZombieHp;
            Assert.Empty(zombie.ArmorPieces);
            Assert.Equal(expected, zombie.CarriedBlockMaterial);
        }
    }

    [Theory]
    [InlineData(Material.Wood)]
    [InlineData(Material.Copper)]
    [InlineData(Material.Iron)]
    [InlineData(Material.Gold)]
    [InlineData(Material.Diamond)]
    [InlineData(Material.Osmium)]
    public void UnsupportedGearMaterialBridgeFallsWithItsDamageAndPersists(Material material)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var origin = new Cell(8, 17, 8);
            var landing = new Cell(8, 16, 8);
            var hp = Balance.BlockHp(material) - 17;
            document["TerrainPlacements"] = new JsonArray(new JsonObject
            {
                ["Cell"] = SavedCell(origin), ["Material"] = material.ToString()
            });
            document["DamagedTerrain"] = new JsonArray(TerrainDamage(origin, hp));
            File.WriteAllText(path, document.ToJsonString());
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);

            restored.Update(.05f);

            Assert.Equal(Material.Air, restored.World.GetNaturalMaterial(origin));
            Assert.Equal(material, restored.World.GetNaturalMaterial(landing));
            Assert.Equal(hp, restored.World.GetTerrainHp(landing));
            Assert.False(restored.Dig(landing));
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.NotNull(reloaded);
            Assert.Equal(material, reloaded.World.GetNaturalMaterial(landing));
            Assert.Equal(hp, reloaded.World.GetTerrainHp(landing));
            Assert.Equal(0, reloaded.Inventory.GetCount(material));
        });
    }

    [Theory]
    [InlineData(6, Material.Wood, 100)]
    [InlineData(6, Material.Osmium, 100)]
    [InlineData(7, Material.Wood, 251)]
    [InlineData(7, Material.Copper, 1001)]
    [InlineData(7, Material.Iron, 2001)]
    [InlineData(7, Material.Gold, 3001)]
    [InlineData(7, Material.Diamond, 5001)]
    [InlineData(7, Material.Osmium, 8001)]
    public void GearMaterialTerrainRejectsLegacyVersionsAndExcessDurability(int version, Material material, int hp)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            if (version < 8) SetCheckpointVersion(document, version);
            var cell = new Cell(8, 16, 8);
            document["TerrainPlacements"] = new JsonArray(new JsonObject
            {
                ["Cell"] = SavedCell(cell), ["Material"] = material.ToString()
            });
            document["DamagedTerrain"] = new JsonArray(TerrainDamage(cell, hp));
            AssertRejectedWithoutRewrite(path, document);
        });
    }

    [Fact]
    public void TowerStacksCanFallToTheBottomAndRetainTheirCellsAndHpOnReload()
    {
        WithSavePath(path =>
        {
            var game = SessionWithBothTools();
            game.Economy.Credit(3);
            for (int y = 16; y <= 18; y++) Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, y, 8)));
            game.Turrets[1].Hp = 23;
            for (int y = 15; y >= 0; y--) Assert.True(game.Dig(new Cell(8, y, 8)));
            Assert.Equal(new[] { new Cell(8, 0, 8), new Cell(8, 1, 8), new Cell(8, 2, 8) }, game.Turrets.Select(tower => tower.Cell));

            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Equal(game.Turrets.Select(tower => (tower.Id, tower.Cell, tower.Hp)), restored.Turrets.Select(tower => (tower.Id, tower.Cell, tower.Hp)));
            Assert.Equal(23, restored.Turrets[1].Hp);
            Assert.Equal(4, restored.Inventory.DirtCount);
            Assert.Equal(12, restored.Inventory.StoneCount);
        });
    }

    [Fact]
    public void APhysicalFallIntoTheBlockEnclosureDoesNotMakeTheCheckpointInvalid()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Inventory.Add(Material.Dirt, 4));
            for (int y = 16; y <= 19; y++) Assert.True(game.BuildBarrier(new Cell(14, y, 15)));
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 20, 15)));
            Assert.True(game.Remove(new Cell(14, 19, 15)));
            var fallen = Assert.Single(game.Turrets);
            Assert.Equal(new Cell(14, 19, 15), fallen.Cell);
            Assert.True(WorldGrid.IsBuildZone(fallen.Cell));

            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Equal(fallen.Cell, Assert.Single(restored.Turrets).Cell);
        });
    }

    [Fact]
    public void FallenFortificationBelowTheBuildZonePreservesMaterialAndDamage()
    {
        WithSavePath(path =>
        {
            var game = SessionWithBothTools();
            Assert.True(game.Inventory.Add(Material.Dirt));
            Assert.True(game.BuildBarrier(new Cell(12, 16, 12)));
            Assert.Single(game.World.Barriers).Hp = 73;
            for (int y = 15; y >= 11; y--)
                for (int x = 11; x <= 13; x++)
                    for (int z = 11; z <= 13; z++)
                        Assert.True(game.Dig(new Cell(x, y, z)));
            var fallen = Assert.Single(game.World.Barriers);
            Assert.Equal(new Cell(12, 11, 12), fallen.Cell);

            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            var reloaded = Assert.Single(restored.World.Barriers);
            Assert.Equal(fallen.Cell, reloaded.Cell);
            Assert.Equal(Material.Dirt, reloaded.Content);
            Assert.Equal(73, reloaded.Hp);
        });
    }

    [Fact]
    public void UnsupportedFortificationClustersSettleTogetherAndKeepTheirSavedCells()
    {
        WithSavePath(path =>
        {
            var game = SessionWithBothTools();
            Assert.True(game.Inventory.Add(Material.Dirt, 2));
            Assert.True(game.BuildBarrier(new Cell(12, 16, 12)));
            Assert.True(game.BuildBarrier(new Cell(12, 17, 12)));
            Assert.True(game.Dig(new Cell(12, 15, 12)));
            Assert.Equal(new[] { 15, 16 }, game.World.Barriers.Select(barrier => barrier.Cell.Y).Order());

            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Equal(new[] { 15, 16 }, restored.World.Barriers.Select(barrier => barrier.Cell.Y).Order());
            Assert.Equal(Material.Air, restored.World.GetNaturalMaterial(new Cell(12, 15, 12)));
        });
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void LegacyLiquidMigrationPreservesExistingStateAndBacksUpItsOriginalFile(int version)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            SetFluidFixture(document);
            SetCheckpointVersion(document, version);
            if (version < 6) document.AsObject().Remove("TerrainPlacements");
            document["Liquids"]!["Obsidian"]![0]!["Hp"] = 75;
            document["Inventory"]!["DirtCount"] = 7;
            document["DamagedTerrain"] = new JsonArray(TerrainDamage(new Cell(8, 15, 8), 73));
            string legacy = document.ToJsonString();
            File.WriteAllText(path, legacy);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Equal(legacy, File.ReadAllText(path));
            Assert.False(File.Exists(path + $".v{version}.bak"));
            Assert.Empty(restored.ExportState().TerrainPlacements!);
            Assert.Equal(7, restored.Inventory.DirtCount);
            Assert.Equal(73, restored.World.GetTerrainHp(new Cell(8, 15, 8)));
            Assert.Equal(7.5f, Assert.Single(restored.World.ObsidianCells).Hp);
            string liquids = System.Text.Json.JsonSerializer.Serialize(restored.Liquids.ExportState());
            SaveStore.Save(restored, path);

            Assert.Equal(legacy, File.ReadAllText(path + $".v{version}.bak"));
            Assert.Equal(9, JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>());
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.NotNull(reloaded);
            Assert.Equal(7, reloaded.Inventory.DirtCount);
            Assert.Equal(73, reloaded.World.GetTerrainHp(new Cell(8, 15, 8)));
            Assert.Equal(liquids, System.Text.Json.JsonSerializer.Serialize(reloaded.Liquids.ExportState()));
            Assert.Equal(restored.LiquidInventory.OrderBy(pair => pair.Key), reloaded.LiquidInventory.OrderBy(pair => pair.Key));
        });
    }

    [Theory]
    [InlineData(5, 1, 50f, 5f)]
    [InlineData(5, 2, 10f, 1f)]
    [InlineData(5, 10, 500f, 50f)]
    [InlineData(6, 1, 25f, 2.5f)]
    [InlineData(6, 2, 100f, 10f)]
    [InlineData(6, 10, 125f, 12.5f)]
    public void LegacyObsidianMigrationPreservesDurabilityFractionExactlyOnce(int version, int units, float oldHp, float expectedHp)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            SetFluidFixture(document);
            SetCheckpointVersion(document, version);
            if (version < 6) document.AsObject().Remove("TerrainPlacements");
            document["Liquids"]!["Cells"]![1]!["Mercury"] = 0;
            document["Liquids"]!["Cells"]![1]!["ObsidianUnits"] = units;
            document["Liquids"]!["Obsidian"]![0]!["Units"] = units;
            document["Liquids"]!["Obsidian"]![0]!["Hp"] = oldHp;
            var original = document.ToJsonString();
            File.WriteAllText(path, original);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.Equal(expectedHp, Assert.Single(restored!.World.ObsidianCells).Hp);
            Assert.Equal(original, File.ReadAllText(path));
            SaveStore.Save(restored, path);
            Assert.Equal(original, File.ReadAllText(path + $".v{version}.bak"));
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.Equal(expectedHp, Assert.Single(reloaded!.World.ObsidianCells).Hp);
        });
    }

    [Theory]
    [InlineData(5, 0f)]
    [InlineData(5, -1f)]
    [InlineData(5, 100.01f)]
    [InlineData(6, 100.01f)]
    [InlineData(7, 10.01f)]
    [InlineData(7, 75f)]
    public void ObsidianDurabilityIsValidatedAgainstItsOriginalCheckpointVersion(int version, float hp)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            SetFluidFixture(document);
            if (version < 8) SetCheckpointVersion(document, version);
            if (version < 6) document.AsObject().Remove("TerrainPlacements");
            document["Liquids"]!["Obsidian"]![0]!["Hp"] = hp;
            AssertRejectedWithoutRewrite(path, document);
            Assert.False(File.Exists(path + $".v{version}.bak"));
        });
    }

    [Theory]
    [InlineData("missing collection")]
    [InlineData("null collection")]
    [InlineData("null entry")]
    [InlineData("duplicate placement")]
    [InlineData("uncleared cell")]
    [InlineData("initial material")]
    [InlineData("outside grid")]
    [InlineData("objective")]
    [InlineData("invalid material")]
    [InlineData("missing material")]
    [InlineData("legacy placement")]
    public void InvalidMovedTerrainIsRejectedWithoutChangingTheCheckpoint(string defect)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var cell = new Cell(8, 11, 8);
            document["Excavated"] = new JsonArray(SavedCell(cell));
            var placement = new JsonObject { ["Cell"] = SavedCell(cell), ["Material"] = "Dirt" };
            document["TerrainPlacements"] = new JsonArray(placement);
            switch (defect)
            {
                case "missing collection": document.AsObject().Remove("TerrainPlacements"); break;
                case "null collection": document["TerrainPlacements"] = null; break;
                case "null entry": document["TerrainPlacements"]![0] = null; break;
                case "duplicate placement": document["TerrainPlacements"]!.AsArray().Add(placement.DeepClone()); break;
                case "uncleared cell": document["Excavated"] = new JsonArray(); break;
                case "initial material": placement["Material"] = "Stone"; break;
                case "outside grid": placement["Cell"]!["X"] = 32; break;
                case "objective": placement["Cell"] = SavedCell(new Cell(15, 15, 15)); break;
                case "invalid material": placement["Material"] = "Obsidian"; break;
                case "missing material": placement.AsObject().Remove("Material"); break;
                case "legacy placement": SetCheckpointVersion(document, 5); break;
            }
            AssertRejectedWithoutRewrite(path, document);
        });
    }

    [Theory]
    [InlineData("missing inventory")]
    [InlineData("null inventory")]
    [InlineData("missing kind")]
    [InlineData("unknown kind")]
    [InlineData("negative units")]
    [InlineData("fractional units")]
    [InlineData("missing simulation")]
    [InlineData("null simulation")]
    [InlineData("legacy fluid data")]
    public void InvalidLiquidCheckpointEnvelopeIsRejectedAndLeftIntact(string defect)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            switch (defect)
            {
                case "missing inventory": document.AsObject().Remove("LiquidInventory"); break;
                case "null inventory": document["LiquidInventory"] = null; break;
                case "missing kind": document["LiquidInventory"]!.AsObject().Remove("Mercury"); break;
                case "unknown kind": document["LiquidInventory"]!["Oil"] = 1; break;
                case "negative units": document["LiquidInventory"]!["Water"] = -1; break;
                case "fractional units": document["LiquidInventory"]!["Water"] = .5; break;
                case "missing simulation": document.AsObject().Remove("Liquids"); break;
                case "null simulation": document["Liquids"] = null; break;
                case "legacy fluid data": SetCheckpointVersion(document, 4); document.AsObject().Remove("TerrainPlacements"); break;
            }
            AssertRejectedWithoutRewrite(path, document);
        });
    }

    [Theory]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void LiquidCheckpointPreservesStratificationCrustDamageVaporAndFlowContinuation(int flowTick)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            SetFluidFixture(document);
            document["Liquids"]!["FlowTick"] = flowTick;
            File.WriteAllText(path, document.ToJsonString());

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Equal(13, restored.LiquidInventory[LiquidKind.Water]);
            Assert.Equal(27, restored.LiquidInventory[LiquidKind.Mercury]);
            Assert.Equal(9, restored.LiquidInventory[LiquidKind.Lava]);
            var state = restored.Liquids.ExportState();
            var layers = Assert.Single(state.Cells, cell => cell.Cell == new Cell(8, 16, 8));
            Assert.Equal(4, layers.Water);
            Assert.Equal(6, layers.Mercury);
            Assert.Equal(0, layers.Lava);
            var crust = Assert.Single(state.Obsidian);
            Assert.Equal(new Cell(9, 15, 8), crust.Cell);
            Assert.Equal(2, crust.Units);
            Assert.Equal(CrustFace.West, crust.Face);
            Assert.Equal(7.5f, crust.Hp);
            Assert.Equal(Material.Obsidian, restored.World.GetMaterial(crust.Cell));
            Assert.Equal(0, restored.Inventory.DirtCount);
            Assert.Equal(2, state.Hazards.Count);
            Assert.Equal(.07d, state.Accumulator);
            Assert.Equal(flowTick, state.FlowTick);
            restored.Liquids.Update(.023456789f);
            state = restored.Liquids.ExportState();
            Assert.Equal(.07d + .023456789f, state.Accumulator);
            string before = System.Text.Json.JsonSerializer.Serialize(state);

            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.NotNull(reloaded);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(reloaded.Liquids.ExportState()));
            restored.Liquids.Update(.17f);
            reloaded.Liquids.Update(.17f);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(restored.Liquids.ExportState()),
                System.Text.Json.JsonSerializer.Serialize(reloaded.Liquids.ExportState()));
            Assert.Equal(restored.LiquidInventory.OrderBy(pair => pair.Key), reloaded.LiquidInventory.OrderBy(pair => pair.Key));
        });
    }

    [Theory]
    [InlineData("missing cells")]
    [InlineData("null cells")]
    [InlineData("null entry")]
    [InlineData("duplicate cell")]
    [InlineData("outside grid")]
    [InlineData("solid terrain")]
    [InlineData("objective")]
    [InlineData("negative units")]
    [InlineData("fractional units")]
    [InlineData("over capacity")]
    [InlineData("crust capacity")]
    [InlineData("crust duplicate")]
    [InlineData("crust HP")]
    [InlineData("crust face")]
    [InlineData("cloud radius")]
    [InlineData("cloud lifetime")]
    [InlineData("cloud vapor units")]
    [InlineData("cloud kind")]
    [InlineData("negative accumulator")]
    [InlineData("whole tick accumulator")]
    [InlineData("negative flow tick")]
    public void InvalidLiquidGeometryOrSimulationStateIsRejectedAndLeftIntact(string defect)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            SetFluidFixture(document);
            var liquid = document["Liquids"]!;
            var first = liquid["Cells"]![0]!;
            switch (defect)
            {
                case "missing cells": liquid.AsObject().Remove("Cells"); break;
                case "null cells": liquid["Cells"] = null; break;
                case "null entry": liquid["Cells"]![0] = null; break;
                case "duplicate cell": liquid["Cells"]!.AsArray().Add(first.DeepClone()); break;
                case "outside grid": first["Cell"]!["X"] = 32; break;
                case "solid terrain": first["Cell"]!["Y"] = 15; break;
                case "objective": first["Cell"] = SavedCell(new Cell(15, 16, 15)); break;
                case "negative units": first["Water"] = -1; break;
                case "fractional units": first["Water"] = 1.5; break;
                case "over capacity": first["Water"] = 5; break;
                case "crust capacity": liquid["Cells"]![1]!["Mercury"] = 9; break;
                case "crust duplicate": liquid["Obsidian"]!.AsArray().Add(liquid["Obsidian"]![0]!.DeepClone()); break;
                case "crust HP": liquid["Obsidian"]![0]!["Hp"] = 10.01f; break;
                case "crust face": liquid["Obsidian"]![0]!["Face"] = "Invalid"; break;
                case "cloud radius": liquid["Hazards"]![0]!["Radius"] = 2; break;
                case "cloud lifetime": liquid["Hazards"]![0]!["RemainingSeconds"] = 0; break;
                case "cloud vapor units": liquid["Hazards"]![0]!["VaporizedUnits"] = 0; break;
                case "cloud kind": liquid["Hazards"]![0]!["Kind"] = "Pierce"; break;
                case "negative accumulator": liquid["Accumulator"] = -.01; break;
                case "whole tick accumulator": liquid["Accumulator"] = .1; break;
                case "negative flow tick": liquid["FlowTick"] = -1; break;
            }
            AssertRejectedWithoutRewrite(path, document);
        });
    }

    [Fact]
    public void CheckpointRejectsLiquidsOverlappingADeployedPiece()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            SetFluidFixture(document);
            AssertRejectedWithoutRewrite(path, document);
        });
    }

    [Theory]
    [InlineData(8, 15, 8, 0)]
    [InlineData(8, 15, 8, -1)]
    [InlineData(8, 15, 8, 100)]
    [InlineData(8, 15, 8, 101)]
    [InlineData(8, 11, 8, 500)]
    [InlineData(8, 11, 8, 501)]
    [InlineData(15, 15, 15, 1)]
    [InlineData(8, 16, 8, 1)]
    [InlineData(32, 15, 8, 1)]
    public void InvalidNaturalTerrainDamageIsRejectedWithoutChangingCheckpoint(int x, int y, int z, int hp)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["DamagedTerrain"] = new JsonArray(TerrainDamage(new Cell(x, y, z), hp));
            AssertRejectedWithoutRewrite(path, document);
        });
    }

    [Theory]
    [InlineData("missing collection")]
    [InlineData("null collection")]
    [InlineData("duplicate cell")]
    [InlineData("excavated cell")]
    [InlineData("missing HP")]
    [InlineData("missing cell")]
    [InlineData("null entry")]
    [InlineData("legacy collection")]
    [InlineData("nonfinite HP")]
    public void MalformedTerrainDamageDataIsRejectedWithoutChangingCheckpoint(string defect)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var dirt = new Cell(8, 15, 8);
            document["DamagedTerrain"] = new JsonArray(TerrainDamage(dirt, 73));
            switch (defect)
            {
                case "missing collection": document.AsObject().Remove("DamagedTerrain"); break;
                case "null collection": document["DamagedTerrain"] = null; break;
                case "duplicate cell": document["DamagedTerrain"]!.AsArray().Add(TerrainDamage(dirt, 72)); break;
                case "excavated cell": document["Excavated"] = new JsonArray(SavedCell(dirt)); break;
                case "missing HP": document["DamagedTerrain"]![0]!.AsObject().Remove("Hp"); break;
                case "missing cell": document["DamagedTerrain"]![0]!.AsObject().Remove("Cell"); break;
                case "null entry": document["DamagedTerrain"] = new JsonArray((JsonNode?)null); break;
                case "legacy collection": SetCheckpointVersion(document, 3); RemoveLiquidFields(document); break;
                case "nonfinite HP": document["DamagedTerrain"]![0]!["Hp"] = JsonNode.Parse("1e100"); break;
            }
            AssertRejectedWithoutRewrite(path, document);
        });
    }

    private static JsonObject TerrainDamage(Cell cell, float hp) => new() { ["Cell"] = SavedCell(cell), ["Hp"] = hp };

    private static GameSession SessionWithBothTools()
    {
        var game = new GameSession();
        game.Economy.Credit(100);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        return game;
    }

    private static void SetCheckpointVersion(JsonNode document, int version)
    {
        document["Version"] = version;
        if (version < 7) document.AsObject().Remove("UpgradeTemplate");
        if (version < 8)
        {
            document["Inventory"]?.AsObject().Remove("AdditionalMaterials");
            document.AsObject().Remove("PendingBlockDrops");
        }
    }

    private static void RemoveLiquidFields(JsonNode document)
    {
        document.AsObject().Remove("TerrainPlacements");
        document.AsObject().Remove("Liquids");
        document.AsObject().Remove("LiquidInventory");
    }

    private static void SetFluidFixture(JsonNode document)
    {
        document["Excavated"] = new JsonArray(SavedCell(new Cell(9, 15, 8)));
        document["LiquidInventory"] = JsonNode.Parse("""{"Water":13,"Mercury":27,"Lava":9}""");
        document["Liquids"] = JsonNode.Parse("""
            {
              "Cells": [
                {"Cell":{"X":8,"Y":16,"Z":8},"Water":4,"Mercury":6,"Lava":0,"ObsidianUnits":0,"CrustFace":null},
                {"Cell":{"X":9,"Y":15,"Z":8},"Water":0,"Mercury":3,"Lava":0,"ObsidianUnits":2,"CrustFace":"West"}
              ],
              "Obsidian": [{"Cell":{"X":9,"Y":15,"Z":8},"Units":2,"Face":"West","Hp":7.5}],
              "Hazards": [
                {"Center":{"X":8,"Y":17,"Z":8},"Radius":1,"Kind":"Thermal","RemainingSeconds":3,"Dps":2,"VaporizedUnits":10},
                {"Center":{"X":12,"Y":17,"Z":8},"Radius":2,"Kind":"Chemical","RemainingSeconds":6,"Dps":4,"VaporizedUnits":3}
              ],
              "Accumulator":0.07,
              "FlowTick":7
            }
            """);
    }

    private static JsonObject SavedCell(Cell cell) => new() { ["X"] = cell.X, ["Y"] = cell.Y, ["Z"] = cell.Z };

    private static void AssertRejectedWithoutRewrite(string path, JsonNode document)
    {
        string invalid = document.ToJsonString();
        File.WriteAllText(path, invalid);
        Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
        Assert.Null(restored);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    private static void SetLegacyProgress(System.Text.Json.Nodes.JsonNode document, string kind, int pulls, int available)
    {
        document["Economy"]!["Upgrades"]![kind]!["PullCount"] = pulls;
        document["Economy"]!["Upgrades"]![kind]!["Available"] = available;
    }

    private static void WithSavePath(Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), "ChessVsZombiesSaveTests-" + Guid.NewGuid().ToString("N") + ".json");
        try { test(path); }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".v1.bak");
            File.Delete(path + ".v2.bak");
            File.Delete(path + ".v3.bak");
            File.Delete(path + ".v4.bak");
            File.Delete(path + ".v5.bak");
            File.Delete(path + ".v6.bak");
            File.Delete(path + ".v7.bak");
            File.Delete(path + ".v8.bak");
        }
    }
}
