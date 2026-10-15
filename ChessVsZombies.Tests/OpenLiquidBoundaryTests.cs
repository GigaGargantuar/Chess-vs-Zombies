using System.Numerics;
using System.Reflection;
using System.Text.Json;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class OpenLiquidBoundaryTests
{
    [Theory]
    [InlineData(LiquidKind.Water)]
    [InlineData(LiquidKind.Mercury)]
    [InlineData(LiquidKind.Lava)]
    public void AnExposedOutsideFaceDrainsRealLiquidAndRetainsItsEscapeAccounting(LiquidKind kind)
    {
        var liquids = new LiquidSystem(new WorldGrid());
        var edge = new Cell(0, 16, 8);
        Assert.True(liquids.TryAdd(edge, kind, 10));

        liquids.Update(.1f, cell => cell != edge);

        Assert.Equal(0, liquids.GetTotalUnits(edge));
        Assert.Equal(10, liquids.GetEscapedUnits(kind));
        var restored = new LiquidSystem(new WorldGrid());
        restored.RestoreState(JsonSerializer.Deserialize<LiquidState>(JsonSerializer.Serialize(liquids.ExportState()))!);
        Assert.Equal(10, restored.GetEscapedUnits(kind));
        Assert.Empty(restored.Cells);
    }

    [Fact]
    public void ASolidCrustSealsItsOutsideFaceUntilItBreaks()
    {
        var world = new WorldGrid();
        var liquids = new LiquidSystem(world);
        var edge = new Cell(0, 16, 8);
        liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(edge, 2, CrustFace.West, 10)],
            Cells = [new(edge, 8, 0, 0, 2, CrustFace.West)]
        });

        liquids.Update(.1f, cell => cell != edge);

        Assert.Equal(8, liquids.GetUnits(edge, LiquidKind.Water));
        Assert.Equal(0, liquids.GetEscapedUnits(LiquidKind.Water));
        Invoke(world, "DamageObsidian", edge, 10f);
        liquids.Update(.1f, cell => cell != edge);
        Assert.Equal(0, liquids.GetTotalUnits(edge));
        Assert.Equal(8, liquids.GetEscapedUnits(LiquidKind.Water));
    }

    [Fact]
    public void OldLiquidSnapshotsDefaultToNoEscapesAndAnInvalidCounterCannotPartiallyRestore()
    {
        var liquids = new LiquidSystem(new WorldGrid());
        const string oldJson = "{\"Cells\":[],\"Obsidian\":[],\"Hazards\":[],\"Accumulator\":0,\"FlowTick\":0}";
        liquids.RestoreState(JsonSerializer.Deserialize<LiquidState>(oldJson)!);
        Assert.All(Enum.GetValues<LiquidKind>(), kind => Assert.Equal(0, liquids.GetEscapedUnits(kind)));
        Assert.Equal(0, liquids.SuppliedWaterUnits);
        var cell = new Cell(8, 16, 8);
        Assert.True(liquids.TryAdd(cell, LiquidKind.Water, 7));
        var bad = liquids.ExportState();
        bad.EscapedLavaUnits = -1;

        Assert.Throws<InvalidDataException>(() => liquids.RestoreState(bad));

        Assert.Equal(7, liquids.GetUnits(cell, LiquidKind.Water));
        Assert.Equal(0, liquids.GetEscapedUnits(LiquidKind.Lava));
    }

    [Fact]
    public void AWholeMegaBucketLeavesTheActualVolumeWithoutBeingClampedBackIntoIt()
    {
        var liquids = new LiquidSystem(new WorldGrid());
        var buckets = new LiquidBucketSystem(new WorldGrid(), liquids);
        Assert.True(buckets.Launch(new Vector3(30.5f, 20, 8), Vector3.UnitX, LiquidKind.Mercury, 15));

        buckets.Update(2);

        Assert.Empty(buckets.Flights);
        Assert.Empty(buckets.PendingSplashes);
        Assert.Empty(liquids.Cells);
        Assert.Equal(15, liquids.GetEscapedUnits(LiquidKind.Mercury));
    }

    [Fact]
    public void AnOlderAboveCeilingArcBecomesOneEscapedParcelAndStaysEscapedAfterSavingAgain()
    {
        var world = new WorldGrid();
        var liquids = new LiquidSystem(world);
        var buckets = new LiquidBucketSystem(world, liquids);
        var old = new BucketSystemState
        {
            Flights = [new(new Vector3(8, 31.8f, 8), Vector3.UnitX, LiquidKind.Water, 10, .2f)]
        };

        buckets.RestoreState(old);

        Assert.Empty(buckets.Flights);
        Assert.Empty(buckets.PendingSplashes);
        Assert.Empty(liquids.Cells);
        Assert.Equal(10, liquids.GetEscapedUnits(LiquidKind.Water));
        var nextWorld = new WorldGrid();
        var nextLiquids = new LiquidSystem(nextWorld);
        nextLiquids.RestoreState(JsonSerializer.Deserialize<LiquidState>(JsonSerializer.Serialize(liquids.ExportState()))!);
        var nextBuckets = new LiquidBucketSystem(nextWorld, nextLiquids);
        nextBuckets.RestoreState(JsonSerializer.Deserialize<BucketSystemState>(JsonSerializer.Serialize(buckets.ExportState()))!);
        nextBuckets.Update(5);
        Assert.Empty(nextBuckets.Flights);
        Assert.Equal(10, nextLiquids.GetEscapedUnits(LiquidKind.Water));
    }

    [Fact]
    public void ADefectiveLaterParcelRejectsTheLegacyCeilingMigrationBeforeAnyStateChanges()
    {
        var world = new WorldGrid();
        var liquids = new LiquidSystem(world);
        var buckets = new LiquidBucketSystem(world, liquids);
        Assert.True(buckets.Launch(new Vector3(8, 20, 8), Vector3.UnitZ, LiquidKind.Mercury, 7));
        var before = buckets.ExportState();
        var old = new BucketSystemState
        {
            Flights = [new(new Vector3(8, 31.8f, 8), Vector3.UnitX, LiquidKind.Water, 10, .2f)],
            PendingSplashes = [new(new Vector3(8, 16, 8), LiquidKind.Lava, 16)]
        };

        Assert.Throws<InvalidDataException>(() => buckets.RestoreState(old));

        Assert.Equal(before.Flights, buckets.ExportState().Flights);
        Assert.Empty(buckets.PendingSplashes);
        Assert.Equal(0, liquids.GetEscapedUnits(LiquidKind.Water));
    }

    [Fact]
    public void MegaScoopingCollectsFifteenRealUnitsPerBucketAcrossSeveralCells()
    {
        var game = new GameSession();
        var mega = new Zombie { Id = -1, Size = ZombieSize.Mega };
        mega.Position = mega.StandAt(new Cell(11, 16, 10));
        game.Zombies.Add(mega);
        for (int x = 11; x <= 14; x++) Assert.True(game.Liquids.TryAdd(new Cell(x, 16, 12), LiquidKind.Water, 10));

        Assert.True((bool)Invoke(game, "TryBucketAction", mega)!);

        Assert.Equal(2, mega.BucketCount);
        Assert.Equal(15, mega.BucketCapacity);
        Assert.All(mega.BucketLoads, load => { Assert.Equal(15, load.Units); Assert.Equal(LiquidKind.Water, load.Kind); });
        Assert.Equal(10, game.Liquids.Cells.Sum(cell => cell.Water));
        Assert.True(game.IsKiloUsingBuckets(mega));
        Assert.Empty(game.Buckets.Flights);
    }

    [Fact]
    public void AFullWorkerPlansARealDryJourneyToTheEdgeBeforeDisposingItsLoad()
    {
        var game = new GameSession();
        var worker = new Zombie { Id = -1, CarriesBucket = true, Position = new Cell(8, 16, 8).Center };
        game.Zombies.Add(worker);
        Set(worker.BucketLoads[0], nameof(ZombieBucketLoad.Kind), LiquidKind.Mercury);
        Set(worker.BucketLoads[0], nameof(ZombieBucketLoad.Units), 10);

        Assert.False((bool)Invoke(game, "TryBucketAction", worker)!);
        Assert.Equal(10, worker.HeldBucketUnits);
        Assert.Empty(game.Buckets.Flights);
        var current = worker.Cell;
        var visited = new HashSet<Cell>();
        while (visited.Add(current))
        {
            object?[] route = [worker, current, default(Cell)];
            Assert.True((bool)Invoke(game, "TryGetBucketRoute", route)!);
            var next = (Cell)route[2]!;
            if (next == current) break;
            Assert.InRange(Math.Abs(next.Y - current.Y), 0, 1);
            Assert.True((bool)Invoke(game, "IsBodyClear", worker.StandAt(next), worker)!);
            current = next;
        }
        Assert.True(current.X is 0 or 31 || current.Z is 0 or 31);
        worker.Position = worker.StandAt(current);
        Assert.True((bool)Invoke(game, "TryBucketAction", worker)!);
        game.Buckets.Update(2);
        Assert.Equal(0, worker.HeldBucketUnits);
        Assert.Empty(game.Liquids.Cells);
        Assert.Equal(10, game.Liquids.GetEscapedUnits(LiquidKind.Mercury));
    }

    [Theory]
    [InlineData(ZombieSize.Standard, 10)]
    [InlineData(ZombieSize.Kilo, 20)]
    [InlineData(ZombieSize.Mega, 30)]
    public void RetreatSuppliesTheSameLivingZombieWithCapacitySizedWaterForTheLavaBarrier(ZombieSize size, int expectedWater)
    {
        var game = new GameSession();
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        for (int x = 12; x < 20; x++)
            for (int z = 12; z < 20; z++)
            {
                var cell = new Cell(x, 16, z);
                if (game.World.GetMaterial(cell) == Material.Air) Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Lava, 10));
            }
        var zombie = new Zombie { Id = -1, WaveIndex = 0, Size = size, Attack = 1 };
        zombie.Position = zombie.StandAt(new Cell(0, 16, 14));
        game.Zombies.Add(zombie);
        game.DamageZombie(zombie, 1, DamageKind.Chemical);
        float hp = zombie.BodyHp;
        long spent = game.Waves[0].BudgetSpent;
        Invoke(game, "BeginZombieRetreat", zombie);

        Assert.True((bool)Invoke(game, "TryCompleteRetreat", zombie, 1.1f)!);

        Assert.False(game.IsZombieRetreating(zombie));
        Assert.Equal(1, zombie.RetreatReturns);
        Assert.True(zombie.CarriesBucket);
        Assert.Equal(expectedWater, zombie.HeldBucketUnits);
        Assert.All(zombie.BucketLoads.Take(zombie.BucketCount), load => Assert.Equal(LiquidKind.Water, load.Kind));
        Assert.Equal(expectedWater, game.Liquids.SuppliedWaterUnits);
        Assert.Equal(hp, zombie.BodyHp);
        Assert.Equal(spent, game.Waves[0].BudgetSpent);
        Assert.DoesNotContain(game.Liquids.Cells, cell => cell.Water > 0);
    }

    [Fact]
    public void SuppliedWaterIsThrownOntoLavaAndProducesRealObsidianAndSteam()
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(new Cell(14, 16, 15), LiquidKind.Lava, 10));
        var worker = new Zombie { Id = -1, CarriesBucket = true, Position = new Cell(11, 16, 15).Center };
        game.Zombies.Add(worker);
        Set(worker.BucketLoads[0], nameof(ZombieBucketLoad.Kind), LiquidKind.Water);
        Set(worker.BucketLoads[0], nameof(ZombieBucketLoad.Units), 10);

        Assert.True((bool)Invoke(game, "TryBucketAction", worker)!);

        var flight = Assert.Single(game.Buckets.Flights);
        Assert.Equal(LiquidKind.Water, flight.Kind);
        Assert.True(flight.Direction.X > .9f, "Useful water was thrown outward instead of toward the lava.");
        game.Buckets.Update(2);
        game.Liquids.Update(.1f);
        Assert.NotEmpty(game.World.ObsidianCells);
        Assert.Contains(game.Liquids.Hazards, cloud => cloud.Kind == DamageKind.Thermal);
        Assert.Equal(0, game.Liquids.GetEscapedUnits(LiquidKind.Water));
    }

    [Fact]
    public void BuriedLavaDoesNotTrapAFullWaterWorkerInAnUnusableJob()
    {
        var game = new GameSession();
        var pool = new Cell(14, 16, 14);
        Assert.True(game.Liquids.TryAdd(pool, LiquidKind.Lava, 5));
        Assert.True(game.Liquids.TryAdd(pool, LiquidKind.Water, 5));
        var worker = new Zombie { Id = -1, CarriesBucket = true, Position = new Cell(11, 16, 14).Center };
        game.Zombies.Add(worker);
        Set(worker.BucketLoads[0], nameof(ZombieBucketLoad.Kind), LiquidKind.Water);
        Set(worker.BucketLoads[0], nameof(ZombieBucketLoad.Units), 10);

        Assert.False((bool)Invoke(game, "CarriesWaterForLava", worker)!);
        Assert.False((bool)Invoke(game, "TryBucketAction", worker)!);
        object?[] route = [worker, worker.Cell, default(Cell)];
        Assert.True((bool)Invoke(game, "TryGetBucketRoute", route)!);

        var next = (Cell)route[2]!;
        Assert.True(next.X < worker.Cell.X, "The full worker did not start carrying its unusable water toward the nearest edge.");
        Assert.Equal(10, worker.HeldBucketUnits);
        Assert.Empty(game.Buckets.Flights);
    }

    private static void Set(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static object? Invoke(object target, string method, params object?[] arguments) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
