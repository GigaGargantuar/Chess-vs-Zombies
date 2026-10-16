using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class AllInOnePersistenceTests
{
    [Fact]
    public void SelectedAllInOneRestoresItsPreviewAndVictorySavesAnOrdinaryNextBattle()
    {
        var catalog = SmallCatalog();
        var game = new GameSession(levels: catalog);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(5, 16, 5)));
        Assert.True(game.SetAllInOne(true));
        string prepared = JsonSerializer.Serialize(game.PreparedWaves);
        WithCheckpoint(path =>
        {
            SaveStore.Save(game, path);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error, catalog), error);
            Assert.NotNull(restored);
            Assert.True(restored.AllInOne);
            Assert.True(restored.MergeFiveLevels);
            Assert.Equal(5, restored.EndLevelNumber);
            Assert.Equal(5, restored.GetSourceLevelNumber(0));
            Assert.Equal(75, restored.SurvivorRewardPerTower);
            Assert.Equal(prepared, JsonSerializer.Serialize(restored.PreparedWaves));
            int balance = restored.Economy.Currency;
            Assert.True(restored.StartLevel());
            FinishBattle(restored);
            Assert.Equal(6, restored.LevelNumber);
            Assert.Equal(balance + 75, restored.Economy.Currency);
            SaveStore.Save(restored, path);

            Assert.True(SaveStore.TryLoad(path, out var next, out error, catalog), error);
            Assert.NotNull(next);
            Assert.False(next.AllInOne);
            Assert.False(next.MergeFiveLevels);
            Assert.Equal(6, next.LevelNumber);
            Assert.Single(next.PreparedWaves);
            next.Update(.1f);
            Assert.Equal(balance + 75, next.Economy.Currency);
            Assert.True(next.StartLevel());
            FinishBattle(next);
            Assert.Equal(7, next.LevelNumber);
            Assert.Equal(balance + 77, next.Economy.Currency);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderCheckpointWithoutAllInOneKeepsItsExistingMode(bool merged)
    {
        var game = new GameSession(levels: SmallCatalog());
        Assert.True(game.SetMergeFiveLevels(merged));
        WithCheckpoint(path =>
        {
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document.Remove("AllInOne");
            File.WriteAllText(path, document.ToJsonString());

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error, SmallCatalog()), error);

            Assert.NotNull(restored);
            Assert.False(restored.AllInOne);
            Assert.Equal(merged, restored.MergeFiveLevels);
            Assert.Equal(merged ? 5 : 1, restored.PreparedWaves.Count);
            Assert.Equal(merged ? 25 : 2, restored.SurvivorRewardPerTower);
        });
    }

    [Theory]
    [InlineData("\"yes\"", true, 1)]
    [InlineData("null", true, 1)]
    [InlineData("true", false, 1)]
    [InlineData("true", true, int.MaxValue)]
    public void InvalidAllInOneCheckpointFailsWithoutChangingTheFile(string preference, bool merged, int level)
    {
        WithCheckpoint(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document["AllInOne"] = JsonNode.Parse(preference);
            document["MergeFiveLevels"] = merged;
            document["LevelNumber"] = level;
            string invalid = document.ToJsonString();
            File.WriteAllText(path, invalid);

            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));

            Assert.Null(restored);
            Assert.NotEmpty(error);
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    [Fact]
    public void ClearingAllInOnePreservesTheRestOfThePreparedCheckpoint()
    {
        var game = new GameSession();
        Assert.True(game.SetAllInOne(true));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(5, 16, 5)));
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Dig(new Cell(8, 15, 8)));
        game.UpgradeTemplate.Points[UpgradeKind.HP] = 2;
        WithCheckpoint(path =>
        {
            SaveStore.Save(game, path);
            var expected = JsonNode.Parse(JsonSerializer.Serialize(game.ExportState()))!.AsObject();

            Assert.True(SaveStore.TryClearMergeSelection(path, out var error), error);
            Assert.True(SaveStore.TryLoad(path, out var restored, out error), error);

            Assert.NotNull(restored);
            Assert.False(restored.AllInOne);
            Assert.False(restored.MergeFiveLevels);
            Assert.Single(restored.PreparedWaves);
            var actual = JsonNode.Parse(JsonSerializer.Serialize(restored.ExportState()))!.AsObject();
            expected.Remove("AllInOne");
            expected.Remove("MergeFiveLevels");
            actual.Remove("AllInOne");
            actual.Remove("MergeFiveLevels");
            Assert.True(JsonNode.DeepEquals(expected, actual));
            string cleared = File.ReadAllText(path);
            Assert.True(SaveStore.TryClearMergeSelection(path, out error), error);
            Assert.Equal(cleared, File.ReadAllText(path));
        });
    }

    [Fact]
    public void UpdatingCombatTemplatePreservesThePreparedAllInOneSelectionAndWorld()
    {
        var game = new GameSession();
        Assert.True(game.SetAllInOne(true));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(5, 16, 5)));
        WithCheckpoint(path =>
        {
            SaveStore.Save(game, path);
            var expected = JsonNode.Parse(JsonSerializer.Serialize(game.ExportState()))!.AsObject();
            Assert.True(game.StartLevel());
            Assert.True(game.Deploy(PieceKind.Knight, new Cell(6, 16, 5)));
            game.UpgradeTemplate.Points[UpgradeKind.HP] = 2;

            Assert.True(SaveStore.TrySaveUpgradeTemplate(game.UpgradeTemplate, path, out var error), error);
            Assert.True(SaveStore.TryLoad(path, out var restored, out error), error);

            Assert.NotNull(restored);
            Assert.True(restored.AllInOne);
            Assert.True(restored.MergeFiveLevels);
            Assert.Single(restored.PreparedWaves);
            Assert.Single(restored.Turrets);
            Assert.Equal(2, restored.UpgradeTemplate.Points[UpgradeKind.HP]);
            var actual = JsonNode.Parse(JsonSerializer.Serialize(restored.ExportState()))!.AsObject();
            expected.Remove("UpgradeTemplate");
            actual.Remove("UpgradeTemplate");
            Assert.True(JsonNode.DeepEquals(expected, actual));
        });
    }

    [Fact]
    public void DeferredSurvivorKeepsItsIdentityWhenAnAllInOneSelectionIsSavedAndLoaded()
    {
        var catalog = SmallCatalog();
        var game = new GameSession(levels: catalog);
        Assert.True(game.StartLevel());
        game.Update(.001f);
        var survivor = Assert.Single(game.Zombies);
        typeof(GameSession).GetMethod("DeferZombieToNextWave", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(game, [survivor]);
        game.Update(.001f);
        Assert.Equal(2, game.LevelNumber);
        Assert.True(game.SetAllInOne(true));
        WithCheckpoint(path =>
        {
            SaveStore.Save(game, path);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error, catalog), error);

            Assert.NotNull(restored);
            Assert.True(restored.AllInOne);
            Assert.Equal(1, restored.PendingReinforcementCount);
            var saved = Assert.Single(restored.ExportState().ReturningZombies!);
            Assert.Equal(2, saved.TargetLevel);
            Assert.Equal(0, saved.TargetWaveIndex);
            Assert.True(restored.StartLevel());
            Assert.Equal(6, Assert.Single(restored.Waves).TotalCount);
            restored.Update(.001f);
            Assert.Equal(survivor.Id, Assert.Single(restored.Zombies).Id);
            Assert.Equal(0, restored.PendingReinforcementCount);
        });
    }

    private static LevelCatalog SmallCatalog()
    {
        var catalog = new LevelCatalog();
        for (int level = 1; level <= 15; level++)
            catalog.Set(new LevelDefinition
                { Number = level, Waves = [new() { SpawnInterval = .1f, Groups = [new() { Count = 1 }] }] });
        return catalog;
    }

    private static void FinishBattle(GameSession game)
    {
        for (int step = 0; step < 100 && game.Phase == GamePhase.Combat; step++)
        {
            game.Update(.01f);
            foreach (var zombie in game.Zombies.ToArray()) game.DamageZombie(zombie, 1_000_000, DamageKind.Chemical);
        }
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    private static void WithCheckpoint(Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-all-in-one-{Guid.NewGuid():N}.json");
        try { test(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
