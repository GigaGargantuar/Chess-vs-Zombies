using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GigaPersistenceTests
{
    [Fact]
    public void DeferredGigaPreservesEveryCounterPartialRecoveryAndTargetAcrossSaveLoadAndReturn()
    {
        var (game, catalog, original, targetId) = DeferredGiga();
        var saved = Assert.Single(game.ExportState().ReturningZombies!);
        Assert.Equal(original.BodyHp, saved.BodyHp);
        Assert.Equal(original.Giga!.HpCounter, saved.Giga!.HpCounter);
        Assert.Equal(original.Giga.ResCounter, saved.Giga.ResCounter);
        Assert.Equal(original.Giga.AttackCounter, saved.Giga.AttackCounter);
        Assert.Equal(original.Giga.RecoverySeconds, saved.Giga.RecoverySeconds);
        Assert.Equal(original.Giga.SpawnCost, saved.OriginalBudgetCost);
        Assert.Equal(original.Giga.SpawnCost, game.PendingReinforcementBudget);
        Assert.Equal(0, game.PendingBossReinforcementBudget);
        Assert.Equal(targetId, saved.Giga.LockedTargetId);
        Assert.Empty(TargetLocks(game));

        WithSavePath(path =>
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error, catalog), error);
            SaveStore.Save(restored!, path);
            Assert.True(SaveStore.TryLoad(path, out restored, out error, catalog), error);
            Assert.Equal(original.Giga.SpawnCost, restored!.PendingReinforcementBudget);
            Assert.Equal(0, restored.PendingBossReinforcementBudget);
            var native = WaveSpawnPlanner.Plan(Assert.Single(catalog.Get(2).Waves));
            Assert.True(restored!.StartLevel());
            var wave = Assert.Single(restored.Waves);
            Assert.Equal(native.HpBudget + original.Giga.SpawnCost, wave.HpBudget);
            Assert.Equal(native.NormalBudget + original.Giga.SpawnCost, wave.NormalBudget);
            Assert.Equal(native.BossBudget, wave.BossBudget);
            Assert.Equal(0, wave.BudgetSpent);
            Assert.True((bool)Invoke(restored, "Spawn", restored.Waves[0])!);

            var returned = Assert.Single(restored.Zombies);
            Assert.True(returned.IsGiga);
            Assert.Equal(original.BodyHp, returned.BodyHp);
            Assert.Equal(original.MaxHp, returned.MaxHp);
            Assert.Equal(original.Giga.InitialCounter, returned.Giga!.InitialCounter);
            Assert.Equal(original.Giga.HpCounter, returned.Giga.HpCounter);
            Assert.Equal(original.Giga.ResCounter, returned.Giga.ResCounter);
            Assert.Equal(original.Giga.AttackCounter, returned.Giga.AttackCounter);
            Assert.Equal(original.Giga.RecoverySeconds, returned.Giga.RecoverySeconds);
            Assert.Equal(targetId, TargetLocks(restored)[returned.Id]);
            Assert.Equal(original.Giga.SpawnCost, returned.OriginalBudgetCost);
            Assert.Equal(original.Giga.SpawnCost, wave.BudgetSpent);
            Assert.Equal(native.BossBudget, wave.BossBudget);
            Assert.Empty(returned.ArmorPieces);
            Assert.False(returned.CarriesBucket);
            Assert.Equal(0, returned.CarriedBlocks);
            Assert.Equal(0, restored.PendingReinforcementCount);
        });
    }

    [Fact]
    public void MissingOriginalCostFallsBackToInitialCounterHpAndRetainsThatFullCostAfterGrowthSaveAndReturn()
    {
        var (game, catalog, original, _) = DeferredGiga(initialCounter: 14, hpCounter: 18, omitOriginalCost: true);
        long initialCost = GigaRules.SpawnCost(14);
        Assert.Equal(0, original.OriginalBudgetCost);
        Assert.True(initialCost > GigaRules.SpawnCost(10));
        Assert.NotEqual(GigaRules.SpawnCost(18), initialCost);
        var saved = Assert.Single(game.ExportState().ReturningZombies!);
        Assert.Equal(14, saved.Giga!.InitialCounter);
        Assert.Equal(18, saved.Giga.HpCounter);
        Assert.Equal(initialCost, saved.OriginalBudgetCost);
        Assert.Equal(initialCost, game.PendingReinforcementBudget);
        Assert.Equal(0, game.PendingBossReinforcementBudget);

        WithSavePath(path =>
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error, catalog), error);
            Assert.Equal(initialCost, Assert.Single(restored!.ExportState().ReturningZombies!).OriginalBudgetCost);
            Assert.Equal(initialCost, restored.PendingReinforcementBudget);
            Assert.Equal(0, restored.PendingBossReinforcementBudget);
            var native = WaveSpawnPlanner.Plan(Assert.Single(catalog.Get(2).Waves));
            Assert.True(restored.StartLevel());
            var wave = Assert.Single(restored.Waves);
            Assert.Equal(native.HpBudget + initialCost, wave.HpBudget);
            Assert.Equal(native.NormalBudget + initialCost, wave.NormalBudget);
            Assert.Equal(native.BossBudget, wave.BossBudget);
            Assert.True((bool)Invoke(restored, "Spawn", wave)!);
            var returned = Assert.Single(restored.Zombies);
            Assert.Equal(14, returned.Giga!.InitialCounter);
            Assert.Equal(18, returned.Giga.HpCounter);
            Assert.Equal(original.BodyHp, returned.BodyHp);
            Assert.Equal(initialCost, returned.OriginalBudgetCost);
            Assert.Equal(initialCost, wave.BudgetSpent);
            Assert.Equal(native.BossBudget, wave.BossBudget);
        });
    }

    [Fact]
    public void ReturningGigaWaitsUntilAnyExistingGigaHasDiedWithoutSpendingItsBudget()
    {
        var (game, _, _, _) = DeferredGiga();
        Assert.True(game.StartLevel());
        var active = new Zombie { Id = 8000, Size = ZombieSize.Giga, WaveIndex = -1 };
        active.Position = active.StandAt(new Cell(10, 16, 10));
        game.Zombies.Add(active);
        var wave = game.Waves[0];
        long spent = wave.BudgetSpent;
        int spawned = wave.Spawned;
        object?[] arguments = [wave, false];

        Assert.False((bool)Invoke(game, "TrySpawnReinforcement", arguments)!);
        Assert.Equal(true, arguments[1]);
        Assert.Equal(1, game.PendingReinforcementCount);
        Assert.Equal(spent, wave.BudgetSpent);
        Assert.Equal(spawned, wave.Spawned);

        active.Hp = 0;
        Assert.True((bool)Invoke(game, "TrySpawnReinforcement", wave, false)!);
        Assert.Single(game.Zombies, zombie => zombie.IsGiga && zombie.Hp > 0);
        Assert.Equal(0, game.PendingReinforcementCount);
    }

    [Fact]
    public void ExportedGigaStateIsDetachedFromThePendingCampaignQueue()
    {
        var (game, _, _, _) = DeferredGiga();
        string expected = JsonSerializer.Serialize(game.ExportState().ReturningZombies);
        var state = Assert.Single(game.ExportState().ReturningZombies!).Giga!;
        state.HpCounter = 100;
        state.ResCounter = 1;
        state.AttackCounter = 30;
        state.RecoverySeconds = 0;
        state.LockedTargetId = null;
        Assert.Equal(expected, JsonSerializer.Serialize(game.ExportState().ReturningZombies));
    }

    [Theory]
    [InlineData("missing-state")]
    [InlineData("ordinary-with-state")]
    [InlineData("initial-below-minimum")]
    [InlineData("hp-before-initial")]
    [InlineData("maximum-hp-counter")]
    [InlineData("zero-res")]
    [InlineData("maximum-res-counter")]
    [InlineData("zero-attack")]
    [InlineData("overflowing-attack")]
    [InlineData("negative-timer")]
    [InlineData("full-second-timer")]
    [InlineData("infinite-timer")]
    [InlineData("zero-target")]
    [InlineData("self-target")]
    [InlineData("wrong-spawn-cost")]
    [InlineData("discounted-kilo-cost")]
    [InlineData("discounted-mega-cost")]
    [InlineData("grown-hp-spawn-cost")]
    [InlineData("body-above-grown-maximum")]
    [InlineData("armor")]
    [InlineData("bucket-tool")]
    [InlineData("bucket-payload")]
    [InlineData("carried-block")]
    [InlineData("held-block")]
    [InlineData("missing-counter")]
    public void MalformedGigaCheckpointIsRejectedBeforeRestoreAndLeavesTheFileIntact(string fault)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var saved = JsonSerializer.SerializeToNode(ValidSavedGiga(), JsonOptions)!.AsObject();
            var giga = saved["Giga"]!.AsObject();
            document["ReturningZombies"] = new JsonArray(saved);
            switch (fault)
            {
                case "missing-state": saved.Remove("Giga"); break;
                case "ordinary-with-state": saved["Size"] = "Standard"; saved["IsBoss"] = false; saved["BodyHp"] = 10; break;
                case "initial-below-minimum": giga["InitialCounter"] = 9; break;
                case "hp-before-initial": giga["HpCounter"] = 9; break;
                case "maximum-hp-counter": giga["HpCounter"] = int.MaxValue; break;
                case "zero-res": giga["ResCounter"] = 0; break;
                case "maximum-res-counter": giga["ResCounter"] = int.MaxValue; break;
                case "zero-attack": giga["AttackCounter"] = 0; break;
                case "overflowing-attack": giga["AttackCounter"] = int.MaxValue; break;
                case "negative-timer": giga["RecoverySeconds"] = -.01; break;
                case "full-second-timer": giga["RecoverySeconds"] = 1; break;
                case "infinite-timer": giga["RecoverySeconds"] = JsonNode.Parse("1e400"); break;
                case "zero-target": giga["LockedTargetId"] = 0; break;
                case "self-target": giga["LockedTargetId"] = saved["Id"]!.DeepClone(); break;
                case "wrong-spawn-cost": saved["OriginalBudgetCost"] = 522; break;
                case "discounted-kilo-cost": saved["OriginalBudgetCost"] = 10; break;
                case "discounted-mega-cost": saved["OriginalBudgetCost"] = 15; break;
                case "grown-hp-spawn-cost": saved["OriginalBudgetCost"] = GigaRules.SpawnCost(12); break;
                case "body-above-grown-maximum": saved["BodyHp"] = (float)GigaRules.MaximumHp(12) + 1; break;
                case "armor": saved["Armor"]!.AsArray().Add(JsonSerializer.SerializeToNode(new SavedZombieArmor())); break;
                case "bucket-tool": saved["CarriesBucket"] = true; break;
                case "bucket-payload": saved["Buckets"]!.AsArray().Add(JsonSerializer.SerializeToNode(new SavedZombieBucket())); break;
                case "carried-block": saved["CarriedBlocks"] = 1; break;
                case "held-block": saved["HeldBlocks"]!.AsArray().Add(JsonSerializer.SerializeToNode(new SavedKiloBlock())); break;
                case "missing-counter": giga.Remove("ResCounter"); break;
                default: throw new ArgumentOutOfRangeException(nameof(fault));
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
    public void HighValidResistanceCountersRestoreWithoutAnUnboundedCounterLoopOrAnOptionalTarget()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var saved = ValidSavedGiga();
            saved.Giga!.ResCounter = 1_000_000_000;
            saved.Giga.LockedTargetId = null;
            document["ReturningZombies"] = new JsonArray(JsonSerializer.SerializeToNode(saved, JsonOptions));
            File.WriteAllText(path, document.ToJsonString());

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.Equal(1_000_000_000, Assert.Single(restored!.ExportState().ReturningZombies!).Giga!.ResCounter);
            var state = GigaState.Restore(10, 12, 1_000_000_000, 5, .625);
            Assert.True(float.IsFinite(state.Resistance));
            Assert.True(state.Resistance > 1_000_000_000);
        });
    }

    [Theory]
    [InlineData(185, 185)]
    [InlineData(2500, 2500)]
    [InlineData(2500, 1)]
    [InlineData(10, int.MaxValue - 1)]
    public void LargeValidAttackCountersRoundTripExactlyWithoutCappingOrReinitializingSavedProgress(int initialCounter, int attackCounter)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var saved = ValidSavedGiga();
            saved.Giga!.InitialCounter = initialCounter;
            saved.Giga.HpCounter = initialCounter;
            saved.Giga.ResCounter = initialCounter;
            saved.Giga.AttackCounter = attackCounter;
            saved.BodyHp = (float)GigaRules.MaximumHp(initialCounter);
            saved.OriginalBudgetCost = checked((int)GigaRules.SpawnCost(initialCounter));
            document["ReturningZombies"] = new JsonArray(JsonSerializer.SerializeToNode(saved, JsonOptions));
            File.WriteAllText(path, document.ToJsonString());

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.Equal(attackCounter, Assert.Single(restored!.ExportState().ReturningZombies!).Giga!.AttackCounter);
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out restored, out error), error);
            Assert.Equal(attackCounter, Assert.Single(restored!.ExportState().ReturningZombies!).Giga!.AttackCounter);
            Assert.True(restored.StartLevel(), restored.Message);
            Assert.True((bool)Invoke(restored, "TrySpawnReinforcement", restored.Waves[0], false)!);
            var returned = Assert.Single(restored.Zombies);
            Assert.Equal(initialCounter, returned.Giga!.InitialCounter);
            Assert.Equal(initialCounter, returned.Giga.HpCounter);
            Assert.Equal(initialCounter, returned.Giga.ResCounter);
            Assert.Equal(attackCounter, returned.Giga.AttackCounter);
            Assert.Equal((float)Math.Pow(GigaRules.GoldenRatio, attackCounter), returned.AttackDamage);
            Assert.True(float.IsFinite(returned.Giga.BlastRadius));
            Assert.Equal(saved.OriginalBudgetCost, returned.OriginalBudgetCost);
        });
    }

    private static (GameSession Game, LevelCatalog Catalog, Zombie Original, int TargetId) DeferredGiga(
        int initialCounter = 10, int? hpCounter = null, bool omitOriginalCost = false)
    {
        var catalog = new LevelCatalog();
        foreach (int number in new[] { 1, 2 }) catalog.Set(new LevelDefinition
        {
            Number = number,
            Waves = [new WaveDefinition { Groups = [new ZombieGroup { Kind = ZombieKind.Basic }] }]
        });
        var game = new GameSession(levels: catalog);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(10, 16, 10)));
        int targetId = Assert.Single(game.Turrets).Id;
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var original = new Zombie { Id = 5000, Size = ZombieSize.Giga, IsBoss = true, WaveIndex = 0 };
        original.RestoreGigaState(initialCounter, hpCounter ?? initialCounter + 3, 7, 5, .625);
        original.Hp = original.MaxHp * .6f;
        original.Position = original.StandAt(new Cell(5, 16, 5));
        typeof(Zombie).GetProperty(nameof(Zombie.OriginalBudgetCost))!.SetValue(original, omitOriginalCost ? 0 : (int)original.Giga!.SpawnCost);
        game.Zombies.Add(original);
        TargetLocks(game)[original.Id] = targetId;
        Invoke(game, "DeferZombieToNextWave", original);
        game.Update(.001f);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(2, game.LevelNumber);
        return (game, catalog, original, targetId);
    }

    private static SavedReturningZombie ValidSavedGiga() => new()
    {
        TargetLevel = 1, TargetWaveIndex = 0, Id = 9000, Kind = ZombieKind.Basic, Tier = GearTier.Wood,
        Size = ZombieSize.Giga, IsBoss = true, BodyHp = (float)GigaRules.MaximumHp(12) * .6f,
        OriginalBudgetCost = (int)GigaRules.SpawnCost(10), CarriesBucket = false, CarriedBlocks = 0,
        Giga = new SavedGigaState { InitialCounter = 10, HpCounter = 12, ResCounter = 7, AttackCounter = 5, RecoverySeconds = .625 }
    };

    private static Dictionary<int, int> TargetLocks(GameSession game) => (Dictionary<int, int>)typeof(GameSession)
        .GetField("_gigaTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;

    private static object? Invoke(object owner, string method, params object?[] arguments) => owner.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static void WithSavePath(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-giga-persistence-{Guid.NewGuid():N}.json");
        try { action(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
