using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

// Super is named Giga in the current runtime. These tests exercise spawning
// independently of the enemy's incoming-hit and attack progression rules.
public sealed class SuperSpawnBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(1046)]
    public void InitialHpFactorCannotFallBelowTen(double originalWaveHp)
    {
        Assert.Equal(10, GigaRules.SelectInitialCounter(originalWaveHp));
        Assert.Equal(523, GigaRules.SpawnCost(10));
    }

    [Theory]
    [InlineData(1100)]
    [InlineData(2000)]
    [InlineData(10000)]
    [InlineData(100000)]
    public void SharedInitialFactorMinimizesHpDistanceAndDoesNotOptimizeResistance(double originalWaveHp)
    {
        double target = originalWaveHp / 2;
        int expected = Enumerable.Range(10, 1000).MinBy(p => Math.Abs(Math.Pow(p, Math.E) - target));

        int selected = GigaRules.SelectInitialCounter(originalWaveHp);
        var zombie = new Zombie { Size = ZombieSize.Giga };
        zombie.InitializeGiga(originalWaveHp);

        Assert.Equal(expected, selected);
        Assert.Equal(expected, zombie.Giga!.HpCounter);
        Assert.Equal(expected, zombie.Giga.ResCounter);
        Assert.Equal((float)Math.Pow(expected, Math.E), zombie.BodyHp);
        Assert.Equal((float)Enumerable.Range(1, expected).Sum(k => Math.Log(k, Math.PI)), zombie.Giga.Resistance);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void RuntimeChecksTheRemainingPoolBeforeSpendingOrConsumingAQueueEntry(int shortfall, bool expectedSpawn)
    {
        var game = Combat(Wave(1046));
        var wave = Assert.Single(game.Waves);
        // The immutable plan permits two purchases, but activation must check
        // what is actually left rather than trusting that original reservation.
        wave.BudgetSpent = wave.HpBudget - 523 + shortfall;
        long before = wave.BudgetSpent;

        Assert.Equal(expectedSpawn, Spawn(game, wave));

        Assert.Equal(before + (expectedSpawn ? 523 : 0), wave.BudgetSpent);
        Assert.Equal(expectedSpawn ? 1 : 0, wave.Spawned);
        Assert.Equal(expectedSpawn ? 1 : 2, game.PendingGigaSpawnCount);
        Assert.Equal(expectedSpawn ? 1 : 0, game.DeathToll.PhysicalSpawns);
        Assert.Equal(expectedSpawn ? 1 : 0, game.Zombies.Count);
    }

    [Fact]
    public void AFailedReplacementDoesNotSpendOrLowerItsOriginalFactor()
    {
        var game = Combat(Wave(1046));
        var wave = Assert.Single(game.Waves);
        Assert.True(Spawn(game, wave));
        var first = Assert.Single(game.Zombies);
        game.DamageZombie(first, first.Hp + 100);
        wave.HpBudget--; // Only 522 remains: do not discount the replacement.

        for (int attempt = 0; attempt < 3; attempt++) Assert.False(Spawn(game, wave));

        Assert.Equal(523, wave.BudgetSpent);
        Assert.Equal(1, wave.Spawned);
        Assert.Equal(1, game.PendingGigaSpawnCount);
        Assert.DoesNotContain(game.Zombies, zombie => zombie.BodyHp > 0);
        wave.HpBudget++;

        Assert.True(Spawn(game, wave));
        var replacement = Assert.Single(game.Zombies, zombie => zombie.BodyHp > 0);
        Assert.Equal(first.Giga!.InitialCounter, replacement.Giga!.InitialCounter);
        Assert.Equal(replacement.Giga.HpCounter, replacement.Giga.ResCounter);
        Assert.Equal(1046, wave.BudgetSpent);
    }

    [Fact]
    public void FailedPlacementKeepsThePurchaseAvailableAndChargesOnceOnRetry()
    {
        var game = Combat(Wave(1046));
        var wave = Assert.Single(game.Waves);
        // Seal every boundary cell above the existing terrain. There is no
        // supported, clear location for a full body anywhere on the spawn ring.
        var blocked = new List<Cell>();
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int z = 0; z < WorldGrid.Size; z++)
                for (int y = 16; y < WorldGrid.Size; y++)
                {
                    var cell = new Cell(x, y, z);
                    if (!WorldGrid.IsSpawnZone(cell)) continue;
                    Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", cell, Material.Stone)!);
                    blocked.Add(cell);
                }

        for (int attempt = 0; attempt < 3; attempt++) Assert.False(Spawn(game, wave));

        Assert.Equal(0, wave.BudgetSpent);
        Assert.Equal(0, wave.Spawned);
        Assert.Equal(2, game.PendingGigaSpawnCount);
        Assert.Equal(0, game.DeathToll.PhysicalSpawns);
        Assert.Empty(game.Zombies);
        foreach (var cell in blocked)
            Assert.True((bool)Invoke(game.World, "DamageTerrain", cell, (float)Balance.StoneBlockHp)!);

        Assert.True(Spawn(game, wave));
        Assert.False(Spawn(game, wave)); // A simultaneous activation is rejected.
        Assert.Equal(523, wave.BudgetSpent);
        Assert.Equal(1, wave.Spawned);
        Assert.Equal(1, game.PendingGigaSpawnCount);
        Assert.Equal(1, game.DeathToll.PhysicalSpawns);
    }

    [Fact]
    public void GameLoopRejectsASimultaneousSpawnAndSpawnsAReplacementAfterDeath()
    {
        var game = Combat(Wave(1046));
        var wave = Assert.Single(game.Waves);
        game.Update(.01f);
        var first = Assert.Single(game.Zombies);
        Assert.Equal(523, wave.BudgetSpent);

        wave.SpawnTimer = 0;
        game.Update(.01f);
        Assert.Single(game.Zombies);
        Assert.Equal(523, wave.BudgetSpent);
        Assert.Equal(1, game.PendingGigaSpawnCount);

        game.DamageZombie(first, first.Hp + 100);
        game.Update(.01f);

        var replacement = Assert.Single(game.Zombies, zombie => zombie.BodyHp > 0);
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(first.Giga!.InitialCounter, replacement.Giga!.InitialCounter);
        Assert.Equal(1046, wave.BudgetSpent);
        Assert.Equal(2, wave.Spawned);
        Assert.Equal(0, game.PendingGigaSpawnCount);
    }

    [Theory]
    [InlineData(.01f)]
    [InlineData(.03f)]
    [InlineData(.05f)]
    public void SeparatePurchasesPreserveTheConfiguredKiloMegaReserveAndDiscounts(float fraction)
    {
        var authored = Wave(10000);
        authored.BossBudgetFraction = fraction;
        var plan = WaveSpawnPlanner.Plan(authored);
        authored.EnableGiga = false;
        var original = WaveSpawnPlanner.Plan(authored);

        Assert.NotNull(plan.GigaSpawns);
        Assert.Equal(original.NormalBudget, plan.NormalBudget);
        Assert.Equal(original.BossBudget, plan.BossBudget);
        var bosses = plan.Groups.Where(group => group.IsBoss).ToArray();
        var originalBosses = original.Groups.Where(group => group.IsBoss).ToArray();
        Assert.Equal(originalBosses.Select(group => (group.Size, group.Kind, group.Tier, group.Count)),
            bosses.Select(group => (group.Size, group.Kind, group.Tier, group.Count)));
        Assert.All(bosses, group => Assert.Equal(group.Size == ZombieSize.Kilo ? 10 : 15,
            WaveSpawnPlan.BudgetCost(group)));
        Assert.InRange(plan.BudgetSpent, 0, plan.HpBudget);
    }

    private static WaveDefinition Wave(int budget) => new()
    { HpBudget = budget, EnableGiga = true, Groups = [new ZombieGroup { Kind = ZombieKind.Basic }] };

    private static GameSession Combat(params WaveDefinition[] waves)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = waves.ToList() });
        var game = new GameSession(123, levels);
        Assert.True(game.StartLevel());
        return game;
    }

    private static bool Spawn(GameSession game, WaveState wave) => (bool)Invoke(game, "TrySpawnGiga", wave)!;
    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
}
