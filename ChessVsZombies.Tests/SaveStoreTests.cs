using ChessVsZombies.Core;
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
                original.Economy.GetProgress(kind).PullCount = 11;
                original.Economy.GetProgress(kind).Available = 2;
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
    public void CurrentCheckpointRejectsATurretInsideTheReservedBlockVolume()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 16, 12)));
            SaveStore.Save(game, path);
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
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
            document["Version"] = 1;
            document.AsObject().Remove("Inventory");
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
            Assert.Equal(3, saved["Version"]!.GetValue<int>());
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
            document["Version"] = 1;
            document.AsObject().Remove("Inventory");
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
            document["Version"] = version;
            document.AsObject().Remove("Inventory");
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
        }
    }
}
