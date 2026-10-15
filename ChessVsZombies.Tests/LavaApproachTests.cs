using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class LavaApproachTests
{
    [Theory]
    [InlineData(0, 16, 16)]
    [InlineData(31, 16, 16)]
    [InlineData(16, 16, 0)]
    [InlineData(16, 16, 31)]
    public void ABlockCarrierApproachesALavaFilledPitEvenWithoutACompleteCoreRoute(int x, int y, int z)
    {
        var game = CreateDefense(1, lavaPit: true);
        Assert.True(game.StartLevel(), game.Message);
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var origin = new Cell(x, y, z).Center;
        var carrier = new Zombie
        {
            Id = -1, WaveIndex = 0, Kind = ZombieKind.Basic, Position = origin,
            Attack = Balance.BaseAttack, Lane = 0
        };
        game.Zombies.Add(carrier);
        var terrain = SnapshotTerrain(game.World);
        int currency = game.Economy.Currency;

        for (int step = 0; step < 30; step++) game.Update(.05f);

        var center = new Vector2(16, 16);
        float previousDistance = Vector2.Distance(new Vector2(origin.X, origin.Z), center);
        float currentDistance = Vector2.Distance(new Vector2(carrier.Position.X, carrier.Position.Z), center);
        Assert.True(currentDistance < previousDistance - .5f,
            $"The carrier stayed at the spawn perimeter despite reachable terrain toward the lava: {carrier.Position}.");
        Assert.Equal(Balance.ZombieHp, carrier.Hp);
        Assert.Equal(1, carrier.CarriedBlocks);
        Assert.False(carrier.CarriesBucket);
        Assert.Equal(currency, game.Economy.Currency);
        Assert.Equal(terrain, SnapshotTerrain(game.World));
        Assert.Empty(game.World.DamagedTerrain);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void ADisconnectedLavaPitAddsOneBucketCarrierForEveryFiveSpawnedZombies()
    {
        var game = CreateDefense(5, lavaPit: true);
        Assert.True(game.StartLevel(), game.Message);

        game.Update(.05f);

        Assert.Equal(5, game.Waves[0].Spawned);
        Assert.Equal(5, game.Zombies.Count);
        var bucketCarrier = Assert.Single(game.Zombies, zombie => zombie.CarriesBucket);
        Assert.Equal(0, bucketCarrier.CarriedBlocks);
        Assert.All(game.Zombies.Where(zombie => !zombie.CarriesBucket), zombie => Assert.Equal(1, zombie.CarriedBlocks));
        Assert.All(game.Zombies, zombie => Assert.Equal(Balance.ZombieHp, zombie.Hp));
    }

    [Fact]
    public void AReachableDryMapKeepsTheOrdinaryBlockCarryingRoster()
    {
        var game = CreateDefense(5, lavaPit: false);
        Assert.True(game.StartLevel(), game.Message);

        game.Update(.05f);

        Assert.Equal(5, game.Waves[0].Spawned);
        Assert.Equal(5, game.Zombies.Count);
        Assert.All(game.Zombies, zombie =>
        {
            Assert.False(zombie.CarriesBucket);
            Assert.Equal(1, zombie.CarriedBlocks);
        });
    }

    private static GameSession CreateDefense(int zombieCount, bool lavaPit)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Name = "Liquid-disconnected approach",
            Waves = [new() { SpawnInterval = .01f, Groups = [new() { Kind = ZombieKind.Basic, Count = zombieCount }] }]
        });
        var game = new GameSession(12345, levels);
        if (!lavaPit) return game;
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        // Concentric one-block stairs leave the center objective suspended.
        // A thin film across the top step blocks prospective bridge floors,
        // although every spawn still has real ground on which to approach.
        for (int inset = 0; inset < 4; inset++)
            for (int x = 12 + inset; x <= 19 - inset; x++)
                for (int z = 12 + inset; z <= 19 - inset; z++)
                {
                    var cell = new Cell(x, 15 - inset, z);
                    if (WorldGrid.IsCore(cell)) continue;
                    Assert.True(dig(cell));
                    Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Lava, inset == 0 ? 1 : LiquidSystem.UnitsPerBlock));
                }
        return game;
    }

    private static Material[] SnapshotTerrain(WorldGrid world)
    {
        var terrain = new Material[WorldGrid.Size * WorldGrid.Size * WorldGrid.Size];
        int index = 0;
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 0; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                    terrain[index++] = world.GetNaturalMaterial(new Cell(x, y, z));
        return terrain;
    }
}
