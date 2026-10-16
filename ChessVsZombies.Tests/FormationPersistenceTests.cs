using System.Text.Json.Nodes;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class FormationPersistenceTests
{
    [Fact]
    public void SavedFormationRoundTripsAfterItsTowersAreRemovedWithoutResumingAutoRestore()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Cell cell = new(8, 16, 8);
            Assert.True(game.Deploy(PieceKind.Knight, cell));
            var turret = Assert.Single(game.Turrets);
            game.Economy.GetProgress(UpgradeKind.Jump).PullCount = 21;
            game.Economy.GetProgress(UpgradeKind.Jump).Available = 3;
            Assert.True(game.Economy.Apply(turret, UpgradeKind.Jump));
            Assert.True(game.Economy.TrySetJumpPattern(turret, [new Cell(3, 0, 1)], out _));
            Assert.True(game.SaveFormation());
            Assert.True(game.Remove(cell));
            Assert.True(game.StartAutoRestoreFormation());
            SaveStore.Save(game, path);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            var snapshot = Assert.Single(restored!.SavedFormation!.Towers);
            Assert.Equal(PieceKind.Knight, snapshot.Kind);
            Assert.Equal(cell, snapshot.Cell);
            Assert.Equal(3, snapshot.UpgradePoints[UpgradeKind.Jump]);
            Assert.Equal(new Cell(3, 0, 1), Assert.Single(snapshot.JumpPattern));
            Assert.Empty(restored.Turrets);
            Assert.Single(restored.MissingFormationTurrets);
            Assert.False(restored.IsAutoRestoringFormation);
            Assert.Equal(8, JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>());
        });
    }

    [Fact]
    public void CheckpointsWithoutFormationDataRemainCompatible()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document.AsObject().Remove("SavedFormation");
            File.WriteAllText(path, document.ToJsonString());
            Assert.True(SaveStore.TryLoad(path, out var game, out var error), error);
            Assert.Null(game!.SavedFormation);
            Assert.Empty(game.MissingFormationTurrets);
        });
    }

    [Fact]
    public void CombatFormationSaveRetainsPreparedBattlefieldAndEconomyEvenWithNewerUpgrades()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            var cell = new Cell(8, 16, 8);
            Assert.True(game.Deploy(PieceKind.Knight, cell));
            SaveStore.Save(game, path);
            Assert.True(game.StartLevel());
            game.Economy.Credit(123);
            game.Economy.GetProgress(UpgradeKind.HP).PullCount = 11;
            game.Economy.GetProgress(UpgradeKind.HP).Available = 2;
            var turret = Assert.Single(game.Turrets);
            Assert.True(game.Economy.Apply(turret, UpgradeKind.HP));
            Assert.True(game.Economy.Apply(turret, UpgradeKind.HP));
            turret.Hp = 2;
            Assert.True(game.SaveFormation());

            Assert.True(SaveStore.TrySaveFormationTemplate(game.SavedFormation!, path, out var error), error);
            Assert.True(SaveStore.TryLoad(path, out var prepared, out error), error);
            Assert.Equal(GamePhase.Building, prepared!.Phase);
            Assert.Equal(998, prepared.Economy.Currency);
            Assert.Equal(Balance.TurretHp, Assert.Single(prepared.Turrets).Hp);
            Assert.Empty(Assert.Single(prepared.Turrets).UpgradePoints);
            Assert.Equal(0, prepared.Economy.GetProgress(UpgradeKind.HP).PullCount);
            Assert.Equal(2, Assert.Single(prepared.SavedFormation!.Towers).UpgradePoints[UpgradeKind.HP]);
            Assert.Equal(GamePhase.Combat, game.Phase);
            Assert.Equal(1121, game.Economy.Currency);
            Assert.Equal(2, turret.Hp);

            // The separately saved template is allowed to need future collection caps.
            Assert.True(prepared.Remove(cell));
            Assert.True(prepared.StartAutoRestoreFormation());
            prepared.Update(.36f);
            Assert.Empty(prepared.Turrets);
            Assert.Contains("cap", prepared.FormationRestoreStatus);
        });
    }

    [Fact]
    public void FormationValidationIsIndependentOfCurrentTerrainAndOccupiedCells()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            var cell = new Cell(8, 16, 8);
            Assert.True(game.Deploy(PieceKind.Pawn, cell));
            Assert.True(game.SaveFormation());
            Assert.True(game.Remove(cell));
            Assert.True(game.Inventory.Add(Material.Dirt));
            Assert.True(game.BuildBarrier(cell));
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.Single(restored!.MissingFormationTurrets);
            Assert.Equal(Material.Barrier, restored.World.GetMaterial(cell));
            Assert.True(restored.StartAutoRestoreFormation());
            restored.Update(.36f);
            Assert.Equal(cell, Assert.Single(restored.Turrets).Cell);
            Assert.Null(restored.World.GetBarrier(cell));
            Assert.Equal(1, restored.Inventory.DirtCount);
            Assert.False(restored.IsAutoRestoringFormation);
        });
    }

    [Theory]
    [InlineData("null towers")]
    [InlineData("empty towers")]
    [InlineData("null tower")]
    [InlineData("duplicate cell")]
    [InlineData("outside cell")]
    [InlineData("core cell")]
    [InlineData("unknown kind")]
    [InlineData("null upgrades")]
    [InlineData("negative upgrade")]
    [InlineData("unknown upgrade")]
    [InlineData("overflow upgrade")]
    [InlineData("innate upgrade")]
    [InlineData("null pattern")]
    [InlineData("empty pattern")]
    [InlineData("duplicate offset")]
    [InlineData("near offset")]
    [InlineData("out of range offset")]
    [InlineData("unsafe offset")]
    public void CorruptFormationIsRejectedWithoutChangingTheCheckpoint(string fault)
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Rook, new Cell(8, 16, 8)));
            Assert.True(game.SaveFormation());
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var formation = document["SavedFormation"]!;
            var tower = formation["Towers"]![0]!;
            switch (fault)
            {
                case "null towers": formation["Towers"] = null; break;
                case "empty towers": formation["Towers"] = new JsonArray(); break;
                case "null tower": formation["Towers"]![0] = null; break;
                case "duplicate cell": formation["Towers"]!.AsArray().Add(tower.DeepClone()); break;
                case "outside cell": tower["Cell"]!["X"] = 31; break;
                case "core cell": tower["Cell"] = CellNode(15, 15, 15); break;
                case "unknown kind": tower["Kind"] = "Queen"; break;
                case "null upgrades": tower["UpgradePoints"] = null; break;
                case "negative upgrade": tower["UpgradePoints"]!["HP"] = -1; break;
                case "unknown upgrade": tower["UpgradePoints"]!["invalid"] = 1; break;
                case "overflow upgrade": tower["UpgradePoints"]!["HP"] = int.MaxValue; break;
                case "innate upgrade": tower["UpgradePoints"]!["Orthogonal"] = 1; break;
                case "null pattern": tower["JumpPattern"] = null; break;
                case "empty pattern": tower["JumpPattern"] = new JsonArray(); break;
                case "duplicate offset": tower["JumpPattern"]!.AsArray().Add(tower["JumpPattern"]![0]!.DeepClone()); break;
                case "near offset": tower["JumpPattern"] = new JsonArray(CellNode(1, 0, 0)); break;
                case "out of range offset": tower["JumpPattern"] = new JsonArray(CellNode(3, 0, 0)); break;
                case "unsafe offset": tower["JumpPattern"] = new JsonArray(CellNode(int.MinValue, 0, 0)); break;
            }
            string invalid = document.ToJsonString();
            File.WriteAllText(path, invalid);
            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    [Fact]
    public void FormationOnlySavePreservesMissingAndCorruptCheckpoints()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
            Assert.True(game.SaveFormation());
            Assert.False(SaveStore.TrySaveFormationTemplate(game.SavedFormation!, path, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.False(File.Exists(path));
            const string invalid = "{ invalid";
            File.WriteAllText(path, invalid);
            Assert.False(SaveStore.TrySaveFormationTemplate(game.SavedFormation!, path, out error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    private static JsonObject CellNode(int x, int y, int z) => new() { ["X"] = x, ["Y"] = y, ["Z"] = z };

    private static void WithSavePath(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "ChessVsZombiesFormation-" + Guid.NewGuid().ToString("N") + ".json");
        try { action(path); }
        finally { File.Delete(path); }
    }
}
