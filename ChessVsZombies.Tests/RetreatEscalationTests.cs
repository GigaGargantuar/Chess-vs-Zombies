using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class RetreatEscalationTests
{
    [Theory]
    [InlineData(ZombieSize.Milli)]
    [InlineData(ZombieSize.Standard)]
    [InlineData(ZombieSize.Kilo)]
    public void ThreePhysicalReturnsImproveArmorWithoutRechargingSpawnBudget(ZombieSize size)
    {
        var game = CreateGame(GearTier.Osmium);
        var zombie = Add(game, -1, size, new Cell(0, 16, 8));
        game.DamageZombie(zombie, zombie.MaxBodyHp * .2f, DamageKind.Chemical);
        float bodyHp = zombie.BodyHp;
        var wave = game.Waves[0];
        wave.BudgetSpent = 10;
        int spawned = wave.Spawned, count = wave.TotalCount;
        float priorHp = wave.TotalHp;
        GearTier[] expected = [GearTier.Wood, GearTier.Stone, GearTier.Osmium];

        for (int index = 0; index < 3; index++)
        {
            MarkRetreating(game, zombie);
            Assert.True((bool)Invoke(game, "TryCompleteRetreat", zombie, 1.1f)!);
            Assert.False(game.IsZombieRetreating(zombie));
            Assert.Equal(index + 1, zombie.RetreatReturns);
            Assert.Equal(expected[index], zombie.Tier);
            Assert.Equal(4, zombie.ArmorPieces.Count);
            Assert.All(zombie.ArmorPieces, piece => Assert.Equal(piece.MaxHp, piece.Hp));
            Assert.Equal(bodyHp, zombie.BodyHp);
            Assert.Equal(10, wave.BudgetSpent);
            Assert.Equal(spawned, wave.Spawned);
            Assert.Equal(count, wave.TotalCount);
            Assert.True(wave.TotalHp > priorHp);
            priorHp = wave.TotalHp;
        }
    }

    [Fact]
    public void ExhaustedZombieMustReachThePhysicalEdgeBeforeItCanLeave()
    {
        var game = CreateGame(GearTier.Iron);
        var zombie = Add(game, -1, ZombieSize.Standard, new Cell(8, 16, 8));
        typeof(Zombie).GetProperty(nameof(Zombie.RetreatReturns))!.SetValue(zombie, 3);
        MarkRetreating(game, zombie);

        Assert.False((bool)Invoke(game, "TryCompleteRetreat", zombie, 2f)!);
        Assert.Contains(zombie, game.Zombies);
        Assert.True(game.IsZombieRetreating(zombie));

        zombie.Position = zombie.StandAt(new Cell(0, 16, 8));
        Assert.True((bool)Invoke(game, "TryCompleteRetreat", zombie, .05f)!);
        game.Update(.05f); // Departures are removed after the enemy update enumeration.
        Assert.DoesNotContain(zombie, game.Zombies);
        Assert.True(zombie.Hp > 0);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void GuardingKiloRevisitingTwoCellsRetreatsEvenWhileAnotherZombieMoves()
    {
        var game = CreateGame(GearTier.Wood);
        var kilo = Add(game, -1, ZombieSize.Kilo, new Cell(8, 16, 8));
        var moving = Add(game, -2, ZombieSize.Standard, new Cell(0, 16, 10));

        for (int step = 0; step < 60 && !game.IsZombieRetreating(kilo); step++)
        {
            kilo.Position = kilo.StandAt(new Cell(8 + step % 2, 16, 8));
            Invoke(game, "FaceKiloTowardShot", kilo, step % 2 == 0 ? Vector3.UnitX : Vector3.UnitZ);
            moving.Position = moving.StandAt(new Cell(Math.Min(24, step / 2), 16, 10));
            Invoke(game, "UpdateRetreatBehavior", .25f);
        }

        Assert.True(game.IsZombieRetreating(kilo));
        Assert.False(game.IsZombieRetreating(moving));
    }

    [Fact]
    public void RealExcavationContinuesLongerThanTheStagnationWindow()
    {
        var game = CreateGame(GearTier.Wood);
        var zombie = Add(game, -1, ZombieSize.Kilo, new Cell(8, 16, 8));
        var obstacle = new Cell(10, 16, 8);
        Assert.True((bool)typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game.World, [obstacle, Material.Osmium])!);

        for (int step = 0; step < 30; step++)
        {
            zombie.Cooldown = 0;
            Assert.True((bool)Invoke(game, "AttackObstacle", zombie, obstacle, null)!);
            Invoke(game, "UpdateRetreatBehavior", 1f);
            Assert.False(game.IsZombieRetreating(zombie));
        }

        Assert.Equal(Balance.BlockHp(Material.Osmium) - 30, game.World.GetTerrainHp(obstacle));
    }

    private static GameSession CreateGame(GearTier topTier)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition
        {
            Name = "Retreat escalation",
            Waves = [new() { Groups = [new() { Kind = ZombieKind.Armored, Tier = topTier, Count = 1 }] }]
        });
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        return game;
    }

    private static Zombie Add(GameSession game, int id, ZombieSize size, Cell cell)
    {
        var zombie = new Zombie { Id = id, WaveIndex = 0, Size = size, Kind = ZombieKind.Basic, Attack = 1 };
        zombie.Position = zombie.StandAt(cell);
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static void MarkRetreating(GameSession game, Zombie zombie)
    {
        var states = (System.Collections.IDictionary)typeof(GameSession)
            .GetField("_retreating", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var stateType = typeof(GameSession).GetNestedType("RetreatState", BindingFlags.NonPublic)!;
        states[zombie.Id] = Activator.CreateInstance(stateType, nonPublic: true);
    }

    private static object? Invoke(GameSession game, string name, params object?[] arguments) => typeof(GameSession)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, arguments);
}
