using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ObsidianPairLiquidTests
{
    [Theory]
    [InlineData(CrustFace.West)]
    [InlineData(CrustFace.East)]
    [InlineData(CrustFace.North)]
    [InlineData(CrustFace.South)]
    public void LiquidsFillTheOpeningBetweenBothLeavesAndRespectTheirCombinedCapacity(CrustFace face)
    {
        var cell = new Cell(8, 16, 8);
        var liquids = new LiquidSystem(new WorldGrid());
        liquids.RestoreState(new LiquidState
        {
            Cells = [new(cell, 1, 2, 0, 5, face)],
            Obsidian = [new(cell, 5, face, 20, OppositeUnits: 2, OppositeHp: 7)]
        });
        Assert.False(liquids.TryAdd(cell, LiquidKind.Water, 3));
        Assert.True(liquids.TryAdd(cell, LiquidKind.Water, 2));
        Assert.False(liquids.TryAdd(cell, LiquidKind.Water, 1));

        var volumes = liquids.GetVolumes(cell).ToArray();
        Assert.Equal(2, volumes.Length);
        Assert.Equal(16.4f, volumes[0].Max.Y, 4);
        Assert.Equal(17, volumes[1].Max.Y, 4);
        foreach (var volume in volumes)
        {
            Assert.Equal(.5f, (volume.Max.X - volume.Min.X) * (volume.Max.Z - volume.Min.Z), 4);
            Assert.DoesNotContain(liquids.ExportState().Obsidian[0].Parts, part => Overlap(volume, part));
        }
        Assert.Equal(LiquidKind.Water, liquids.GetLiquidAt(cell.Center));
    }

    [Theory]
    [InlineData(2, 3, 0)]
    [InlineData(2, 3, 2)]
    [InlineData(2, 8, 0)]
    public void WetLeafMergesPreserveBothCellsLiquidAndDisplaceOnlyTheOverflow(int falling, int lower, int retainedUpper)
    {
        var world = new WorldGrid();
        var liquids = new LiquidSystem(world);
        var bottom = new Cell(8, 16, 8);
        var top = bottom + new Cell(0, 1, 0);
        var topCrust = retainedUpper == 0
            ? new ObsidianCell(top, falling, CrustFace.East, falling * 5)
            : new ObsidianCell(top, falling + retainedUpper, CrustFace.West,
                (falling + retainedUpper) * 5, OppositeUnits: falling, OppositeHp: falling * 5);
        liquids.RestoreState(new LiquidState
        {
            Cells = [new(top, 10 - falling - retainedUpper, 0, 0, topCrust.Units, topCrust.Face),
                new(bottom, 0, 10 - lower, 0, lower, CrustFace.West)],
            Obsidian = [topCrust, new(bottom, lower, CrustFace.West, lower * 5)]
        });
        var beforeWater = Total(liquids, LiquidKind.Water);
        var beforeMercury = Total(liquids, LiquidKind.Mercury);
        var moved = topCrust.Parts.Single(part => part.Face == CrustFace.East);
        Assert.True((bool)typeof(WorldGrid).GetMethod("MoveObsidianPart", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(world, [moved, bottom])!);
        typeof(LiquidSystem).GetMethod("DisplaceForObsidianMove", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(liquids, [top, bottom]);

        Assert.Equal(beforeWater, Total(liquids, LiquidKind.Water));
        Assert.Equal(beforeMercury, Total(liquids, LiquidKind.Mercury));
        Assert.Equal(beforeWater, liquids.GetUnits(top, LiquidKind.Water));
        Assert.Equal(falling, liquids.GetUnits(top, LiquidKind.Mercury));
        Assert.Equal(10 - lower - falling, liquids.GetTotalUnits(bottom));
        Assert.Equal(10 - retainedUpper, liquids.GetTotalUnits(top));
        Assert.All(liquids.Cells, liquid => Assert.InRange(
            liquid.Water + liquid.Mercury + liquid.Lava + liquid.ObsidianUnits, 0, 10));
        var restored = new LiquidSystem(new WorldGrid());
        restored.RestoreState(liquids.ExportState());
        Assert.Equal(JsonSerializer.Serialize(liquids.ExportState()), JsonSerializer.Serialize(restored.ExportState()));
    }

    [Theory]
    [InlineData(CrustFace.West)]
    [InlineData(CrustFace.East)]
    [InlineData(CrustFace.North)]
    [InlineData(CrustFace.South)]
    public void BothPairFacesReactSymmetricallyWithoutLosingEitherLeaf(CrustFace primary)
    {
        var world = new WorldGrid();
        var center = new Cell(8, 5, 8);
        var direction = primary is CrustFace.West or CrustFace.East ? new Cell(1, 0, 0) : new Cell(0, 0, 1);
        var west = center - direction;
        var east = center + direction;
        foreach (var cell in new[] { west, center, east })
            Assert.True((bool)typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(world, [cell])!);
        var liquids = new LiquidSystem(world);
        liquids.RestoreState(new LiquidState
        {
            Cells = [new(center, 0, 0, 6, 4, primary), new(west, 1, 0, 0), new(east, 1, 0, 0)],
            Obsidian = [new(center, 4, primary, 20, OppositeUnits: 2, OppositeHp: 10)]
        });

        liquids.Update(.1f);

        Assert.Equal(new ObsidianCell(center, 6, primary, 30, OppositeUnits: 3, OppositeHp: 15), world.GetObsidian(center));
        Assert.Equal(4, liquids.GetUnits(center, LiquidKind.Lava));
        Assert.Equal(0, liquids.GetUnits(west, LiquidKind.Water));
        Assert.Equal(0, liquids.GetUnits(east, LiquidKind.Water));
        Assert.Equal(2, liquids.Hazards.Count());
        Assert.Contains(liquids.Hazards, cloud => cloud.Center == west);
        Assert.Contains(liquids.Hazards, cloud => cloud.Center == east);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("missing-leaf")]
    [InlineData("empty-primary")]
    [InlineData("overlapping")]
    [InlineData("horizontal")]
    [InlineData("offset")]
    [InlineData("primary-hp")]
    [InlineData("secondary-hp")]
    [InlineData("zero-secondary-hp")]
    [InlineData("nonfinite")]
    public void InvalidPairSnapshotsAreRejectedBeforeChangingExistingWorldAndLiquids(string defect)
    {
        var cell = new Cell(8, 16, 8);
        var world = new WorldGrid();
        var liquids = new LiquidSystem(world);
        liquids.RestoreState(new LiquidState { Obsidian = [new(cell, 1, CrustFace.Bottom, 3)] });
        Assert.True(liquids.TryAdd(cell, LiquidKind.Water, 3));
        var before = JsonSerializer.Serialize(liquids.ExportState());
        var revision = world.Revision;
        var valid = new ObsidianCell(cell, 5, CrustFace.West, 20, OppositeUnits: 2, OppositeHp: 8);
        var invalid = defect switch
        {
            "negative" => valid with { OppositeUnits = -1 },
            "missing-leaf" => valid with { OppositeUnits = 0 },
            "empty-primary" => valid with { OppositeUnits = 5 },
            "overlapping" => valid with { Units = 11 },
            "horizontal" => valid with { Face = CrustFace.Bottom },
            "offset" => valid with { OffsetUnits = 1 },
            "primary-hp" => valid with { Hp = 25, OppositeHp = 5 },
            "secondary-hp" => valid with { OppositeHp = 11 },
            "zero-secondary-hp" => valid with { OppositeHp = 0 },
            _ => valid with { OppositeHp = float.NaN }
        };

        Assert.Throws<InvalidDataException>(() => liquids.RestoreState(new LiquidState { Obsidian = [invalid] }));

        Assert.Equal(revision, world.Revision);
        Assert.Equal(before, JsonSerializer.Serialize(liquids.ExportState()));
    }

    [Fact]
    public void PairedCheckpointRetainsBothLeafShapesDurabilityAndLiquidWhileOlderSinglesDefaultToNoPair()
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            var cell = new Cell(8, 16, 8);
            var singleCell = cell + new Cell(0, 0, 2);
            var pair = new ObsidianCell(cell, 5, CrustFace.West, 20, OppositeUnits: 2, OppositeHp: 8);
            var single = new ObsidianCell(singleCell, 2, CrustFace.Bottom, 9);
            game.Liquids.RestoreState(new LiquidState
            {
                Cells = [new(cell, 3, 2, 0, 5, pair.Face)], Obsidian = [pair, single]
            });
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var savedSingle = document["Liquids"]!["Obsidian"]!.AsArray()[1]!.AsObject();
            savedSingle.Remove("OppositeUnits");
            savedSingle.Remove("OppositeHp");
            File.WriteAllText(path, document.ToJsonString());

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);

            Assert.Equal(pair, restored!.World.GetObsidian(cell));
            Assert.Equal(pair.Parts.ToArray(), restored.World.GetObsidian(cell)!.Parts.ToArray());
            Assert.Equal(single, restored.World.GetObsidian(singleCell));
            Assert.Equal(3, restored.Liquids.GetUnits(cell, LiquidKind.Water));
            Assert.Equal(2, restored.Liquids.GetUnits(cell, LiquidKind.Mercury));
        });
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void LegacyDurabilityMigrationRejectsPairFieldsWithoutChangingTheCheckpoint(int version)
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            var cell = new Cell(8, 16, 8);
            game.Liquids.RestoreState(new LiquidState
            {
                Obsidian = [new(cell, 5, CrustFace.West, 20, OppositeUnits: 2, OppositeHp: 8)]
            });
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["Version"] = version;
            document.AsObject().Remove("UpgradeTemplate");
            document.AsObject().Remove("PendingBlockDrops");
            document["Inventory"]!.AsObject().Remove("AdditionalMaterials");
            if (version == 5) document.AsObject().Remove("TerrainPlacements");
            string invalid = document.ToJsonString();
            File.WriteAllText(path, invalid);

            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));

            Assert.Null(restored);
            Assert.Contains("legacy obsidian", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    private static int Total(LiquidSystem liquids, LiquidKind kind) => liquids.Cells.Sum(cell => liquids.GetUnits(cell.Cell, kind));
    private static bool Overlap(LiquidVolume liquid, ObsidianCell crust) =>
        MathF.Min(liquid.Max.X, crust.Max.X) - MathF.Max(liquid.Min.X, crust.Min.X) > .00001f &&
        MathF.Min(liquid.Max.Y, crust.Max.Y) - MathF.Max(liquid.Min.Y, crust.Min.Y) > .00001f &&
        MathF.Min(liquid.Max.Z, crust.Max.Z) - MathF.Max(liquid.Min.Z, crust.Min.Z) > .00001f;

    private static void WithSavePath(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "chess-zombies-obsidian-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(Path.Combine(directory, "campaign.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
