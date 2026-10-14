using System.Text.Json.Nodes;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class TemplatePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemplateRoundTripPreservesUnavailableRequestsAndPatternWithoutSpendingPoints(bool enabled)
    {
        WithSavePath(path =>
        {
            var original = new GameSession();
            original.UpgradeTemplate.Points = new()
            {
                [UpgradeKind.HP] = int.MaxValue, [UpgradeKind.ATK] = 0,
                [UpgradeKind.Jump] = 7, [UpgradeKind.Orthogonal] = 99
            };
            original.UpgradeTemplate.UseJumpPattern = enabled;
            original.UpgradeTemplate.JumpPattern = [new(5, -2, 0), new(-3, 1, 4)];

            SaveStore.Save(original, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);

            Assert.NotNull(restored);
            Assert.Equal(8, JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>());
            Assert.Equal(original.UpgradeTemplate.Points.OrderBy(pair => pair.Key), restored.UpgradeTemplate.Points.OrderBy(pair => pair.Key));
            Assert.Equal(enabled, restored.UpgradeTemplate.UseJumpPattern);
            Assert.Equal(original.UpgradeTemplate.JumpPattern, restored.UpgradeTemplate.JumpPattern);
            Assert.Equal(1000, restored.Economy.Currency);
            Assert.All(restored.Economy.Progress.Values, progress =>
            {
                Assert.Equal(0, progress.PullCount);
                Assert.Equal(0, progress.Available);
            });
        });
    }

    [Fact]
    public void ApplyingOnlyAvailablePointsDoesNotConsumeOrRewriteTheSavedRequest()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            var cell = new Cell(8, 16, 8);
            Assert.True(game.Deploy(PieceKind.Pawn, cell));
            var hp = game.Economy.GetProgress(UpgradeKind.HP);
            hp.PullCount = hp.Available = 1;
            game.UpgradeTemplate.Points[UpgradeKind.HP] = 3;
            Assert.True(game.ApplyUpgradeTemplate(cell, game.UpgradeTemplate));

            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);

            Assert.NotNull(restored);
            Assert.Equal(3, restored.UpgradeTemplate.Points[UpgradeKind.HP]);
            Assert.Equal(1, Assert.Single(restored.Turrets).UpgradePoints[UpgradeKind.HP]);
            Assert.Equal(0, restored.Economy.GetProgress(UpgradeKind.HP).Available);
        });
    }

    [Fact]
    public void ExportedTemplateCollectionsAreIndependentSnapshots()
    {
        var game = new GameSession();
        game.UpgradeTemplate.Points[UpgradeKind.ATK] = 5;
        game.UpgradeTemplate.UseJumpPattern = true;
        game.UpgradeTemplate.JumpPattern = [new(4, 0, 2)];
        var exported = game.ExportState().UpgradeTemplate!;

        game.UpgradeTemplate.Points[UpgradeKind.ATK] = 9;
        game.UpgradeTemplate.JumpPattern.Add(new(2, 2, 0));
        Assert.Equal(5, exported.Points[UpgradeKind.ATK]);
        Assert.Equal(new Cell(4, 0, 2), Assert.Single(exported.JumpPattern));
        exported.Points[UpgradeKind.HP] = 17;
        exported.JumpPattern.Clear();
        exported.UseJumpPattern = false;
        Assert.False(game.UpgradeTemplate.Points.ContainsKey(UpgradeKind.HP));
        Assert.Equal(2, game.UpgradeTemplate.JumpPattern.Count);
        Assert.True(game.UpgradeTemplate.UseJumpPattern);
    }

    [Fact]
    public void ResetStartsWithAnIndependentDefaultTemplate()
    {
        var game = new GameSession();
        var previous = game.UpgradeTemplate;
        previous.Points[UpgradeKind.HP] = 4;
        previous.UseJumpPattern = true;
        previous.JumpPattern.Clear();

        game.Reset();

        Assert.NotSame(previous, game.UpgradeTemplate);
        Assert.Empty(game.UpgradeTemplate.Points);
        Assert.False(game.UpgradeTemplate.UseJumpPattern);
        Assert.Equal(new UpgradeTemplate().JumpPattern, game.UpgradeTemplate.JumpPattern);
        Assert.Equal(4, previous.Points[UpgradeKind.HP]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void OlderCheckpointsGainDefaultTemplatesAndKeepOriginalBackupOnFirstSave(int version)
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            game.Economy.Credit(17);
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["Version"] = version;
            document.AsObject().Remove("UpgradeTemplate");
            document["Inventory"]?.AsObject().Remove("AdditionalMaterials");
            document.AsObject().Remove("PendingBlockDrops");
            if (version < 6) document.AsObject().Remove("TerrainPlacements");
            if (version < 5)
            {
                document.AsObject().Remove("Liquids");
                document.AsObject().Remove("LiquidInventory");
            }
            if (version < 4) document.AsObject().Remove("DamagedTerrain");
            if (version < 3) document.AsObject().Remove("Inventory");
            if (version >= 5)
            {
                // These versions genuinely stored 50 HP per obsidian unit.
                // Template migration must preserve its 75% remaining durability.
                document["Liquids"]!["Cells"] = JsonNode.Parse("""
                    [{"Cell":{"X":8,"Y":16,"Z":8},"Water":0,"Mercury":0,"Lava":0,"ObsidianUnits":2,"CrustFace":"West"}]
                    """);
                document["Liquids"]!["Obsidian"] = JsonNode.Parse("""
                    [{"Cell":{"X":8,"Y":16,"Z":8},"Units":2,"Face":"West","Hp":75}]
                    """);
            }
            var legacy = document.ToJsonString();
            File.WriteAllText(path, legacy);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);

            Assert.NotNull(restored);
            Assert.Equal(1017, restored.Economy.Currency);
            Assert.Empty(restored.UpgradeTemplate.Points);
            Assert.False(restored.UpgradeTemplate.UseJumpPattern);
            Assert.Equal(new UpgradeTemplate().JumpPattern, restored.UpgradeTemplate.JumpPattern);
            if (version >= 5) Assert.Equal(7.5f, Assert.Single(restored.World.ObsidianCells).Hp);
            Assert.Equal(legacy, File.ReadAllText(path));
            Assert.False(File.Exists(path + $".v{version}.bak"));
            restored.UpgradeTemplate.Points[UpgradeKind.ATK] = 4;
            SaveStore.Save(restored, path);
            Assert.Equal(legacy, File.ReadAllText(path + $".v{version}.bak"));
            Assert.Equal(8, JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>());
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.Equal(4, reloaded!.UpgradeTemplate.Points[UpgradeKind.ATK]);
            if (version >= 5) Assert.Equal(7.5f, Assert.Single(reloaded.World.ObsidianCells).Hp);
        });
    }

    [Theory]
    [InlineData("missing template")]
    [InlineData("null template")]
    [InlineData("missing points")]
    [InlineData("null points")]
    [InlineData("negative count")]
    [InlineData("unknown kind")]
    [InlineData("numeric unknown kind")]
    [InlineData("missing flag")]
    [InlineData("missing pattern")]
    [InlineData("null pattern")]
    [InlineData("empty pattern")]
    [InlineData("duplicate offset")]
    [InlineData("zero offset")]
    [InlineData("near offset")]
    [InlineData("unsafe offset")]
    [InlineData("legacy template")]
    public void InvalidTemplatesAreRejectedWithoutRewritingTheCheckpoint(string defect)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var template = document["UpgradeTemplate"]!;
            switch (defect)
            {
                case "missing template": document.AsObject().Remove("UpgradeTemplate"); break;
                case "null template": document["UpgradeTemplate"] = null; break;
                case "missing points": template.AsObject().Remove("Points"); break;
                case "null points": template["Points"] = null; break;
                case "negative count": template["Points"]!["HP"] = -1; break;
                case "unknown kind": template["Points"]!["Unknown"] = 1; break;
                case "numeric unknown kind": template["Points"]!["100"] = 1; break;
                case "missing flag": template.AsObject().Remove("UseJumpPattern"); break;
                case "missing pattern": template.AsObject().Remove("JumpPattern"); break;
                case "null pattern": template["JumpPattern"] = null; break;
                case "empty pattern": template["JumpPattern"] = new JsonArray(); break;
                case "duplicate offset": template["JumpPattern"]!.AsArray().Add(template["JumpPattern"]![0]!.DeepClone()); break;
                case "zero offset": template["JumpPattern"] = Pattern(0, 0, 0); break;
                case "near offset": template["JumpPattern"] = Pattern(1, -1, 1); break;
                case "unsafe offset": template["JumpPattern"] = Pattern(int.MinValue, 0, 0); break;
                case "legacy template": document["Version"] = 6; break;
            }
            var invalid = document.ToJsonString();
            File.WriteAllText(path, invalid);

            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    [Fact]
    public void SavingTemplateDuringCombatKeepsThePreparedBattlefieldAndEconomy()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
            SaveStore.Save(game, path);
            Assert.True(game.StartLevel());
            game.Economy.Credit(123);
            Assert.Single(game.Turrets).Hp = 2;
            game.UpgradeTemplate.Points[UpgradeKind.HP] = 17;
            game.UpgradeTemplate.UseJumpPattern = true;
            game.UpgradeTemplate.JumpPattern = [new(5, 0, 1)];

            Assert.True(SaveStore.TrySaveUpgradeTemplate(game.UpgradeTemplate, path, out var error), error);
            Assert.True(SaveStore.TryLoad(path, out var prepared, out error), error);

            Assert.NotNull(prepared);
            Assert.Equal(GamePhase.Building, prepared.Phase);
            Assert.Equal(999, prepared.Economy.Currency);
            Assert.Equal(30, Assert.Single(prepared.Turrets).Hp);
            Assert.Equal(17, prepared.UpgradeTemplate.Points[UpgradeKind.HP]);
            Assert.True(prepared.UpgradeTemplate.UseJumpPattern);
            Assert.Equal(new Cell(5, 0, 1), Assert.Single(prepared.UpgradeTemplate.JumpPattern));
            Assert.Equal(GamePhase.Combat, game.Phase);
            Assert.Equal(1122, game.Economy.Currency);
            Assert.Equal(2, Assert.Single(game.Turrets).Hp);
            game.UpgradeTemplate.JumpPattern.Clear();
            game.UpgradeTemplate.Points[UpgradeKind.HP] = 2;
            Assert.Equal(17, prepared.UpgradeTemplate.Points[UpgradeKind.HP]);
            Assert.Single(prepared.UpgradeTemplate.JumpPattern);
        });
    }

    [Fact]
    public void TemplateOnlySavePreservesMissingCorruptAndInvalidCheckpoints()
    {
        WithSavePath(path =>
        {
            var template = new UpgradeTemplate();
            Assert.False(SaveStore.TrySaveUpgradeTemplate(template, path, out var missingError));
            Assert.False(string.IsNullOrWhiteSpace(missingError));
            Assert.False(File.Exists(path));
            const string corrupt = "{ broken";
            File.WriteAllText(path, corrupt);
            Assert.False(SaveStore.TrySaveUpgradeTemplate(template, path, out var corruptError));
            Assert.False(string.IsNullOrWhiteSpace(corruptError));
            Assert.Equal(corrupt, File.ReadAllText(path));

            File.Delete(path);
            SaveStore.Save(new GameSession(), path);
            var before = File.ReadAllText(path);
            template.Points[UpgradeKind.HP] = -1;
            Assert.False(SaveStore.TrySaveUpgradeTemplate(template, path, out var invalidError));
            Assert.False(string.IsNullOrWhiteSpace(invalidError));
            Assert.Equal(before, File.ReadAllText(path));
        });
    }

    private static JsonArray Pattern(int x, int y, int z) => new(new JsonObject { ["X"] = x, ["Y"] = y, ["Z"] = z });

    private static void WithSavePath(Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "ChessVsZombiesTemplate-" + Guid.NewGuid().ToString("N") + ".json");
        try { test(path); }
        finally
        {
            File.Delete(path);
            for (var version = 1; version <= 7; version++) File.Delete(path + $".v{version}.bak");
        }
    }
}
