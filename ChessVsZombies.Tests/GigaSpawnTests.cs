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
        Assert.Equal(0, plan.GigaCount);
        Assert.Equal(plan.SpawnCount, plan.EncounterSpawnCount);
        Assert.Equal(plan.TotalHp, plan.EncounterTotalHp);
        Assert.Equal(plan.Groups.Sum(group => (long)group.Count * WaveSpawnPlan.BudgetCost(group)), plan.BudgetSpent);
    }

    [Fact]
    public void InitialCounterUsesTheOriginalWavePoolBeforeReservedPurchasesRemoveOrdinarySpawns()
    {
        var original = WaveSpawnPlanner.Plan(Wave(10000, enabled: false));
        var plan = WaveSpawnPlanner.Plan(Wave(10000));
        var giga = Assert.IsType<GigaSpawnPlan>(plan.GigaSpawns);

        Assert.Equal(original.ExactTotalHp, giga.OriginalWaveHp);
        Assert.Equal(GigaRules.SelectInitialCounter(original.ExactTotalHp), giga.InitialCounter);
        Assert.Equal(GigaRules.SpawnCost(giga.InitialCounter), giga.SpawnCost);
        Assert.True(giga.InitialCounter > GigaRules.SelectInitialCounter(plan.ExactTotalHp));
        Assert.Equal(original.HpBudget, plan.HpBudget);
        Assert.Equal(original.NormalBudget, plan.NormalBudget);
        Assert.Equal(original.BossBudget, plan.BossBudget);
        long ordinaryCost = plan.Groups.Sum(group => (long)group.Count * WaveSpawnPlan.BudgetCost(group));
        Assert.Equal(ordinaryCost + giga.SpawnCost * giga.Count, plan.BudgetSpent);
        Assert.InRange(plan.BudgetSpent, 0, plan.HpBudget);
    }

    [Theory]
    [InlineData(522, 0)]
    [InlineData(523, 1)]
    [InlineData(1046, 2)]
    public void MinimumCounterNeverShrinksToMakeAnUnaffordableSpawnFit(int budget, int expectedGigas)
    {
        var plan = WaveSpawnPlanner.Plan(Wave(budget));

        Assert.Equal(expectedGigas, plan.GigaCount);
        if (plan.GigaSpawns is { } giga)
        {
            Assert.Equal(10, giga.InitialCounter);
            Assert.Equal(523, giga.SpawnCost);
            Assert.True(giga.ExactInitialHp > 522);
            Assert.Equal(plan.SpawnCount + expectedGigas, plan.EncounterSpawnCount);
        }
        Assert.InRange(plan.BudgetSpent, 0, budget);
    }

    [Fact]
    public void MergedWaveReservesOnceUsingItsOwnCombinedOriginalPool()
    {
        var original = WaveSpawnPlanner.Combine([Wave(600, enabled: false), Wave(600, enabled: false)]);
        var combined = WaveSpawnPlanner.Combine([Wave(600), Wave(600)]);
        var giga = Assert.IsType<GigaSpawnPlan>(combined.GigaSpawns);

        Assert.Equal(original.ExactTotalHp, giga.OriginalWaveHp);
        Assert.Equal(GigaRules.SelectInitialCounter(original.ExactTotalHp), giga.InitialCounter);
        Assert.Equal(original.HpBudget / giga.SpawnCost, giga.Count);
        Assert.InRange(combined.BudgetSpent, 0, original.HpBudget);
    }

    [Fact]
    public void ReservedReplacementWaitsForDeathAndChargesOnlyWhenItsBodySpawns()
    {
        var game = Combat(Wave(1046));
        var wave = Assert.Single(game.Waves);
        Assert.Equal(2, game.PendingGigaSpawnCount);
        Assert.Equal(2, wave.TotalCount);
        Assert.Equal(0, wave.BudgetSpent);

        Assert.True(SpawnGiga(game, wave));
        var first = Assert.Single(game.Zombies);
        Assert.Equal(523, wave.BudgetSpent);
        Assert.Equal(1, game.PendingGigaSpawnCount);
        Assert.False(SpawnGiga(game, wave));
        Assert.Equal(523, wave.BudgetSpent);
        first.Hp = 0;

        Assert.True(SpawnGiga(game, wave));

        var replacement = Assert.Single(game.Zombies, zombie => zombie.BodyHp > 0);
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(first.Giga!.InitialCounter, replacement.Giga!.InitialCounter);
        Assert.Equal(1046, wave.BudgetSpent);
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
