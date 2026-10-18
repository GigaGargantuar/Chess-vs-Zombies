using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class DeathTollDiagnosticsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(2)]
    [InlineData(5000)]
    public void PreSpawnDeathIsCountedOnceWhenItsScheduledSlotIsLaterProcessed(int count)
    {
        var game = Battle(count);
        var first = Fronts(game).Single(slot => Slot(slot) == 0);
        Fire(game, first, 1_000_000);
        Assert.Equal(1, game.DeathToll.PreSpawnQueueDeaths);
        Assert.Equal(1, game.DeathToll.TotalDeaths);
        Assert.Equal(0, game.DeathToll.DeadScheduledSlotsProcessed);
        Assert.Equal(count, game.UnprocessedQueueEntries);

        Assert.True((bool)Invoke(game, "ScheduleNextStandby", game.Waves[0])!);
        Assert.Equal(1, game.DeathToll.PreSpawnQueueDeaths);
        Assert.Equal(1, game.DeathToll.TotalDeaths);
        Assert.Equal(1, game.DeathToll.DeadScheduledSlotsProcessed);
        Assert.Equal(1, game.DeathToll.ProcessedQueuedSlots);
        Assert.Equal(0, game.DeathToll.PhysicalSpawns);
        Assert.Equal(count - 1, game.UnprocessedQueueEntries);
    }

    [Fact]
    public void ActiveDeathIsCountedOnceAcrossDamageAndCleanup()
    {
        var game = Battle(2);
        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);
        var zombie = Assert.Single(game.Zombies);
        Assert.Equal(1, game.DeathToll.PhysicalSpawns);
        game.DamageZombie(zombie, 1_000_000, DamageKind.Chemical);
        game.DamageZombie(zombie, 1_000_000, DamageKind.Chemical);
        game.Update(.01f);
        Assert.Equal(1, game.DeathToll.FieldDeaths);
        Assert.Equal(0, game.DeathToll.PreSpawnQueueDeaths);
        Assert.Equal(1, game.DeathToll.TotalDeaths);
        Assert.Equal(1, game.DeathToll.PhysicalSpawns);
    }

    [Fact]
    public void CleanupCountsADeathFromDirectHpMutation()
    {
        var game = Battle(2);
        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);
        var zombie = Assert.Single(game.Zombies);
        game.Waves[0].SpawnTimer = 100;
        zombie.Hp = 0;
        game.Update(.01f);
        Assert.Equal(1, game.DeathToll.FieldDeaths);
    }

    [Fact]
    public void NonlethalCoverAndEmptyWallHitsSeparateHitsDamageAndMisses()
    {
        var game = Battle(1, ZombieSize.Kilo);
        var front = Assert.Single(Fronts(game));
        Fire(game, front, 3);
        Assert.Equal(1, game.DeathToll.QueuedProjectileHits);
        Assert.Equal(0, game.DeathToll.QueuedDamage);
        Assert.Equal(1, game.DeathToll.BoundaryImpactsWithoutQueuedDamage);
        Fire(game, front, 1_000_000); // Breaking cover absorbs this whole shot.
        Assert.Equal(2, game.DeathToll.QueuedProjectileHits);
        Assert.Equal(0, game.DeathToll.QueuedDamage);
        Assert.Equal(2, game.DeathToll.BoundaryImpactsWithoutQueuedDamage);
        Fire(game, front, 2); // Live body, nonlethal.
        Assert.Equal(3, game.DeathToll.QueuedProjectileHits);
        Assert.Equal(2, game.DeathToll.QueuedDamage);
        Assert.Equal(0, game.DeathToll.PreSpawnQueueDeaths);

        game.Projectiles.Add(new Projectile { Origin = new Vector3(16.5f, 30, 16.5f),
            Target = new Vector3(16.5f, 34, 16.5f), Damage = 1,
            Kind = ShotKind.Pellet, Duration = .05f });
        Invoke(game, "UpdateProjectiles", .05f);
        Assert.Equal(3, game.DeathToll.BoundaryImpactsWithoutQueuedDamage);
        Assert.Equal(3, game.DeathToll.QueuedProjectileHits);
    }

    [Fact]
    public void ArmorDamageCountsAsQueuedDamageBeforeAnyDeath()
    {
        var game = Battle(1, kind: ZombieKind.Armored, tier: GearTier.Iron);
        Fire(game, Assert.Single(Fronts(game)), 3);
        Assert.Equal(1, game.DeathToll.QueuedProjectileHits);
        Assert.Equal(3, game.DeathToll.QueuedDamage);
        Assert.Equal(0, game.DeathToll.PreSpawnQueueDeaths);
        Assert.Equal(0, game.DeathToll.BoundaryImpactsWithoutQueuedDamage);
    }

    [Fact]
    public void RatesUseCombatTimeAndResetWithSession()
    {
        var game = Battle(2);
        var first = Fronts(game).Single(slot => Slot(slot) == 0);
        Fire(game, first, 1_000_000);
        Assert.Equal(0, game.DeathToll.KillsPerSecond);
        game.Waves[0].SpawnTimer = 1000;
        game.Update(0); // Paused UI frames do not call Update; zero time is also ignored.
        Assert.Equal(0, game.DeathToll.CombatSeconds);
        game.Update(.05f);
        Assert.InRange(game.DeathToll.KillsPerSecond, 19.99, 20.01);
        Assert.InRange(game.DeathToll.QueueProcessingPerSecond, 0, .001);
        game.Waves[0].SpawnTimer = 1000;
        game.Update(60.1f);
        Assert.Equal(0, game.DeathToll.KillsPerSecond);
        Assert.Equal(0, game.DeathToll.QueueProcessingPerSecond);
        Assert.Equal(1, game.DeathToll.TotalDeaths);
        game.Reset();
        Assert.Equal(0, game.DeathToll.CombatSeconds);
        Assert.Equal(0, game.DeathToll.TotalDeaths);
        Assert.Equal(0, game.DeathToll.QueuedProjectileHits);
    }

    [Fact]
    public void LargeDeadDueBatchAddsProcessedSlotsWithoutNewKills()
    {
        var game = Battle(5000);
        for (int slot = 0; slot < 64; slot++)
            Fire(game, Fronts(game).Single(front => Slot(front) == slot), 1_000_000);
        Assert.Equal(64, game.DeathToll.PreSpawnQueueDeaths);
        Assert.Equal(0, game.DeathToll.ProcessedQueuedSlots);
        game.Waves[0].SpawnTimer = -630;
        Assert.True((bool)Invoke(game, "ScheduleLargeDueBatch", LargeWave(game), game.Waves[0], 10f)!);
        Assert.Equal(64, game.DeathToll.PreSpawnQueueDeaths);
        Assert.Equal(64, game.DeathToll.DeadScheduledSlotsProcessed);
        Assert.Equal(64, game.DeathToll.ProcessedQueuedSlots);
        Assert.Equal(4936, game.UnprocessedQueueEntries);
        Assert.Equal(0, game.DeathToll.PhysicalSpawns);
        game.Waves[0].SpawnTimer = 1000;
        game.Update(.05f);
        Assert.InRange(game.DeathToll.QueueProcessingPerSecond, 1279.9, 1280.1);
    }

    [Fact]
    public void SessionTotalsCrossLevelBoundariesButCheckpointLoadStartsANewObservation()
    {
        var game = Battle(1);
        Fire(game, Assert.Single(Fronts(game)), 1_000_000);
        game.Update(.01f);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(1, game.DeathToll.TotalDeaths);
        double firstLevelSeconds = game.DeathToll.CombatSeconds;
        Assert.True(game.StartLevel());
        Assert.Equal(1, game.DeathToll.TotalDeaths);
        Assert.Equal(firstLevelSeconds, game.DeathToll.CombatSeconds);

        // Checkpoints are preparation-only; load the one saved at the transition.
        game.Reset();
        FireAndFinish(game);
        string path = Path.Combine(Path.GetTempPath(), $"cvz-death-toll-{Guid.NewGuid():N}.json");
        try
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var loaded, out var error), error);
            Assert.NotNull(loaded);
            Assert.True(loaded.DeathTollStartedFromCheckpoint);
            Assert.Equal(0, loaded.DeathToll.TotalDeaths);
            Assert.Equal(0, loaded.DeathToll.CombatSeconds);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void VictoryCanLeaveDeadScheduledEntriesUnprocessedWithoutAnotherDeath()
    {
        var game = Battle(3);
        for (int slot = 0; slot < 3; slot++)
            Fire(game, Fronts(game).Single(front => Slot(front) == slot), 1_000_000);
        Assert.Equal(3, game.DeathToll.PreSpawnQueueDeaths);
        Assert.Equal(3, game.UnprocessedQueueEntries);
        game.Update(.01f);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(3, game.DeathToll.TotalDeaths);
        Assert.Equal(1, game.DeathToll.DeadScheduledSlotsProcessed);
        Assert.Equal(2, game.UnprocessedQueueEntries);
    }

    [Theory]
    [InlineData(18_000_000)]
    [InlineData(188_000_000)]
    public void HugeLazyRosterKeepsDiagnosticStateBounded(int count)
    {
        var game = Battle(count);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        long total = 0;
        for (int i = 0; i < 1000; i++)
        {
            total += game.UnprocessedQueueEntries;
            total += (long)game.DeathToll.KillsPerSecond;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal((long)count * 1000, total);
        Assert.True(allocated < 256_000, $"Diagnostic snapshots allocated {allocated:N0} bytes.");
        Fire(game, Fronts(game).First(), 1_000_000);
        Assert.Equal(1, game.DeathToll.PreSpawnQueueDeaths);
        Assert.Equal(count, game.UnprocessedQueueEntries);
    }

    private static GameSession Battle(int count, ZombieSize size = ZombieSize.Standard,
        ZombieKind kind = ZombieKind.Basic, GearTier tier = GearTier.Wood)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves = [new WaveDefinition { SpawnInterval = 10,
            Groups = [new ZombieGroup { Count = count, Size = size, Kind = kind, Tier = tier,
                IsBoss = size is ZombieSize.Kilo or ZombieSize.Mega }] }] });
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        return game;
    }

    private static object[] Fronts(GameSession game)
    {
        var stacks = (IDictionary)typeof(GameSession).GetField("_standbyStacks", Private)!.GetValue(game)!;
        return stacks.Values.Cast<object>()
            .Select(stack => stack.GetType().GetProperty("Front")!.GetValue(stack))
            .Where(front => front != null).Cast<object>().ToArray();
    }

    private static int Slot(object front) => (int)front.GetType().GetProperty("Slot")!.GetValue(front)!;

    private static object LargeWave(GameSession game) => ((IList)typeof(GameSession)
        .GetField("_largeStandbyWaves", Private)!.GetValue(game)!)[0]!;

    private static void FireAndFinish(GameSession game)
    {
        Assert.True(game.StartLevel());
        Fire(game, Assert.Single(Fronts(game)), 1_000_000);
        game.Update(.01f);
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    private static void Fire(GameSession game, object front, float damage)
    {
        var cell = ((Cell?)front.GetType().GetProperty("Cell")!.GetValue(front))!.Value;
        var group = (ZombieGroup)front.GetType().GetProperty("Group")!.GetValue(front)!;
        var shape = new Zombie { Size = group.Size };
        var position = shape.StandAt(cell);
        var inward = cell.X == 0 ? Vector3.UnitX : cell.X + shape.GridFootprint == WorldGrid.Size
            ? -Vector3.UnitX : cell.Z == 0 ? Vector3.UnitZ : -Vector3.UnitZ;
        game.Projectiles.Add(new Projectile { Origin = position + inward * 2,
            Target = position - inward * 2, Damage = damage, Kind = ShotKind.Pellet, Duration = .05f });
        Invoke(game, "UpdateProjectiles", .05f);
    }

    private static object? Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);
}
