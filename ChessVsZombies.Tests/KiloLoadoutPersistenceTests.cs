using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloLoadoutPersistenceTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public void ReturningKiloRetainsTwoBucketsAlongsideItsTwoBlockLoadout(int backpackBlocks, int heldBlocks)
    {
        WithSavePath(path =>
        {
            var saved = ReturningKilo(backpackBlocks, heldBlocks);
            WriteReturningKilo(path, saved);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            var queued = Assert.Single(restored!.ExportState().ReturningZombies!);
            Assert.True(queued.CarriesBucket);
            Assert.Equal(backpackBlocks, queued.CarriedBlocks);
            Assert.Equal(heldBlocks, queued.HeldBlocks.Count);
            Assert.Equal(4, queued.Buckets[0].Units);
            Assert.Equal(LiquidKind.Water, queued.Buckets[0].Kind);
            Assert.Equal(8, queued.Buckets[1].Units);
            Assert.Equal(LiquidKind.Mercury, queued.Buckets[1].Kind);

            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.True(reloaded!.StartLevel());
            Assert.True((bool)Invoke(reloaded, "Spawn", reloaded.Waves[0])!);
            var kilo = Assert.Single(reloaded.Zombies);
            Assert.True(kilo.CarriesBucket);
            Assert.Equal(backpackBlocks, kilo.CarriedBlocks);
            Assert.Equal(12, kilo.HeldBucketUnits);
            Assert.Equal(heldBlocks, reloaded.KiloCovers.Count);
            Assert.Equal(saved.HeldBlocks.Select(block => block.Hp), reloaded.KiloCovers.Select(block => block.Hp));
        });
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public void LegacyBlockCarryingKiloGetsEmptyBucketsWithoutLosingAnyBlock(int backpackBlocks, int heldBlocks)
    {
        WithSavePath(path =>
        {
            var saved = ReturningKilo(backpackBlocks, heldBlocks);
            saved.CarriesBucket = false;
            saved.Buckets.Clear();
            WriteReturningKilo(path, saved);
            string original = File.ReadAllText(path);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.Equal(original, File.ReadAllText(path));
            var queued = Assert.Single(restored!.ExportState().ReturningZombies!);
            Assert.True(queued.CarriesBucket);
            Assert.Equal(backpackBlocks, queued.CarriedBlocks);
            Assert.Equal(heldBlocks, queued.HeldBlocks.Count);
            Assert.Equal(2, queued.Buckets.Count);
            Assert.All(queued.Buckets, bucket => { Assert.Equal(0, bucket.Units); Assert.Null(bucket.Kind); });

            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.True(reloaded!.StartLevel());
            Assert.True((bool)Invoke(reloaded, "Spawn", reloaded.Waves[0])!);
            var kilo = Assert.Single(reloaded.Zombies);
            Assert.True(kilo.CarriesBucket);
            Assert.Equal(backpackBlocks, kilo.CarriedBlocks);
            Assert.Equal(heldBlocks, reloaded.KiloCovers.Count);
            Assert.Equal(0, kilo.HeldBucketUnits);
        });
    }

    [Theory]
    [InlineData(ZombieSize.Milli)]
    [InlineData(ZombieSize.Standard)]
    public void SimultaneousBucketAndBlockStorageRemainsExclusiveToKilos(ZombieSize size)
    {
        WithSavePath(path =>
        {
            var saved = ReturningKilo(1, 0);
            saved.Size = size;
            saved.IsBoss = false;
            saved.BodyHp = size == ZombieSize.Milli ? 5 : 10;
            saved.Buckets.RemoveAt(1);
            WriteReturningKilo(path, saved);
            string original = File.ReadAllText(path);

            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.NotEmpty(error);
            Assert.Equal(original, File.ReadAllText(path));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ResupplyKeepsKiloBucketsAndExistingCoversAndOnlyRefillsMissingBlockSlots(int heldBlocks)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition
        {
            Waves = [new() { Groups = [new() { Kind = ZombieKind.Armored, Tier = GearTier.Iron }] }]
        });
        var game = new GameSession(levels: catalog);
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        game.Waves[0].BudgetSpent = 10;
        var kilo = new Zombie { Id = 500, Size = ZombieSize.Kilo, Kind = ZombieKind.Basic, Attack = 1 };
        kilo.Position = kilo.StandAt(new Cell(5, 16, 8));
        kilo.FaceToward(Vector3.UnitX);
        game.Zombies.Add(kilo);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.Equal(2, game.KiloCovers.Count);
        for (int index = heldBlocks; index < 2; index++) Invoke(game, "DamageKiloCover", kilo.Id, index, 100000f);
        if (heldBlocks > 0) Invoke(game, "DamageKiloCover", kilo.Id, 0, 7.5f);
        var expected = game.KiloCovers.Select(cover => (cover.BlockIndex, cover.Material, cover.Hp)).ToArray();
        Set(kilo.BucketLoads[0], nameof(ZombieBucketLoad.Units), 4);
        Set(kilo.BucketLoads[0], nameof(ZombieBucketLoad.Kind), LiquidKind.Water);
        Set(kilo.BucketLoads[1], nameof(ZombieBucketLoad.Units), 8);
        Set(kilo.BucketLoads[1], nameof(ZombieBucketLoad.Kind), LiquidKind.Mercury);
        game.DamageZombie(kilo, 3, DamageKind.Chemical);
        float bodyHp = kilo.BodyHp;
        kilo.Position = kilo.StandAt(new Cell(0, 16, 8));
        Invoke(game, "BeginZombieRetreat", kilo);

        Assert.True((bool)Invoke(game, "TryCompleteRetreat", kilo, 1.1f)!);

        Assert.False(game.IsZombieRetreating(kilo));
        Assert.True(kilo.CarriesBucket);
        Assert.Equal(2 - heldBlocks, kilo.CarriedBlocks);
        Assert.Equal(expected, game.KiloCovers.Select(cover => (cover.BlockIndex, cover.Material, cover.Hp)).ToArray());
        Assert.Equal(4, kilo.BucketLoads[0].Units);
        Assert.Equal(LiquidKind.Water, kilo.BucketLoads[0].Kind);
        Assert.Equal(8, kilo.BucketLoads[1].Units);
        Assert.Equal(LiquidKind.Mercury, kilo.BucketLoads[1].Kind);
        Assert.Equal(bodyHp, kilo.BodyHp);
        Assert.Equal(10, game.Waves[0].BudgetSpent);
        Assert.Equal(1, kilo.RetreatReturns);
        Assert.Empty(game.Buckets.Flights);
        Assert.Empty(game.Buckets.PendingSplashes);
        Assert.Empty(game.Liquids.Cells);
    }

    private static SavedReturningZombie ReturningKilo(int backpackBlocks, int heldBlocks) => new()
    {
        TargetLevel = 1, TargetWaveIndex = 0, Id = 500, Kind = ZombieKind.Basic, Tier = GearTier.Wood,
        Size = ZombieSize.Kilo, IsBoss = true, BodyHp = 37, RetreatReturns = 3, OriginalBudgetCost = 10,
        CarriesBucket = true, CarriedBlocks = backpackBlocks,
        Buckets = [new() { Units = 4, Kind = LiquidKind.Water }, new() { Units = 8, Kind = LiquidKind.Mercury }],
        HeldBlocks = Enumerable.Range(0, heldBlocks).Select(index => new SavedKiloBlock
        {
            BlockIndex = index, Material = index == 0 ? Material.Dirt : Material.Osmium,
            Hp = index == 0 ? 62.5f : 7000.25f, OffsetX = index == 0 ? -.5f : .5f, OffsetZ = .5f
        }).ToList()
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static void WriteReturningKilo(string path, SavedReturningZombie saved)
    {
        SaveStore.Save(new GameSession(), path);
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["ReturningZombies"] = new JsonArray(JsonSerializer.SerializeToNode(saved, JsonOptions));
        File.WriteAllText(path, document.ToJsonString());
    }

    private static object? Invoke(object target, string name, params object?[] arguments) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static void WithSavePath(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-kilo-loadout-{Guid.NewGuid():N}.json");
        try { action(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
