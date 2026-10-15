using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloBucketTests
{
    [Fact]
    public void ANaturalArmoredKiloChoosesTwoBucketsForCentralLavaAndUsesBothLoads()
    {
        var catalog = new LevelCatalog();
        var game = new GameSession(levels: catalog);
        var checkpoint = game.ExportState();
        checkpoint.LevelNumber = 7;
        Invoke(game, "RestoreCheckpoint", checkpoint);
        for (int x = 12; x < 20; x++)
            for (int z = 12; z < 20; z++)
            {
                var cell = new Cell(x, 16, z);
                if (game.World.GetMaterial(cell) == Material.Air)
                    Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Lava, 10));
            }
        Assert.True(game.StartLevel(), game.Message);
        var plan = WaveSpawnPlanner.Plan(Assert.Single(catalog.Get(7).Waves));
        var wave = Assert.Single(game.Waves);
        wave.Spawned = Enumerable.Range(0, plan.SpawnCount)
            .First(index => WaveSpawnPlanner.SelectGroup(plan, index).IsBoss);

        Assert.True((bool)Invoke(game, "Spawn", wave)!);

        var kilo = Assert.Single(game.Zombies);
        Assert.Equal(ZombieSize.Kilo, kilo.Size);
        Assert.True(kilo.IsBoss);
        Assert.True(kilo.CarriesBucket);
        Assert.Equal(2, kilo.BucketCount);
        Assert.Equal(1, kilo.CarriedBlocks);
        Assert.Equal(4, kilo.ArmorPieces.Count);
        Assert.All(kilo.ArmorPieces, armor => Assert.Equal(GearTier.Wood, armor.Tier));
        Assert.Equal(10, wave.BudgetSpent);
        // Move the naturally equipped actor to the bank to isolate the actual
        // scoop/launch cycle from the separate full-distance navigation test.
        kilo.Position = kilo.StandAt(new Cell(12, 16, 10));
        kilo.FaceToward(Vector3.UnitZ);
        int initial = WorldUnits(game);

        Assert.True(Action(game, kilo));
        Assert.Equal(20, kilo.HeldBucketUnits);
        Assert.Equal(initial - 20, WorldUnits(game));
        FinishAction(kilo);
        Assert.False(Action(game, kilo));
        kilo.Position = kilo.StandAt(new Cell(0, 16, 10));
        Assert.True(Action(game, kilo));

        Assert.Equal(2, game.Buckets.Flights.Count);
        Assert.All(game.Buckets.Flights, flight => { Assert.Equal(10, flight.Units); Assert.Equal(LiquidKind.Lava, flight.Kind); });
        Assert.Equal(initial, WorldUnits(game) + game.Buckets.Flights.Sum(flight => flight.Units));
        Assert.Equal(4, kilo.ArmorPieces.Count);
        Assert.Empty(game.KiloCovers);
    }

    [Fact]
    public void OneScoopCycleFillsTwoIndependentBucketsFromRealReachableLiquids()
    {
        var game = new GameSession();
        var kilo = AddCarrier(game);
        AddSources(game, LiquidKind.Water, LiquidKind.Mercury);

        Assert.True(Action(game, kilo));

        Assert.Equal(2, kilo.BucketCount);
        Assert.Equal(20, kilo.HeldBucketUnits);
        Assert.Collection(kilo.BucketLoads,
            load => { Assert.Equal(10, load.Units); Assert.Equal(LiquidKind.Water, load.Kind); },
            load => { Assert.Equal(10, load.Units); Assert.Equal(LiquidKind.Mercury, load.Kind); });
        Assert.Equal(kilo.BucketLoads[0].Units, kilo.BucketUnits);
        Assert.Equal(kilo.BucketLoads[0].Kind, kilo.BucketKind);
        Assert.Equal(0, WorldUnits(game));
        Assert.Equal(1, kilo.CarriedBlocks);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void BothBucketsLaunchSeparateArcsWithoutCombiningTheirLiquidKinds()
    {
        var game = new GameSession();
        var kilo = AddCarrier(game);
        AddSources(game, LiquidKind.Water, LiquidKind.Mercury);
        Assert.True(Action(game, kilo));
        Assert.True(Action(game, kilo));
        Assert.Empty(game.Buckets.Flights);
        FinishAction(kilo);
        Assert.False(Action(game, kilo));
        kilo.Position = kilo.StandAt(new Cell(0, 16, 10));

        Assert.True(Action(game, kilo));

        Assert.Equal(0, kilo.HeldBucketUnits);
        Assert.All(kilo.BucketLoads, load => { Assert.Equal(0, load.Units); Assert.Null(load.Kind); });
        Assert.Equal(2, game.Buckets.Flights.Count);
        Assert.Equal(20, game.Buckets.Flights.Sum(flight => flight.Units));
        Assert.Contains(game.Buckets.Flights, flight => flight.Kind == LiquidKind.Water && flight.Units == 10);
        Assert.Contains(game.Buckets.Flights, flight => flight.Kind == LiquidKind.Mercury && flight.Units == 10);
        Assert.NotEqual(game.Buckets.Flights[0].Origin, game.Buckets.Flights[1].Origin);
        Assert.All(game.Buckets.Flights, flight =>
        {
            Assert.Equal(kilo.Position.Y - kilo.StandingHeight, flight.GetPositionAt(1).Y, 4);
            Assert.Equal(3, Vector2.Distance(new(flight.Origin.X, flight.Origin.Z),
                new(flight.GetPositionAt(1).X, flight.GetPositionAt(1).Z)), 4);
        });
        game.Buckets.Update(3);
        Assert.Equal(20, WorldUnits(game) + game.Buckets.PendingUnits + game.Buckets.Flights.Sum(flight => flight.Units) +
            Enum.GetValues<LiquidKind>().Sum(game.Liquids.GetEscapedUnits));
    }

    [Fact]
    public void DeathSpillsBothBucketsAndDropsItsUnspentBackpackBlockExactlyOnce()
    {
        var game = new GameSession();
        var kilo = AddCarrier(game);
        AddSources(game, LiquidKind.Water, LiquidKind.Mercury);
        Assert.True(Action(game, kilo));

        game.DamageZombie(kilo, 10_000, DamageKind.Chemical);
        game.DamageZombie(kilo, 10_000, DamageKind.Chemical);

        Assert.Equal(0, kilo.HeldBucketUnits);
        Assert.Equal(20, game.Buckets.PendingUnits);
        Assert.Equal(2, game.Buckets.PendingSplashes.Count);
        var block = Assert.Single(game.PendingBlockDrops);
        Assert.Equal(Material.Dirt, block.Content);
        Assert.Equal(kilo.Cell, block.Cell);
        Assert.False(block.WasBarrier);
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Empty(game.KiloCovers);
        game.Buckets.Update(.02f);
        Assert.Equal(20, WorldUnits(game) + game.Buckets.PendingUnits);
    }

    [Theory]
    [InlineData(ZombieSize.Standard)]
    [InlineData(ZombieSize.Milli)]
    public void SmallerWorkersKeepOneBucketCapacity(ZombieSize size)
    {
        var game = new GameSession();
        var worker = new Zombie { Id = -1, Size = size, CarriesBucket = true };
        worker.Position = worker.StandAt(new Cell(12, 16, 11));
        game.Zombies.Add(worker);
        AddSources(game, LiquidKind.Water, LiquidKind.Water);

        Assert.True(Action(game, worker));

        Assert.Equal(1, worker.BucketCount);
        Assert.Equal(10, worker.HeldBucketUnits);
        Assert.Equal(0, worker.BucketLoads[1].Units);
        Assert.Equal(0, worker.CarriedBlocks);
        Assert.Equal(10, WorldUnits(game));
    }

    [Fact]
    public void AnUnreachableSecondSourceDoesNotCreateASecondPayload()
    {
        var game = new GameSession();
        var kilo = AddCarrier(game);
        Assert.True(game.Liquids.TryAdd(new Cell(12, 16, 12), LiquidKind.Water, 10));
        Assert.True(game.Liquids.TryAdd(new Cell(17, 16, 17), LiquidKind.Water, 10));

        Assert.True(Action(game, kilo));

        Assert.Equal(10, kilo.HeldBucketUnits);
        Assert.Equal(10, WorldUnits(game));
        Assert.Equal(0, kilo.BucketLoads[1].Units);
    }

    [Fact]
    public void AKiloBucketRouteUsesItsWholeBodyClearanceAtTheBank()
    {
        var game = new GameSession();
        var kilo = AddCarrier(game, new Cell(7, 16, 9));
        AddSources(game, LiquidKind.Water, LiquidKind.Water);
        Assert.True((bool)Invoke(game, "HasReachableBucketWork", kilo)!);
        var current = kilo.Cell;
        var visited = new HashSet<Cell>();

        while (visited.Add(current))
        {
            object?[] route = [kilo, current, default(Cell)];
            Assert.True((bool)Invoke(game, "TryGetBucketRoute", route)!);
            var next = (Cell)route[2]!;
            Assert.True((bool)Invoke(game, "IsBodyClear", kilo.StandAt(next), kilo)!);
            if (next == current)
            {
                kilo.Position = kilo.StandAt(current);
                Assert.True(Action(game, kilo));
                Assert.InRange(kilo.HeldBucketUnits, 1, 20);
                return;
            }
            current = next;
        }
        Assert.Fail("The sized bucket route contains a cycle instead of reaching the actual shore.");
    }

    [Fact]
    public void ExistingCoverBlocksRemainRealWhileTheKiloUsesItsBuckets()
    {
        var game = new GameSession();
        var kilo = AddCarrier(game);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.Equal(2, game.KiloCovers.Count);
        var covers = game.KiloCovers.ToArray();
        AddSources(game, LiquidKind.Water, LiquidKind.Water);

        Assert.True(Action(game, kilo));

        Assert.Equal(20, kilo.HeldBucketUnits);
        Assert.Equal(covers.Select(cover => cover with { IsActive = false }), game.KiloCovers);
        Assert.Empty(game.PendingBlockDrops);
    }

    private static Zombie AddCarrier(GameSession game, Cell? anchor = null)
    {
        var kilo = new Zombie { Id = -1, Size = ZombieSize.Kilo, CarriesBucket = true };
        kilo.Position = kilo.StandAt(anchor ?? new Cell(12, 16, 10));
        game.Zombies.Add(kilo);
        return kilo;
    }

    private static void AddSources(GameSession game, LiquidKind first, LiquidKind second)
    {
        Assert.True(game.Liquids.TryAdd(new Cell(12, 16, 12), first, 10));
        Assert.True(game.Liquids.TryAdd(new Cell(13, 16, 12), second, 10));
    }

    private static bool Action(GameSession game, Zombie zombie) => (bool)Invoke(game, "TryBucketAction", zombie)!;
    private static void FinishAction(Zombie zombie)
    {
        typeof(GameSession).GetMethod("UpdateKiloEquipment", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [zombie, Balance.BucketActionSeconds]);
        zombie.Cooldown = 0;
    }
    private static int WorldUnits(GameSession game) => game.Liquids.Cells.Sum(cell => cell.Water + cell.Mercury + cell.Lava);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
