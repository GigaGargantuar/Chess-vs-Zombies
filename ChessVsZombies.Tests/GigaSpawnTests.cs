using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GigaSpawnTests
{
    [Fact]
    public void LegacyAuthoredWavesKeepTheirExactRosterWithoutOptingIn()
    {
        var wave = Wave(10000, enabled: false);
        var plan = WaveSpawnPlanner.Plan(wave);

        Assert.Null(plan.GigaSpawns);
        Assert.False(plan.GigaEnabled);
        Assert.Equal(plan.SpawnCount, plan.EncounterSpawnCount);
        Assert.Equal(plan.TotalHp, plan.EncounterTotalHp);
        Assert.Equal(plan.Groups.Sum(group => (long)group.Count * WaveSpawnPlan.BudgetCost(group)), plan.BudgetSpent);
    }

    [Fact]
    public void InitialQuoteUsesTheStartingPoolWithoutPrepayingOrRemovingOrdinarySpawns()
    {
        var original = WaveSpawnPlanner.Plan(Wave(10000, enabled: false));
        var plan = WaveSpawnPlanner.Plan(Wave(10000));
        var giga = Assert.IsType<GigaSpawnPlan>(plan.GigaSpawns);

        Assert.True(plan.GigaEnabled);
        Assert.Equal((double)original.HpBudget, giga.OriginalWaveHp);
        Assert.Equal(GigaRules.SelectInitialCounter(original.HpBudget), giga.InitialCounter);
        Assert.Equal(GigaRules.SpawnCost(giga.InitialCounter), giga.SpawnCost);
        Assert.True(giga.InitialCounter > GigaRules.SelectInitialCounter(plan.HpBudget - giga.SpawnCost * giga.Count));
        Assert.Equal(original.HpBudget, plan.HpBudget);
        Assert.Equal(original.NormalBudget, plan.NormalBudget);
        Assert.Equal(original.BossBudget, plan.BossBudget);
        AssertSameOrdinaryRoster(original, plan);
        Assert.InRange(plan.BudgetSpent, 0, plan.HpBudget);
    }

    [Theory]
    [InlineData(522, 0)]
    [InlineData(523, 1)]
    [InlineData(1046, 2)]
    public void MinimumCounterNeverShrinksToMakeAnUnaffordableSpawnFit(int budget, int expectedGigas)
    {
        var plan = WaveSpawnPlanner.Plan(Wave(budget));

        Assert.True(plan.GigaEnabled);
        Assert.Equal(expectedGigas, plan.GigaSpawns?.Count ?? 0);
        if (plan.GigaSpawns is { } giga)
        {
            Assert.Equal(10, giga.InitialCounter);
            Assert.Equal(523, giga.SpawnCost);
            Assert.True(giga.ExactInitialHp > 522);
            Assert.Equal(plan.SpawnCount, plan.EncounterSpawnCount);
        }
        Assert.InRange(plan.BudgetSpent, 0, budget);
    }

    [Theory]
    [InlineData(1000, .01f, 1)]
    [InlineData(1000, .05f, 1)]
    [InlineData(1046, .01f, 2)]
    [InlineData(1046, .05f, 2)]
    public void GigaPurchasesUseTheEntireWavePoolAndChargeFullHpOutsideTheBossReserve(int budget, float bossFraction, int expectedGigas)
    {
        var definition = Wave(budget);
        definition.BossBudgetFraction = bossFraction;
        var plan = WaveSpawnPlanner.Plan(definition);
        var reserved = Assert.IsType<GigaSpawnPlan>(plan.GigaSpawns);
        Assert.Equal(expectedGigas, reserved.Count);
        Assert.Equal(523, reserved.SpawnCost);
        Assert.True(reserved.SpawnCost > plan.BossBudget);
        Assert.Equal((double)budget, reserved.OriginalWaveHp);

        var game = Combat(definition);
        var wave = Assert.Single(game.Waves);
        long bossReserve = wave.BossBudget;
        Assert.Equal(0, wave.BudgetSpent);
        for (int purchase = 1; purchase <= expectedGigas; purchase++)
        {
            Assert.True(SpawnGiga(game, wave));
            var giga = Assert.Single(game.Zombies, zombie => zombie.IsGiga && zombie.Hp > 0);
            Assert.Equal(giga.Giga!.InitialCounter, giga.Giga.AttackCounter);
            Assert.Equal(523, giga.OriginalBudgetCost);
            Assert.Equal(523L * purchase, wave.BudgetSpent);
            Assert.Equal(bossReserve, wave.BossBudget);
            Assert.True(wave.BudgetSpent > bossReserve);
            giga.Hp = 0;
        }
        Assert.Equal(0, game.PendingGigaSpawnCount);
        Assert.InRange(wave.BudgetSpent, 0, budget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericGigaBudgetCostAlwaysUsesItsFullMinimumHpRegardlessOfBossFlag(bool isBoss)
    {
        Assert.Equal(523, WaveSpawnPlan.BudgetCost(new ZombieGroup { Size = ZombieSize.Giga, IsBoss = isBoss }));
        Assert.Equal(10, WaveSpawnPlan.BudgetCost(new ZombieGroup { Size = ZombieSize.Kilo, IsBoss = true }));
        Assert.Equal(15, WaveSpawnPlan.BudgetCost(new ZombieGroup { Size = ZombieSize.Mega, IsBoss = true }));
    }

    [Fact]
    public void MergedWaveQuotesItsCombinedStartingPoolWithoutReplacingOrdinaryPurchases()
    {
        var original = WaveSpawnPlanner.Combine([Wave(600, enabled: false), Wave(600, enabled: false)]);
        var combined = WaveSpawnPlanner.Combine([Wave(600), Wave(600)]);
        var giga = Assert.IsType<GigaSpawnPlan>(combined.GigaSpawns);

        Assert.Equal((double)original.HpBudget, giga.OriginalWaveHp);
        Assert.Equal(GigaRules.SelectInitialCounter(original.HpBudget), giga.InitialCounter);
        Assert.Equal(original.HpBudget / giga.SpawnCost, giga.Count);
        AssertSameOrdinaryRoster(original, combined);
        Assert.InRange(combined.BudgetSpent, 0, original.HpBudget);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(35)]
    [InlineData(95)]
    [InlineData(100)]
    [InlineData(1048576)]
    public void AffordableGeneratedCampaignWavesQuoteGigasFromTheSpendablePoolDespiteDiscountedBossHp(int levelNumber)
    {
        var catalog = new LevelCatalog();
        foreach (var wave in catalog.Get(levelNumber).Waves)
        {
            var plan = WaveSpawnPlanner.Plan(wave);
            wave.EnableGiga = false;
            var original = WaveSpawnPlanner.Plan(wave);

            Assert.True(original.ExactTotalHp > original.HpBudget);
            AssertAffordableInitialQuote(original, plan);
        }
    }

    [Fact]
    public void HighLevelAllInOneUsesTheCombinedSpendablePoolAndKeepsItsGigasAffordable()
    {
        var catalog = new LevelCatalog();
        var waves = Enumerable.Range(1048576, 5).SelectMany(number => catalog.Get(number).Waves).ToArray();
        var combined = WaveSpawnPlanner.Combine(waves);
        foreach (var wave in waves) wave.EnableGiga = false;
        var original = WaveSpawnPlanner.Combine(waves);

        Assert.True(original.HpBudget > int.MaxValue);
        Assert.True(original.ExactTotalHp > original.HpBudget);
        AssertAffordableInitialQuote(original, combined);
        Assert.True(combined.GigaSpawns!.SpawnCost <= int.MaxValue);
    }

    [Fact]
    public void GeneratedLevel95NaturallySpawnsALiveGigaOnTheFirstCombatUpdate()
    {
        var generated = new LevelCatalog().Get(95);
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Number = 1, Name = generated.Name, Waves = generated.Waves });
        var game = new GameSession(123, catalog);
        Assert.True(game.StartLevel());
        var wave = Assert.Single(game.Waves);
        Assert.True(game.PendingGigaSpawnCount > 0);
        Assert.Equal(0, wave.BudgetSpent);

        game.Update(.001f);

        var giga = Assert.Single(game.Zombies, zombie => zombie.IsGiga && zombie.BodyHp > 0);
        Assert.Equal(4, giga.GridFootprint);
        Assert.Equal(new Vector3(4), giga.BodyHalfExtents * 2);
        var min = giga.Position - giga.BodyHalfExtents;
        var max = giga.Position + giga.BodyHalfExtents;
        Assert.True(min.X >= 0 && min.Y >= 0 && min.Z >= 0);
        Assert.True(max.X <= WorldGrid.Size && max.Y <= WorldGrid.Size && max.Z <= WorldGrid.Size);
        Assert.True((bool)Invoke(game, "IsBodyClear", giga.Position, giga)!);
        Assert.Equal(giga.Giga!.SpawnCost, wave.BudgetSpent);
        Assert.InRange(wave.BudgetSpent, 1, wave.HpBudget);
        Assert.Equal(GigaRules.SelectInitialCounter(wave.HpBudget), giga.Giga.InitialCounter);
        Assert.Equal(giga.Giga.InitialCounter, giga.Giga.AttackCounter);
    }

    [Fact]
    public void AnAffordableReplacementWaitsForDeathAndAddsItsCountAndHpOnlyWhenItSpawns()
    {
        var game = Combat(Wave(1046));
        var wave = Assert.Single(game.Waves);
        int originalCount = wave.TotalCount;
        float originalHp = wave.TotalHp;
        Assert.Equal(1, game.PendingGigaSpawnCount);
        Assert.Equal(0, wave.BudgetSpent);

        Assert.True(SpawnGiga(game, wave));
        var first = Assert.Single(game.Zombies);
        Assert.Equal(523, wave.BudgetSpent);
        Assert.Equal(originalCount + 1, wave.TotalCount);
        Assert.Equal(originalHp + first.MaxHp, wave.TotalHp);
        Assert.Equal(1, game.PendingGigaSpawnCount);
        Assert.False(SpawnGiga(game, wave));
        Assert.Equal(523, wave.BudgetSpent);
        first.Hp = 0;

        Assert.True(SpawnGiga(game, wave));

        var replacement = Assert.Single(game.Zombies, zombie => zombie.BodyHp > 0);
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(first.Giga!.InitialCounter, replacement.Giga!.InitialCounter);
        Assert.Equal(replacement.Giga.InitialCounter, replacement.Giga.AttackCounter);
        Assert.Equal(1046, wave.BudgetSpent);
        Assert.Equal(originalCount + 2, wave.TotalCount);
        Assert.Equal(2, wave.Spawned);
        Assert.Equal(0, game.PendingGigaSpawnCount);
        Assert.False(SpawnGiga(game, wave));
    }

    [Fact]
    public void SingletonAppliesAcrossOverlappingWavesAndSpawnFitsTheFullFourCubedBody()
    {
        var game = Combat(Wave(1046), Wave(1046));
        Assert.True(SpawnGiga(game, game.Waves[0]));
        var giga = Assert.Single(game.Zombies);
        Assert.False(SpawnGiga(game, game.Waves[1]));
        var min = giga.Position - giga.BodyHalfExtents;
        var max = giga.Position + giga.BodyHalfExtents;

        Assert.Equal(new Vector3(4), max - min);
        Assert.True(min.X == 0 || min.Z == 0 || max.X == WorldGrid.Size || max.Z == WorldGrid.Size);
        Assert.True(min.X >= 0 && min.Y >= 0 && min.Z >= 0);
        Assert.True(max.X <= WorldGrid.Size && max.Y <= WorldGrid.Size && max.Z <= WorldGrid.Size);
        Assert.True((bool)Invoke(game, "IsBodyClear", giga.Position, giga)!);
        Assert.Empty(giga.ArmorPieces);
        Assert.False(giga.CarriesBucket);
        Assert.Equal(0, giga.CarriedBlocks);
        giga.Hp = 0;
        Assert.True(SpawnGiga(game, game.Waves[1]));
    }

    [Fact]
    public void AnOptedInQueueIsIncludedInVictoryAndExternalReplaySkipping()
    {
        var game = Combat(Wave(1046));
        var wave = Assert.Single(game.Waves);
        wave.Spawned = wave.TotalCount;

        Invoke(game, "ReconcileExternallySkippedSpawns");

        Assert.Equal(0, game.PendingGigaSpawnCount);
        Assert.Empty(game.Zombies);
        Assert.Equal(0, wave.BudgetSpent);
        game.Update(.001f);
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    [Fact]
    public void CatalogRetainsTheExplicitToggleAndPreventsGigasBypassingTheirSpecialQueue()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [Wave(1046)] });
        Assert.True(Assert.Single(levels.Get(1).Waves).EnableGiga);
        Assert.All(new LevelCatalog().Get(20).Waves, wave => Assert.True(wave.EnableGiga));
        Assert.Throws<InvalidDataException>(() => levels.Set(new LevelDefinition
        { Waves = [new WaveDefinition { Groups = [new ZombieGroup { Size = ZombieSize.Giga }] }] }));
    }

    private static WaveDefinition Wave(int budget, bool enabled = true) => new()
    { HpBudget = budget, EnableGiga = enabled, Groups = [new ZombieGroup { Kind = ZombieKind.Basic }] };

    private static void AssertAffordableInitialQuote(WaveSpawnPlan original, WaveSpawnPlan plan)
    {
        var giga = Assert.IsType<GigaSpawnPlan>(plan.GigaSpawns);
        Assert.True(giga.Count >= 1);
        Assert.Equal((double)original.HpBudget, giga.OriginalWaveHp);
        Assert.Equal(GigaRules.SelectInitialCounter(original.HpBudget), giga.InitialCounter);
        Assert.Equal(GigaRules.SpawnCost(giga.InitialCounter), giga.SpawnCost);
        Assert.Equal(original.HpBudget / giga.SpawnCost, giga.Count);
        Assert.InRange(giga.SpawnCost, 1, original.HpBudget);
        if (giga.InitialCounter > GigaRules.MinimumInitialCounter)
            Assert.InRange(giga.SpawnCost / (double)original.HpBudget, .4, .6);
        else
            Assert.Equal(GigaRules.SpawnCost(GigaRules.MinimumInitialCounter), giga.SpawnCost);

        Assert.True(plan.GigaEnabled);
        AssertSameOrdinaryRoster(original, plan);
        Assert.Equal(original.HpBudget, plan.HpBudget);
        Assert.Equal(original.NormalBudget, plan.NormalBudget);
        Assert.Equal(original.BossBudget, plan.BossBudget);
        Assert.InRange(plan.BudgetSpent, 0, original.HpBudget);
        Assert.Equal(plan.SpawnCount, plan.EncounterSpawnCount);
    }

    private static void AssertSameOrdinaryRoster(WaveSpawnPlan original, WaveSpawnPlan plan)
    {
        Assert.Equal(original.Groups.Select(group => (group.Kind, group.Tier, group.Size, group.IsBoss, group.Count)),
            plan.Groups.Select(group => (group.Kind, group.Tier, group.Size, group.IsBoss, group.Count)));
        Assert.Equal(original.SpawnCount, plan.SpawnCount);
        Assert.Equal(original.TotalHp, plan.TotalHp);
        Assert.Equal(original.ExactTotalHp, plan.ExactTotalHp);
        Assert.Equal(original.BudgetSpent, plan.BudgetSpent);
        Assert.Equal(plan.Groups.Sum(group => (long)group.Count * WaveSpawnPlan.BudgetCost(group)), plan.BudgetSpent);
        Assert.Equal(plan.TotalHp, plan.EncounterTotalHp);
        Assert.Equal(plan.ExactTotalHp, plan.EncounterExactTotalHp);
    }

    private static GameSession Combat(params WaveDefinition[] waves)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = waves.ToList() });
        var game = new GameSession(123, levels);
        Assert.True(game.StartLevel());
        return game;
    }

    private static bool SpawnGiga(GameSession game, WaveState wave) => (bool)Invoke(game, "TrySpawnGiga", wave)!;
    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
}
