using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class LazyStandbySpawnTests
{
    [Fact]
    public void EighteenMillionSlotsStartWithBoundedFrontsAndAdvanceOneTargetAtATime()
    {
        var game = NewBattle(18_000_000);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(game.StartLevel());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64L * 1024 * 1024, $"StartLevel allocated {allocated:N0} bytes.");
        Assert.Equal(18_000_000, game.LivingStandbyCount);
        Assert.Empty(LegacySlots(game));
        Assert.InRange(Fronts(game).Length, 2, 512);

        var front = Fronts(game).OrderBy(Slot).First();
        var cell = CellOf(front);
        Fire(game, front, 1_000_000);
        Assert.Equal(17_999_999, game.LivingStandbyCount);
        Assert.Equal(0, BodyHp(front));
        var next = Fronts(game).Single(candidate => CellOf(candidate) == cell);
        Assert.True(Slot(next) > Slot(front));
        Assert.Equal(10, BodyHp(next));
        Assert.Equal(GamePhase.Combat, game.Phase);
    }

    [Fact]
    public void EachSeededBatchCoversEveryValidPositionOnceAndRepeatsWithTheSameSeed()
    {
        var game = NewBattle(5000);
        Assert.True(game.StartLevel());
        var large = LargeWave(game);
        var lane = ((IList)large.GetType().GetProperty("Lanes")!.GetValue(large)!)[0]!;
        int positions = ((Array)lane.GetType().GetProperty("Positions")!.GetValue(lane)!).Length;
        var positionIndex = lane.GetType().GetMethod("PositionIndex")!;
        int[] Batch(int start) => Enumerable.Range(start, positions).Select(ordinal =>
            (int)positionIndex.Invoke(lane, [ordinal])!).ToArray();
        Assert.Equal(Enumerable.Range(0, positions), Batch(0).Order());
        Assert.Equal(Enumerable.Range(0, positions), Batch(positions).Order());
        var firstOrder = Batch(0);
        game.Reset();
        Assert.True(game.StartLevel());
        lane = ((IList)LargeWave(game).GetType().GetProperty("Lanes")!.GetValue(LargeWave(game))!)[0]!;
        positionIndex = lane.GetType().GetMethod("PositionIndex")!;
        Assert.Equal(firstOrder, Batch(0));
    }

    [Fact]
    public void LargeWaveKeepsWoundsDeadGapsAndIndependentBlockedDeadlines()
    {
        var game = NewBattle(5000);
        Assert.True(game.StartLevel());
        var first = Fronts(game).Single(front => Slot(front) == 0);
        var second = Fronts(game).Single(front => Slot(front) == 1);
        Assert.NotEqual(CellOf(first), CellOf(second));
        Damage(game, second, 1_000_000);
        Damage(game, first, 3);
        var blocked = CellOf(first);
        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", blocked, Material.Dirt)!);

        game.Update(.01f);
        Assert.Equal(0, game.Waves[0].Spawned);
        game.Update(.99f);
        Assert.Equal(1, game.Waves[0].Spawned); // The dead second slot used its deadline.
        Assert.Empty(game.Zombies);
        Assert.True((bool)Invoke(game.World, "Dig", blocked)!);
        game.Update(.01f);
        Assert.Equal(2, game.Waves[0].Spawned);
        Assert.Equal(7, Assert.Single(game.Zombies).Hp);
        Assert.Equal(blocked, game.Zombies[0].Cell);
    }

    [Fact]
    public void BlockedLargeSlotDoesNotDelayALivingSlotAtAnotherPosition()
    {
        var game = NewBattle(5000);
        Assert.True(game.StartLevel());
        var first = Fronts(game).Single(front => Slot(front) == 0);
        var second = Fronts(game).Single(front => Slot(front) == 1);
        var blocked = CellOf(first);
        Assert.NotEqual(blocked, CellOf(second));
        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", blocked, Material.Dirt)!);
        game.Update(.01f);
        Assert.Equal(0, game.Waves[0].Spawned);
        game.Update(.99f);
        Assert.Equal(1, game.Waves[0].Spawned);
        Assert.Equal(CellOf(second), Assert.Single(game.Zombies).Cell);
        float deadline = game.Waves[0].SpawnTimer;
        Assert.True((bool)Invoke(game.World, "Dig", blocked)!);
        game.Update(.01f);
        Assert.Equal(2, game.Waves[0].Spawned);
        Assert.Contains(game.Zombies, zombie => zombie.Cell == blocked);
        Assert.InRange(game.Waves[0].SpawnTimer, deadline - .011f, deadline - .009f);
    }

    [Fact]
    public void IndependentlyBlockedLargeSlotsResumeInScheduledOrder()
    {
        var game = NewBattle(5000);
        Assert.True(game.StartLevel());
        var first = CellOf(Fronts(game).Single(front => Slot(front) == 0));
        var second = CellOf(Fronts(game).Single(front => Slot(front) == 1));
        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", first, Material.Dirt)!);
        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", second, Material.Dirt)!);
        game.Waves[0].SpawnTimer = 1;
        game.Update(2.01f);
        Assert.Equal(0, game.Waves[0].Spawned);
        game.Waves[0].SpawnTimer = 100;
        Assert.True((bool)Invoke(game.World, "Dig", second)!);
        Assert.True((bool)Invoke(game.World, "Dig", first)!);
        game.Update(.01f);
        Assert.Equal([first, second], game.Zombies.OrderBy(zombie => zombie.Id)
            .Select(zombie => zombie.Cell).ToArray());
    }

    [Fact]
    public void LargeMixedRosterFrontsMatchThePlannerAndKeepPositionOrder()
    {
        var catalog = new LevelCatalog();
        var definition = new WaveDefinition { SpawnInterval = 1, Groups =
        [
            new() { Kind = ZombieKind.Basic, Count = 3000 },
            new() { Kind = ZombieKind.Armored, Tier = GearTier.Iron, Count = 1300 },
            new() { Kind = ZombieKind.Basic, Size = ZombieSize.Kilo, IsBoss = true, Count = 2 }
        ] };
        catalog.Set(new LevelDefinition { Waves = [definition] });
        var plan = WaveSpawnPlanner.Plan(definition);
        var game = new GameSession(8, catalog);
        Assert.True(game.StartLevel());
        foreach (var front in Fronts(game))
            Assert.Equal(WaveSpawnPlanner.SelectGroup(plan, Slot(front)).Kind,
                ((ZombieGroup)front.GetType().GetProperty("Group")!.GetValue(front)!).Kind);
        var cell = CellOf(Fronts(game).OrderBy(Slot).First());
        int last = -1;
        for (int index = 0; index < 30; index++)
        {
            var front = Fronts(game).Single(slot => CellOf(slot) == cell);
            Assert.True(Slot(front) > last);
            last = Slot(front);
            Damage(game, front, 1_000_000);
        }
        Assert.Equal(plan.SpawnCount - 30, game.LivingStandbyCount);
    }

    [Fact]
    public void LargeScheduledArrivalsMatchReferenceRosterAndBatchPositions()
    {
        var definition = new WaveDefinition { SpawnInterval = 1, Groups =
        [
            new() { Kind = ZombieKind.Basic, Count = 3000 },
            new() { Kind = ZombieKind.Armored, Tier = GearTier.Iron, Count = 1200 },
            new() { Kind = ZombieKind.Armed, Tier = GearTier.Wood, Count = 100 }
        ] };
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves = [definition] });
        var plan = WaveSpawnPlanner.Plan(definition);
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        var lanes = (IList)LargeWave(game).GetType().GetProperty("Lanes")!.GetValue(LargeWave(game))!;
        var groupOrdinals = new int[plan.Groups.Count];
        for (int slot = 0; slot < 512; slot++)
        {
            var group = WaveSpawnPlanner.SelectGroup(plan, slot);
            int groupIndex = Enumerable.Range(0, plan.Groups.Count).Single(index =>
                ReferenceEquals(plan.Groups[index], group));
            var lane = lanes[groupIndex]!;
            int positionIndex = (int)lane.GetType().GetMethod("PositionIndex")!
                .Invoke(lane, [groupOrdinals[groupIndex]++])!;
            var positions = (Cell[])lane.GetType().GetProperty("Positions")!.GetValue(lane)!;
            Assert.True((bool)Invoke(game, "ScheduleNextStandby", game.Waves[0])!);
            var zombie = game.Zombies[slot];
            Assert.Equal(group.Kind, zombie.Kind);
            Assert.Equal(group.Tier, zombie.Tier);
            Assert.Equal(group.Size, zombie.Size);
            Assert.Equal(LevelCatalog.ZombieMaxHp(group.Kind, group.Tier, group.Size), zombie.Hp);
            Assert.Equal(positions[positionIndex], zombie.Cell);
        }
        Assert.Equal(512, game.Waves[0].Spawned);
    }

    [Fact]
    public void LargeGiantPreservesPartiallyDamagedCoverAtActivation()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves = [new WaveDefinition { SpawnInterval = 1,
            Groups = [new ZombieGroup { Count = 4097, Size = ZombieSize.Kilo, IsBoss = true }] }] });
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        var first = Fronts(game).Single(front => Slot(front) == 0);
        Damage(game, first, 13);
        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);
        var giant = Assert.Single(game.Zombies);
        Assert.Equal(0, giant.CarriedBlocks);
        Assert.Equal(Balance.DirtBlockHp - 13, Assert.Single(game.KiloCovers).Hp);
    }

    [Fact]
    public void LargeArmoredFrontActivatesWithItsDamagedArmor()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves = [new WaveDefinition { SpawnInterval = 1,
            Groups = [new ZombieGroup { Count = 5000, Kind = ZombieKind.Armored, Tier = GearTier.Iron }] }] });
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        var first = Fronts(game).Single(front => Slot(front) == 0);
        Fire(game, first, 3);
        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);
        var zombie = Assert.Single(game.Zombies);
        Assert.Equal(207, zombie.Hp);
        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(47, zombie.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Shield).Hp);
    }

    [Fact]
    public void PreparingLargeQueueDoesNotConsumeCombatRandomness()
    {
        var game = NewBattle(18_000_000);
        Assert.True(game.StartLevel());
        var combatRandom = (Random)typeof(GameSession).GetField("_random",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;
        Assert.Equal(new Random(12345).Next(), combatRandom.Next());
    }

    [Fact]
    public void LargeFrontDeathInvalidatesProjectileForecasts()
    {
        var game = NewBattle(5000);
        game.ProjectileDiagnostics = new ProjectileDiagnostics();
        Assert.True(game.StartLevel());
        var front = Fronts(game).OrderBy(Slot).First();
        var shot = Shot(front, 1);
        game.Projectiles.Add(shot);
        Invoke(game, "BeginMegaInterceptionFrame", .05f);
        Invoke(game, "NextMegaInterceptionTime", shot, .05f);
        long before = game.ProjectileDiagnostics.ForecastEvaluations;
        Invoke(game, "NextMegaInterceptionTime", shot, .05f);
        Assert.Equal(before, game.ProjectileDiagnostics.ForecastEvaluations);
        Damage(game, front, 1_000_000);
        Invoke(game, "NextMegaInterceptionTime", shot, .05f);
        Assert.Equal(before + 1, game.ProjectileDiagnostics.ForecastEvaluations);
        Invoke(game, "EndMegaInterceptionFrame");
    }

    [Fact]
    public void MaximumSupportedCountStartsWithoutIntegerOverflow()
    {
        var game = NewBattle(int.MaxValue);
        Assert.True(game.StartLevel());
        Assert.Equal(int.MaxValue, game.LivingStandbyCount);
        Assert.InRange(Fronts(game).Length, 2, 512);
    }

    [Fact]
    public void AggregateCountOverflowRejectsTheLevelBeforeAllocatingQueueState()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves =
        [
            new WaveDefinition { Groups = [new ZombieGroup { Count = int.MaxValue }] },
            new WaveDefinition { Groups = [new ZombieGroup { Count = 1 }] }
        ] });
        var game = new GameSession(12345, catalog);
        Assert.False(game.StartLevel());
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(0, game.LivingStandbyCount);
        Assert.Contains("integer range", game.Message);
    }

    [Fact]
    public void SkippingAlmostAnEntireHugeWaveDoesNotWalkItsRoster()
    {
        var game = NewBattle(18_000_000);
        Assert.True(game.StartLevel());
        game.Waves[0].SpawnTimer = 1000;
        game.Waves[0].Spawned = 17_999_999;
        game.Update(.001f);
        Assert.Equal(1, game.LivingStandbyCount);
        var final = Assert.Single(Fronts(game));
        Assert.Equal(17_999_999, Slot(final));
        Damage(game, final, 1_000_000);
        game.Update(.001f);
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    [Fact]
    public void BulkSkipCountsPreKilledSlotsWithoutRemovingAnExtraSurvivor()
    {
        var game = NewBattle(5000);
        Assert.True(game.StartLevel());
        Damage(game, Fronts(game).Single(front => Slot(front) == 0), 1_000_000);
        game.Waves[0].SpawnTimer = 1000;
        game.Waves[0].Spawned = 4999;
        game.Update(.001f);
        Assert.Equal(1, game.LivingStandbyCount);
        Assert.Equal(4999, game.Waves[0].Spawned);
        Assert.Equal(4999, Slot(Assert.Single(Fronts(game))));
    }

    [Fact]
    public void BulkSkipAfterBatchedDeadDeadlineKeepsTheFinalSurvivor()
    {
        var game = NewBattle(5000, .000000001f);
        Assert.True(game.StartLevel());
        Damage(game, Fronts(game).Single(front => Slot(front) == 0), 1_000_000);
        foreach (var cell in Fronts(game).Select(CellOf).Distinct())
            Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", cell, Material.Dirt)!);
        game.Update(.05f);
        Assert.Equal(1, game.Waves[0].Spawned);
        game.Waves[0].Spawned = 4999;
        game.Waves[0].SpawnTimer = 1000;
        game.Update(.001f);
        Assert.Equal(1, game.LivingStandbyCount);
        Assert.Equal(4999, Slot(Assert.Single(Fronts(game))));
    }

    [Fact]
    public void HugeDueJumpOverBlockedCellsKeepsEveryLivingSlotQueued()
    {
        var game = NewBattle(18_000_000, .000000001f);
        Assert.True(game.StartLevel());
        for (int index = 0; index < 256; index++)
        {
            var front = Fronts(game).OrderBy(Slot).First();
            Damage(game, front, 1_000_000);
        }
        foreach (var cell in Fronts(game).Select(CellOf).Distinct())
            Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", cell, Material.Dirt)!);
        game.Update(.05f);
        Assert.Equal(256, game.Waves[0].Spawned);
        Assert.Equal(18_000_000 - 256, game.LivingStandbyCount);
        Assert.Equal(18_000_000, ScheduledCursor(game));
        Assert.Empty(game.Zombies);
        Assert.Equal(GamePhase.Combat, game.Phase);
        game.Update(.05f);
        Assert.Equal(256, game.Waves[0].Spawned);
    }

    [Fact]
    public void SuccessiveDeathsAfterABatchedDeadlineEachCompleteTheirOwnDueSlot()
    {
        var game = NewBattle(5000, .000000001f);
        Assert.True(game.StartLevel());
        foreach (var cell in Fronts(game).Select(CellOf).Distinct())
            Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", cell, Material.Dirt)!);
        game.Update(.05f);
        Assert.Equal(0, game.Waves[0].Spawned);
        var cellOfStack = CellOf(Fronts(game).OrderBy(Slot).First());
        for (int index = 1; index <= 2; index++)
        {
            Damage(game, Fronts(game).Single(front => CellOf(front) == cellOfStack), 1_000_000);
            Assert.Equal(index, game.Waves[0].Spawned);
        }
        Assert.Equal(4998, game.LivingStandbyCount);
    }

    [Fact]
    public void InitiallySealedBoundaryMaterializesFrontsWhenACellOpens()
    {
        var game = NewBattle(5000);
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int z = 0; z < WorldGrid.Size; z++)
            {
                if (x != 0 && x != WorldGrid.Size - 1 && z != 0 && z != WorldGrid.Size - 1) continue;
                for (int y = 16; y < WorldGrid.Size; y++)
                {
                    var cell = new Cell(x, y, z);
                    if (game.World.GetMaterial(cell) == Material.Air)
                        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", cell, Material.Dirt)!);
                }
            }
        Assert.True(game.StartLevel());
        Assert.Empty(Fronts(game));
        Assert.Equal(5000, game.LivingStandbyCount);
        game.Waves[0].SpawnTimer = 100;
        game.Update(.01f);
        Assert.Empty(game.Zombies);
        var opening = new Cell(0, 16, 0);
        Assert.True((bool)Invoke(game.World, "Dig", opening)!);
        game.Update(.01f);
        var front = Assert.Single(Fronts(game));
        Assert.Equal(opening, CellOf(front));
        Damage(game, front, 1);
        Assert.Equal(9, BodyHp(front));
        Assert.Equal(5000, game.LivingStandbyCount);
    }

    [Fact]
    public void SkippingALargeWaveLeavesTheFollowingSmallWaveAvailable()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves =
        [
            new WaveDefinition { Groups = [new ZombieGroup { Count = 5000 }] },
            new WaveDefinition { Groups = [new ZombieGroup { Count = 1 }] }
        ] });
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        Assert.Equal(5001, game.LivingStandbyCount);
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        game.Update(.01f);
        Assert.Equal(1, game.Waves[1].Spawned);
        Assert.Equal(0, game.LivingStandbyCount);
        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.Single(game.Zombies);
    }

    [Fact]
    public void LargeWaveMergeUsesPreciseDamageWhenFloatProgressStopsMoving()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves =
        [
            new WaveDefinition { Groups = [new ZombieGroup { Count = 18_000_000 }] },
            new WaveDefinition { Groups = [new ZombieGroup { Count = 1 }] }
        ] });
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        var large = LargeWave(game);
        double half = (double)large.GetType().GetProperty("ExactTotalHp")!.GetValue(large)! / 2;
        large.GetType().GetProperty("ExactDamageTaken")!.SetValue(large, half - 1);
        game.Waves[0].DamageTaken = (float)half - 16;
        game.Waves[0].SpawnTimer = 100;
        Damage(game, Fronts(game).Single(front => Slot(front) == 0), 1);
        Assert.True(game.Waves[0].DamageTaken < game.Waves[0].TotalHp * .5f);
        game.Update(.001f);
        Assert.True(game.Waves[1].Started);
    }

    [Fact]
    public void LargeWaveKilledBeforeArrivalClearsOnceWithoutWalkingDeadTail()
    {
        var game = NewBattle(4097);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(5, 16, 5)));
        int before = game.Economy.Currency;
        Assert.True(game.StartLevel());
        while (game.LivingStandbyCount > 0)
            Damage(game, Fronts(game).OrderBy(Slot).First(), 1_000_000);
        Assert.Equal(0, game.Waves[0].Spawned);
        game.Update(.01f);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(before + Balance.SurvivorIncome, game.Economy.Currency);
        game.Update(1);
        Assert.Equal(before + Balance.SurvivorIncome, game.Economy.Currency);
    }

    [Fact]
    public void ResetRepeatsLargeWavePositionsWithoutGrowingUntouchedState()
    {
        var game = NewBattle(18_000_000);
        Assert.True(game.StartLevel());
        var first = Fronts(game).OrderBy(Slot).First();
        var cell = CellOf(first);
        for (int count = 0; count < 256; count++)
            Damage(game, Fronts(game).Single(front => CellOf(front) == cell), 1_000_000);
        Assert.Equal(18_000_000 - 256, game.LivingStandbyCount);
        Assert.InRange(Fronts(game).Length, 2, 512);
        var dead = LargeWave(game).GetType().GetProperty("Dead")!.GetValue(LargeWave(game))!;
        var chunks = (IDictionary)dead.GetType().GetField("_chunks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(dead)!;
        Assert.InRange(chunks.Count, 1, 16);
        game.Reset();
        Assert.Equal(0, game.LivingStandbyCount);
        Assert.True(game.StartLevel());
        Assert.Equal(cell, CellOf(Fronts(game).Single(front => Slot(front) == 0)));
        Assert.Equal(18_000_000, game.LivingStandbyCount);
    }

    private static GameSession NewBattle(int count, float interval = 1)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves = [new WaveDefinition { SpawnInterval = interval,
            Groups = [new ZombieGroup { Count = count }] }] });
        return new GameSession(12345, catalog);
    }

    private static object LargeWave(GameSession game) => ((IList)typeof(GameSession)
        .GetField("_largeStandbyWaves", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(game)!)[0]!;

    private static int ScheduledCursor(GameSession game) => ((int[])typeof(GameSession)
        .GetField("_scheduledNormalSlots", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(game)!)[0];

    private static Array LegacySlots(GameSession game) => (Array)((IList)typeof(GameSession)
        .GetField("_standbyWaves", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(game)!)[0]!;

    private static object[] Fronts(GameSession game)
    {
        var stacks = (IDictionary)typeof(GameSession).GetField("_standbyStacks",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;
        return stacks.Values.Cast<object>().Select(stack => stack.GetType().GetProperty("Front")!.GetValue(stack))
            .Where(front => front != null).Cast<object>().ToArray();
    }

    private static int Slot(object front) => (int)front.GetType().GetProperty("Slot")!.GetValue(front)!;
    private static float BodyHp(object front) => (float)front.GetType().GetProperty("BodyHp")!.GetValue(front)!;
    private static Cell CellOf(object front) => ((Cell?)front.GetType().GetProperty("Cell")!.GetValue(front)!)!.Value;
    private static Projectile Shot(object front, float damage)
    {
        var cell = CellOf(front);
        var group = (ZombieGroup)front.GetType().GetProperty("Group")!.GetValue(front)!;
        var shape = new Zombie { Size = group.Size };
        var target = shape.StandAt(cell);
        var direction = cell.X == 0 ? Vector3.UnitX : cell.X + shape.GridFootprint == WorldGrid.Size
            ? -Vector3.UnitX : cell.Z == 0 ? Vector3.UnitZ : -Vector3.UnitZ;
        return new Projectile { Origin = target + direction * 2, Target = target, Damage = damage,
            Kind = ShotKind.Pellet, Duration = .05f };
    }

    private static void Fire(GameSession game, object front, float damage)
    {
        game.Projectiles.Add(Shot(front, damage));
        Invoke(game, "UpdateProjectiles", .05f);
    }

    private static void Damage(GameSession game, object front, float amount) => Invoke(game, "DamageStandby", front,
        amount, DamageKind.Chemical, ShotKind.Pellet, Vector3.UnitX);
    private static object? Invoke(object target, string method, params object[] args) => target.GetType()
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
}
