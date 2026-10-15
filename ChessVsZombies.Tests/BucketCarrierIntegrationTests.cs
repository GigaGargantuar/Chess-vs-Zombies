using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class BucketCarrierIntegrationTests
{
    [Fact]
    public void AShoreWorkerScoopsHoldsAndThrowsItsConservedPayloadThroughPublicUpdates()
    {
        var game = CreateDefense();
        var source = new Cell(12, 16, 14);
        Assert.True(game.Liquids.TryAdd(source, LiquidKind.Water, 10));
        var worker = BeginWithWorker(game, new Cell(11, 16, 14));
        int initial = TotalOwnedLiquid(game);
        var shorePosition = worker.Position;

        game.Update(.02f);

        Assert.Equal(10, worker.BucketUnits);
        Assert.Equal(LiquidKind.Water, worker.BucketKind);
        Assert.Equal(0, game.Liquids.GetUnits(source, LiquidKind.Water));
        Assert.Equal(0, worker.CarriedBlocks);
        Assert.Empty(game.Buckets.Flights);
        Assert.Equal(initial, TotalOwnedLiquid(game));
        game.Update(.45f);
        Assert.Equal(shorePosition, worker.Position);
        Assert.Equal(10, worker.BucketUnits);
        Assert.Empty(game.Buckets.Flights);
        Assert.Equal(initial, TotalOwnedLiquid(game));

        AdvanceUntil(game, 2, () => game.Buckets.Flights.Count > 0, () => Assert.Equal(initial, TotalOwnedLiquid(game)));

        var flight = Assert.Single(game.Buckets.Flights);
        Assert.Equal(0, worker.BucketUnits);
        Assert.Null(worker.BucketKind);
        Assert.Equal(10, flight.Units);
        Assert.Equal(LiquidKind.Water, flight.Kind);
        var nominalLanding = flight.GetPositionAt(1);
        Assert.Equal(shorePosition.Y - .5f, nominalLanding.Y, 4);
        Assert.Equal(3, Vector2.Distance(new Vector2(flight.Origin.X, flight.Origin.Z),
            new Vector2(nominalLanding.X, nominalLanding.Z)), 4);
        Assert.True(Vector3.Dot(flight.Direction, shorePosition - new Vector3(16, shorePosition.Y, 16)) > 0,
            "A clearing worker threw its payload deeper toward the objective.");

        AdvanceUntil(game, 3, () => game.Buckets.Flights.Count == 0, () => Assert.Equal(initial, TotalOwnedLiquid(game)));
        Assert.Empty(game.Buckets.Flights);
        Assert.Equal(initial, TotalOwnedLiquid(game));
        Assert.Equal(10, game.Liquids.Cells.Sum(cell => cell.Water) + game.Buckets.PendingUnits);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Empty(game.World.ObsidianCells);
    }

    [Fact]
    public void ADeadWorkerSpillsItsHeldLiquidOnceAndNeverDropsABlock()
    {
        var game = CreateDefense();
        Assert.True(game.Liquids.TryAdd(new Cell(12, 16, 14), LiquidKind.Water, 10));
        var worker = BeginWithWorker(game, new Cell(11, 16, 14));
        var terrain = SnapshotTerrain(game.World);
        game.Update(.02f);
        Assert.Equal(10, worker.BucketUnits);

        game.DamageZombie(worker, 10_000, DamageKind.Chemical);

        Assert.Equal(0, worker.Hp);
        Assert.Equal(0, worker.BucketUnits);
        Assert.Null(worker.BucketKind);
        Assert.Equal(0, worker.CarriedBlocks);
        Assert.Equal(10, game.Buckets.PendingUnits);
        Assert.Equal(10, TotalOwnedLiquid(game));
        Assert.Empty(game.PendingBlockDrops);
        game.DamageZombie(worker, 10_000, DamageKind.Chemical);
        Assert.Equal(10, TotalOwnedLiquid(game));
        game.Update(.4f);

        Assert.DoesNotContain(worker, game.Zombies);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(10, TotalOwnedLiquid(game));
        Assert.Equal(terrain, SnapshotTerrain(game.World));
        Assert.Empty(game.PendingBlockDrops);
        Assert.Empty(game.World.DamagedTerrain);
    }

    [Fact]
    public void AnAutomaticallySpawnedBucketWorkerReachesTheLavaShoreOfAFloatingCorePit()
    {
        var game = CreateDefense();
        ExcavateFloatingCorePit(game);
        int initial = TotalOwnedLiquid(game);
        Assert.True(game.StartLevel(), game.Message);
        game.Update(.02f);
        var worker = Assert.Single(game.Zombies);
        var origin = worker.Position;
        Assert.True(worker.CarriesBucket, "A liquid-disconnected approach did not equip the scheduled bucket carrier.");
        bool collected = worker.BucketUnits > 0;
        int maximumHeld = worker.BucketUnits;

        AdvanceUntil(game, 90, () => collected || worker.Hp <= 0, () =>
        {
            collected |= worker.BucketUnits > 0 || game.Buckets.Flights.Count > 0;
            maximumHeld = Math.Max(maximumHeld, worker.BucketUnits);
            Assert.Equal(initial, TotalOwnedLiquid(game));
        });

        Assert.True(worker.Hp > 0, $"The bucket worker died before collecting any liquid at {worker.Position}.");
        Assert.True(collected,
            $"The bucket worker never collected from the lava shore; origin {origin}, current {worker.Position}, hiding {game.IsZombieHiding(worker)}.");
        Assert.InRange(maximumHeld, 1, Balance.BucketCapacity);
        Assert.True(Vector3.Distance(origin, worker.Position) > 5,
            "The bucket worker did not physically approach from its perimeter spawn.");
        Assert.Equal(0, worker.CarriedBlocks);
        Assert.Equal(initial, TotalOwnedLiquid(game));
        Assert.Equal(1, game.Waves[0].Spawned);
        Assert.Equal(1, game.Waves[0].TotalCount);
    }

    private static GameSession CreateDefense()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Name = "Bucket carrier integration",
            Waves = [new() { Groups = [new() { Kind = ZombieKind.Basic, Count = 1 }] }]
        });
        return new GameSession(12345, levels);
    }

    private static Zombie BeginWithWorker(GameSession game, Cell position)
    {
        Assert.True(game.StartLevel(), game.Message);
        // Reserve the existing wave budget for this explicit shore placement;
        // all collection, holding, launches, movement, and death use public simulation.
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var worker = new Zombie
        {
            Id = -1, WaveIndex = 0, Kind = ZombieKind.Basic, Tier = GearTier.Wood,
            CarriesBucket = true, Position = position.Center, Attack = Balance.BaseAttack, Lane = 0
        };
        game.Zombies.Add(worker);
        return worker;
    }

    private static void ExcavateFloatingCorePit(GameSession game)
    {
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        for (int inset = 0; inset < 4; inset++)
            for (int x = 12 + inset; x <= 19 - inset; x++)
                for (int z = 12 + inset; z <= 19 - inset; z++)
                {
                    var cell = new Cell(x, 15 - inset, z);
                    if (WorldGrid.IsCore(cell)) continue;
                    Assert.True(dig(cell));
                    Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Lava,
                        inset == 0 ? 1 : LiquidSystem.UnitsPerBlock));
                }
    }

    private static int TotalOwnedLiquid(GameSession game) =>
        game.Liquids.Cells.Sum(cell => cell.Water + cell.Mercury + cell.Lava) +
        game.Zombies.Sum(zombie => zombie.BucketUnits) +
        game.Buckets.Flights.Sum(flight => flight.Units) + game.Buckets.PendingUnits;

    private static void AdvanceUntil(GameSession game, float seconds, Func<bool> complete, Action? inspect = null)
    {
        for (float elapsed = 0; elapsed < seconds && !complete(); elapsed += .05f)
        {
            game.Update(.05f);
            inspect?.Invoke();
        }
    }

    private static Material[] SnapshotTerrain(WorldGrid world)
    {
        var terrain = new Material[WorldGrid.Size * WorldGrid.Size * WorldGrid.Size];
        int index = 0;
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 0; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++) terrain[index++] = world.GetNaturalMaterial(new Cell(x, y, z));
        return terrain;
    }
}
