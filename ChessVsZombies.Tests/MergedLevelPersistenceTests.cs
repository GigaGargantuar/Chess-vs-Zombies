using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MergedLevelPersistenceTests
{
    [Fact]
    public void PreparedMergeRestoresAllFiveLevelsButVictorySavesTheNormalNextBattle()
    {
        var catalog = SmallCatalog();
        var game = new GameSession(levels: catalog);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(5, 16, 5)));
        Assert.True(game.SetMergeFiveLevels(true));
        WithCheckpoint(path =>
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error, catalog), error);
            Assert.NotNull(restored);
            Assert.True(restored.MergeFiveLevels);
            Assert.Equal(5, restored.EndLevelNumber);
            Assert.Equal(5, restored.PreparedWaves.Count);
            Assert.Equal(25, restored.SurvivorRewardPerTower);
            Assert.Equal(Enumerable.Range(1, 5), Enumerable.Range(0, 5).Select(restored.GetSourceLevelNumber));
            int before = restored.Economy.Currency;
            Assert.True(restored.StartLevel());
            FinishBattle(restored);
            Assert.Equal(6, restored.LevelNumber);
            Assert.Equal(6, restored.EndLevelNumber);
            Assert.False(restored.MergeFiveLevels);
            Assert.Single(restored.PreparedWaves);
            Assert.Equal(before + 25, restored.Economy.Currency);
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var afterVictory, out error, catalog), error);
            Assert.NotNull(afterVictory);
            afterVictory.Update(1);
            Assert.Equal(6, afterVictory.LevelNumber);
            Assert.False(afterVictory.MergeFiveLevels);
            Assert.Equal(1, afterVictory.LevelSpan);
            Assert.Equal(6, afterVictory.EndLevelNumber);
            Assert.Equal(2, afterVictory.SurvivorRewardPerTower);
            Assert.Single(afterVictory.PreparedWaves);
            Assert.Equal(6, afterVictory.GetSourceLevelNumber(0));
            Assert.Equal(before + 25, afterVictory.Economy.Currency);
            Assert.True(afterVictory.StartLevel());
            Assert.Single(afterVictory.Waves);
            FinishBattle(afterVictory);
            Assert.Equal(7, afterVictory.LevelNumber);
            Assert.Equal(before + 27, afterVictory.Economy.Currency);
        });
    }

    [Fact]
    public void OlderCheckpointWithoutPreferenceKeepsNormalProgression()
    {
        WithCheckpoint(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document.Remove("MergeFiveLevels");
            File.WriteAllText(path, document.ToJsonString());
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.False(restored.MergeFiveLevels);
            Assert.Equal(1, restored.LevelSpan);
            Assert.Equal(2, restored.SurvivorRewardPerTower);
            Assert.Single(restored.PreparedWaves);
        });
    }

    [Fact]
    public void SavingCombatTemplatePreservesMergedPreparationAndItsWorld()
    {
        var game = new GameSession();
        Assert.True(game.SetMergeFiveLevels(true));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(5, 16, 5)));
        WithCheckpoint(path =>
        {
            SaveStore.Save(game, path);
            string prepared = JsonSerializer.Serialize(game.ExportState());
            Assert.True(game.StartLevel());
            Assert.True(game.Deploy(PieceKind.Knight, new Cell(6, 16, 5)));
            game.UpgradeTemplate.Points[UpgradeKind.HP] = 2;
            Assert.True(SaveStore.TrySaveUpgradeTemplate(game.UpgradeTemplate, path, out var error), error);
            Assert.True(SaveStore.TryLoad(path, out var restored, out error), error);
            Assert.NotNull(restored);
            Assert.True(restored.MergeFiveLevels);
            Assert.Equal(1, restored.LevelNumber);
            Assert.Single(restored.Turrets);
            Assert.Equal(2, restored.UpgradeTemplate.Points[UpgradeKind.HP]);
            var expected = JsonNode.Parse(prepared)!.AsObject();
            var actual = JsonNode.Parse(JsonSerializer.Serialize(restored.ExportState()))!.AsObject();
            expected.Remove("UpgradeTemplate");
            actual.Remove("UpgradeTemplate");
            Assert.True(JsonNode.DeepEquals(expected, actual));
        });
    }

    [Fact]
    public void ClearingTheFinishedBattleSelectionPreservesThePreparedCheckpointExceptForMerge()
    {
        var game = new GameSession();
        Assert.True(game.SetMergeFiveLevels(true));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(5, 16, 5)));
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Dig(new Cell(8, 15, 8)));
        Assert.True(game.BuildBarrier(new Cell(14, 16, 14), Material.Dirt));
        game.UpgradeTemplate.Points[UpgradeKind.HP] = 2;
        game.UpgradeTemplate.UseJumpPattern = true;
        game.UpgradeTemplate.JumpPattern = [new Cell(2, 1, 0)];
        WithCheckpoint(path =>
        {
            SaveStore.Save(game, path);
            var expected = JsonNode.Parse(JsonSerializer.Serialize(game.ExportState()))!.AsObject();
            Assert.True(game.StartLevel());
            Assert.True(game.Deploy(PieceKind.Knight, new Cell(6, 16, 5)));

            Assert.True(SaveStore.TryClearMergeSelection(path, out var error), error);

            Assert.True(SaveStore.TryLoad(path, out var restored, out error), error);
            Assert.NotNull(restored);
            Assert.False(restored.MergeFiveLevels);
            Assert.Single(restored.PreparedWaves);
            Assert.Equal(1, restored.LevelNumber);
            var actual = JsonNode.Parse(JsonSerializer.Serialize(restored.ExportState()))!.AsObject();
            expected.Remove("MergeFiveLevels");
            actual.Remove("MergeFiveLevels");
            Assert.True(JsonNode.DeepEquals(expected, actual));
            string cleared = File.ReadAllText(path);
            Assert.True(SaveStore.TryClearMergeSelection(path, out error), error);
            Assert.Equal(cleared, File.ReadAllText(path));
        });
    }

    [Fact]
    public void ClearingMergeWithoutAPreparedCheckpointFailsWithoutCreatingOne()
    {
        WithCheckpoint(path =>
        {
            Assert.False(File.Exists(path));

            Assert.False(SaveStore.TryClearMergeSelection(path, out var error));

            Assert.NotEmpty(error);
            Assert.False(File.Exists(path));
        });
    }

    [Theory]
    [InlineData("{ broken JSON")]
    [InlineData("{\"Version\":8,\"MergeFiveLevels\":true}")]
    public void ClearingMergeFromACorruptCheckpointPreservesItsOriginalContents(string invalid)
    {
        WithCheckpoint(path =>
        {
            File.WriteAllText(path, invalid);

            Assert.False(SaveStore.TryClearMergeSelection(path, out var error));

            Assert.NotEmpty(error);
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    [Theory]
    [InlineData("\"yes\"", 1)]
    [InlineData("null", 1)]
    [InlineData("true", int.MaxValue)]
    public void InvalidMergedCheckpointFailsWithoutChangingTheFile(string preference, int level)
    {
        WithCheckpoint(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document["MergeFiveLevels"] = JsonNode.Parse(preference);
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
    public void DisablingSavedPreferenceRestoresNormalPreviewAndResetClearsIt()
    {
        var game = new GameSession();
        Assert.True(game.SetMergeFiveLevels(true));
        WithCheckpoint(path =>
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.True(restored.SetMergeFiveLevels(false));
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var normal, out error), error);
            Assert.NotNull(normal);
            Assert.False(normal.MergeFiveLevels);
            Assert.Single(normal.PreparedWaves);
            Assert.True(normal.SetMergeFiveLevels(true));
            normal.Reset();
            Assert.False(normal.MergeFiveLevels);
            Assert.Equal(1, normal.LevelNumber);
            Assert.Single(normal.PreparedWaves);
        });
    }

    [Fact]
    public void ChangingPreferenceKeepsDeferredSurvivorAtFirstWaveAcrossSaveLoad()
    {
        var catalog = SmallCatalog();
        var game = new GameSession(levels: catalog);
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var survivor = Assert.Single(game.Zombies);
        typeof(Zombie).GetProperty(nameof(Zombie.RetreatReturns))!.SetValue(survivor, 3);
        typeof(GameSession).GetMethod("DeferZombieToNextWave", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(game, [survivor]);
        game.Update(.01f);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(2, game.LevelNumber);
        Assert.True(game.SetMergeFiveLevels(true));
        WithCheckpoint(path =>
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error, catalog), error);
            Assert.NotNull(restored);
            Assert.Equal(1, restored.PendingReinforcementCount);
            var saved = Assert.Single(restored.ExportState().ReturningZombies!);
            Assert.Equal(2, saved.TargetLevel);
            Assert.Equal(0, saved.TargetWaveIndex);
            Assert.True(restored.StartLevel());
            restored.Update(.01f);
            Assert.Contains(restored.Zombies, zombie => zombie.Id == survivor.Id && zombie.WaveIndex == 0);
            Assert.Equal(0, restored.PendingReinforcementCount);
        });
    }

    private static LevelCatalog SmallCatalog()
    {
        var catalog = new LevelCatalog();
        for (int level = 1; level <= 15; level++)
            catalog.Set(new LevelDefinition { Number = level, Waves = [new() { SpawnInterval = .01f, Groups = [new() { Count = 1 }] }] });
        return catalog;
    }

    private static void FinishBattle(GameSession game)
    {
        for (int step = 0; step < 100 && game.Phase == GamePhase.Combat; step++)
        {
            game.Update(.01f);
            foreach (var zombie in game.Zombies.ToArray()) game.DamageZombie(zombie, 10000);
        }
        if (game.Phase == GamePhase.Combat)
        {
            StandbyTestSupport.FinishQueuedSlots(game);
            game.Update(.01f);
        }
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    private static void WithCheckpoint(Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-merge-{Guid.NewGuid():N}.json");
        try { test(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
