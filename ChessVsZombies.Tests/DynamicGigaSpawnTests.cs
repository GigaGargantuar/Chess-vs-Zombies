using System.Collections;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class DynamicGigaSpawnTests
{
    [Fact]
    public void ReplacementsContinueWithCheaperCurrentPoolQuotesBeyondTheInitialQuoteCount()
    {
        var game = Combat(10000);
        var wave = Assert.Single(game.Waves);
        int originalCount = wave.TotalCount;
        int[] expectedCounters = [23, 18, 14, 10, 10];
        long[] expectedCosts = [5030, 2584, 1305, 523, 523];
        long spent = 0;

        for (int index = 0; index < expectedCounters.Length; index++)
        {
            Assert.Equal(1, game.PendingGigaSpawnCount);
            long remaining = wave.HpBudget - spent;
            Assert.Equal(expectedCounters[index], GigaRules.SelectInitialCounter(remaining));
            Assert.True(Spawn(game, wave));
            var giga = Assert.Single(game.Zombies, zombie => zombie.IsGiga && zombie.Hp > 0);
            Assert.Equal(expectedCounters[index], giga.Giga!.InitialCounter);
            Assert.Equal(expectedCounters[index], giga.Giga.HpCounter);
            Assert.Equal(expectedCounters[index], giga.Giga.ResCounter);
            Assert.Equal(expectedCounters[index], giga.Giga.AttackCounter);
            Assert.Equal(expectedCosts[index], giga.OriginalBudgetCost);
            Assert.Equal(expectedCosts[index], giga.Giga.SpawnCost);
            spent += expectedCosts[index];
            Assert.Equal(spent, wave.BudgetSpent);
            Assert.Equal(index + 1, wave.Spawned);
            Assert.Equal(originalCount + index + 1, wave.TotalCount);
            Assert.False(Spawn(game, wave));
            Assert.Equal(spent, wave.BudgetSpent);
            giga.Hp = 0;
        }

        Assert.Equal(9965, wave.BudgetSpent);
        Assert.Equal(35, wave.HpBudget - wave.BudgetSpent);
        Assert.Equal(0, game.PendingGigaSpawnCount);
        Assert.False(Spawn(game, wave));
        Assert.Equal(5, game.DeathToll.PhysicalSpawns);
    }

    [Fact]
    public void InterveningOrdinaryPurchasesWeakenTheNextBodyWithoutChangingTheActiveBody()
    {
        var game = Combat(10000);
        var wave = Assert.Single(game.Waves);
        Assert.True(Spawn(game, wave));
        var first = Assert.Single(game.Zombies);
        int firstCounter = first.Giga!.InitialCounter;
        float firstMaximum = first.MaxHp;
        wave.BudgetSpent += 2000; // A purchase from the same shared wallet.
        long beforeReplacement = wave.BudgetSpent;
        int expectedCounter = GigaRules.SelectInitialCounter(wave.HpBudget - beforeReplacement);
        long expectedCost = GigaRules.SpawnCost(expectedCounter);
        Assert.Equal(firstCounter, first.Giga.InitialCounter);
        Assert.Equal(firstMaximum, first.MaxHp);
        Assert.False(Spawn(game, wave));
        Assert.Equal(beforeReplacement, wave.BudgetSpent);
        first.Hp = 0;

        Assert.True(Spawn(game, wave));

        var replacement = Assert.Single(game.Zombies, zombie => zombie.IsGiga && zombie.Hp > 0);
        Assert.Equal(expectedCounter, replacement.Giga!.InitialCounter);
        Assert.Equal(expectedCounter, replacement.Giga.AttackCounter);
        Assert.True(replacement.Giga.InitialCounter < firstCounter);
        Assert.Equal(expectedCost, replacement.OriginalBudgetCost);
        Assert.Equal(beforeReplacement + expectedCost, wave.BudgetSpent);
        Assert.Equal(firstCounter, first.Giga.InitialCounter);
        Assert.Equal(firstMaximum, first.MaxHp);
    }

    [Fact]
    public void CombatProgressionOfAnActiveBodyDoesNotSpendOrRepriceItsSpawnPurchase()
    {
        var game = Combat(10000);
        var wave = Assert.Single(game.Waves);
        Assert.True(Spawn(game, wave));
        var giga = Assert.Single(game.Zombies);
        int initial = giga.Giga!.InitialCounter;
        long spent = wave.BudgetSpent;
        giga.RestoreGigaState(initial, initial, 1, 1, 0); // Isolate progression after an earlier kill reset.
        game.DamageZombie(giga, 1);
        giga.Giga!.AdvanceRecovery(2.5f);
        giga.Giga.RecordSuccessfulAttack(false);

        Assert.Equal(initial, giga.Giga.InitialCounter);
        Assert.Equal(initial + 1, giga.Giga.HpCounter);
        Assert.Equal(3, giga.Giga.ResCounter);
        Assert.Equal(2, giga.Giga.AttackCounter);
        Assert.Equal(spent, wave.BudgetSpent);
        Assert.Equal(spent, giga.OriginalBudgetCost);
        Assert.Equal(spent, giga.Giga.SpawnCost);
    }

    [Theory]
    [InlineData(1046)]
    [InlineData(1000000)]
    public void ExhaustedSharedWalletRetiresOrdinaryQueueWithoutInventingKillsAndAllowsVictory(int budget)
    {
        var game = Combat(budget);
        var wave = Assert.Single(game.Waves);
        float originalTotalHp = wave.TotalHp;
        wave.BudgetSpent = wave.HpBudget;
        Assert.Equal(0, game.PendingGigaSpawnCount);

        game.Update(.001f);

        Assert.Equal(wave.TotalCount, wave.Spawned);
        Assert.True(wave.TotalHp <= originalTotalHp);
        Assert.Equal(budget, wave.BudgetSpent);
        Assert.Equal(0, game.DeathToll.PhysicalSpawns);
        Assert.Equal(0, game.DeathToll.FieldDeaths);
        Assert.Equal(0, game.DeathToll.PreSpawnQueueDeaths);
        Assert.Empty(game.Zombies);
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    [Theory]
    [InlineData(3000, false)]
    [InlineData(5000, true)]
    public void AConsumedCheapGroupCannotMaskThousandsOfUnaffordableRemainingPurchases(int expensiveCount, bool sparse)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition
        {
            Waves = [new WaveDefinition
            {
                EnableGiga = true,
                Groups =
                [
                    new ZombieGroup { Kind = ZombieKind.Basic, Size = ZombieSize.Micro, Count = 1 },
                    new ZombieGroup { Kind = ZombieKind.Basic, Count = expensiveCount }
                ]
            }]
        });
        var game = new GameSession(123, catalog);
        Assert.True(game.StartLevel());
        var wave = Assert.Single(game.Waves);
        var largeWaves = (IList)Field(game, "_largeStandbyWaves");
        Assert.Equal(sparse, largeWaves[0] != null);
        IEnumerable<object> candidates = sparse
            ? ((IDictionary)Property(largeWaves[0]!, "Fronts")).Values.Cast<object>()
            : ((Array)((IList)Field(game, "_standbyWaves"))[0]!).Cast<object>();
        var cheap = Assert.Single(candidates, slot => ((ZombieGroup)Property(slot, "Group")).Size == ZombieSize.Micro);
        wave.BudgetSpent = wave.HpBudget - 7;
        Assert.True((bool)Invoke(game, "TryActivateStandby", cheap, wave)!);
        var micro = Assert.Single(game.Zombies);
        Assert.Equal(ZombieSize.Micro, micro.Size);
        Assert.Equal(5, wave.HpBudget - wave.BudgetSpent);
        game.DamageZombie(micro, micro.Hp + 100);
        Assert.True(game.Zombies.Remove(micro));
        Assert.Equal(1, wave.Spawned);
        Assert.Equal(expensiveCount, game.LivingStandbyCount);
        long spent = wave.BudgetSpent;
        long deaths = game.DeathToll.TotalDeaths;
        long queueDeaths = game.DeathToll.PreSpawnQueueDeaths;
        long physicalSpawns = game.DeathToll.PhysicalSpawns;

        Assert.True((bool)Invoke(game, "RetireUnfundedStandby", wave)!);

        Assert.Equal(wave.TotalCount, wave.Spawned);
        Assert.Equal(0, game.LivingStandbyCount);
        Assert.Equal(spent, wave.BudgetSpent);
        Assert.Equal(5, wave.HpBudget - wave.BudgetSpent);
        Assert.Equal(deaths, game.DeathToll.TotalDeaths);
        Assert.Equal(queueDeaths, game.DeathToll.PreSpawnQueueDeaths);
        Assert.Equal(physicalSpawns, game.DeathToll.PhysicalSpawns);
        Assert.Empty(game.Zombies);
        Assert.False((bool)Invoke(game, "RetireUnfundedStandby", wave)!);
    }

    private static GameSession Combat(int budget)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition
        {
            Waves = [new WaveDefinition { HpBudget = budget, EnableGiga = true, Groups = [new ZombieGroup { Kind = ZombieKind.Basic }] }]
        });
        var game = new GameSession(123, catalog);
        Assert.True(game.StartLevel());
        return game;
    }

    private static bool Spawn(GameSession game, WaveState wave) => (bool)typeof(GameSession)
        .GetMethod("TrySpawnGiga", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, [wave])!;

    private static object Field(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static object Property(object owner, string name) => owner.GetType().GetProperty(name)!.GetValue(owner)!;

    private static object? Invoke(object owner, string name, params object?[] arguments) => owner.GetType()
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, arguments);
}
