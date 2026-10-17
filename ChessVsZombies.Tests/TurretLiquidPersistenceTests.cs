using System.Text.Json.Nodes;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class TurretLiquidPersistenceTests
{
    [Theory]
    [InlineData(5, 0)]
    [InlineData(2, 3)]
    [InlineData(0, 5)]
    public void WetAndEncasedTurretsRoundTripWithoutChangingCheckpointVersion(int waterUnits, int obsidianUnits)
    {
        WithSavePath(path =>
        {
            var original = CreateTurret();
            var turret = Assert.Single(original.Turrets);
            var face = obsidianUnits == 0 ? (CrustFace?)null : CrustFace.West;
            original.Liquids.RestoreState(new LiquidState
            {
                Cells = waterUnits == 0 ? [] : [new(turret.Cell, waterUnits, 0, 0, obsidianUnits, face)],
                Obsidian = obsidianUnits == 0 ? [] :
                    [new(turret.Cell, obsidianUnits, CrustFace.West, obsidianUnits * LiquidSystem.ObsidianHpPerUnit - 1)]
            });

            SaveStore.Save(original, path);

            Assert.Equal(9, JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>());
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            var restoredTurret = Assert.Single(restored.Turrets);
            Assert.Equal(turret.Id, restoredTurret.Id);
            Assert.Equal(turret.Cell, restoredTurret.Cell);
            Assert.Equal(turret.Hp, restoredTurret.Hp);
            Assert.Equal(waterUnits, restored.Liquids.GetUnits(turret.Cell, LiquidKind.Water));
            Assert.Equal(original.World.GetObsidian(turret.Cell), restored.World.GetObsidian(turret.Cell));

            // The restored living piece must keep its reduced liquid capacity.
            Assert.False(restored.Liquids.TryAdd(turret.Cell, LiquidKind.Water, 1));
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var again, out error), error);
            Assert.Equal(waterUnits, again!.Liquids.GetTotalUnits(turret.Cell));
            Assert.Equal(original.World.GetObsidian(turret.Cell), again.World.GetObsidian(turret.Cell));
        });
    }

    [Fact]
    public void OpposingObsidianShellLeavesPreserveTheirIndividualDurability()
    {
        WithSavePath(path =>
        {
            var game = CreateTurret();
            var cell = Assert.Single(game.Turrets).Cell;
            var shell = new ObsidianCell(cell, 5, CrustFace.West, 19, OppositeUnits: 2, OppositeHp: 7);
            game.Liquids.RestoreState(new LiquidState { Obsidian = [shell] });

            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);

            var restoredShell = restored!.World.GetObsidian(cell);
            Assert.Equal(shell, restoredShell);
            Assert.Equal(shell.Parts, restoredShell!.Parts);
            Assert.Single(restored.Turrets);
        });
    }

    [Theory]
    [InlineData("too-much-liquid")]
    [InlineData("combined-liquid-and-shell-overflow")]
    [InlineData("full-obsidian-block")]
    [InlineData("natural-terrain")]
    [InlineData("placed-terrain")]
    [InlineData("barrier")]
    public void TurretsCannotRestoreInsideExcessVolumeOrFullSolidBlocks(string fault)
    {
        WithSavePath(path =>
        {
            var game = CreateTurret();
            var cell = Assert.Single(game.Turrets).Cell;
            Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Water, 5));
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var liquids = document["Liquids"]!;
            var liquid = liquids["Cells"]![0]!;
            switch (fault)
            {
                case "too-much-liquid":
                    liquid["Water"] = 6;
                    break;
                case "combined-liquid-and-shell-overflow":
                    AddShell(liquids, liquid, cell, 3, 3);
                    break;
                case "full-obsidian-block":
                    AddShell(liquids, liquid, cell, 10, 0);
                    break;
                case "natural-terrain":
                    document["Turrets"]![0]!["Cell"]!["Y"] = 15;
                    break;
                case "placed-terrain":
                    liquids["Cells"]!.AsArray().Clear();
                    document["TerrainPlacements"]!.AsArray().Add(new JsonObject
                    {
                        ["Cell"] = CellNode(cell), ["Material"] = "Stone"
                    });
                    break;
                case "barrier":
                    liquids["Cells"]!.AsArray().Clear();
                    document["Barriers"]!.AsArray().Add(new JsonObject
                    {
                        ["Cell"] = CellNode(cell), ["Content"] = "Dirt", ["Hp"] = 10
                    });
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(fault));
            }
            string invalidCheckpoint = document.ToJsonString();
            File.WriteAllText(path, invalidCheckpoint);

            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalidCheckpoint, File.ReadAllText(path));
        });
    }

    private static void AddShell(JsonNode liquids, JsonNode liquid, Cell cell, int units, int waterUnits)
    {
        liquid["Water"] = waterUnits;
        liquid["ObsidianUnits"] = units;
        liquid["CrustFace"] = "West";
        liquids["Obsidian"]!.AsArray().Add(new JsonObject
        {
            ["Cell"] = CellNode(cell), ["Units"] = units, ["Face"] = "West",
            ["Hp"] = units * LiquidSystem.ObsidianHpPerUnit
        });
    }

    private static JsonObject CellNode(Cell cell) => new() { ["X"] = cell.X, ["Y"] = cell.Y, ["Z"] = cell.Z };

    private static GameSession CreateTurret()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)), game.Message);
        return game;
    }

    private static void WithSavePath(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-turret-liquid-{Guid.NewGuid():N}.json");
        try { action(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
