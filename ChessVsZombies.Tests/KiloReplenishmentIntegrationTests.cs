using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloReplenishmentIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AUsedOrDestroyedHandRecoversARealGroundBlockEvenWithANearbyAlly(bool usedForConstruction)
    {
        var game = new GameSession();
        var kilo = new Zombie { Id = 100, WaveIndex = -1, Size = ZombieSize.Kilo };
        kilo.Position = kilo.StandAt(new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        game.Zombies.Add(kilo);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.Equal(2, game.KiloCovers.Count);
        Invoke(game, "DamageKiloCover", kilo.Id, 1, 7.5f);
        if (usedForConstruction) Invoke(game, "ConsumeKiloConstructionBlock", kilo);
        else Invoke(game, "DamageKiloCover", kilo.Id, 0, 1000f);
        var surviving = Assert.Single(game.KiloCovers);
        Set(kilo.BucketLoads[0], nameof(ZombieBucketLoad.Units), 4);
        Set(kilo.BucketLoads[0], nameof(ZombieBucketLoad.Kind), LiquidKind.Water);
        Set(kilo.BucketLoads[1], nameof(ZombieBucketLoad.Units), 8);
        Set(kilo.BucketLoads[1], nameof(ZombieBucketLoad.Kind), LiquidKind.Mercury);
        kilo.FaceToward(-Vector3.UnitZ);
        var source = new Cell(6, 15, 4);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(source));
        Assert.False((bool)Invoke(game.World, "DamageTerrain", source, 13f)!);
        var ally = new Zombie { Id = 101, WaveIndex = -1, Size = ZombieSize.Kilo };
        ally.Position = ally.StandAt(new Cell(5, 16, 8));
        game.Zombies.Add(ally);
        var cooldowns = (IDictionary<int, float>)typeof(GameSession)
            .GetField("_kiloActionCooldowns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        cooldowns[ally.Id] = 100;

        Invoke(game, "UpdateKilos", 1.1f);

        var replenished = game.KiloCovers.Where(cover => cover.CarrierId == kilo.Id).ToArray();
        Assert.Equal(2, replenished.Length);
        Assert.Equal(surviving.Hp, Assert.Single(replenished, cover => cover.BlockIndex == surviving.BlockIndex).Hp);
        Assert.Equal(surviving.Material, Assert.Single(replenished, cover => cover.BlockIndex == surviving.BlockIndex).Material);
        var replacement = Assert.Single(replenished, cover => cover.BlockIndex != surviving.BlockIndex);
        Assert.Equal(Material.Dirt, replacement.Material);
        Assert.Equal(87, replacement.Hp);
        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Equal(4, kilo.BucketLoads[0].Units);
        Assert.Equal(LiquidKind.Water, kilo.BucketLoads[0].Kind);
        Assert.Equal(8, kilo.BucketLoads[1].Units);
        Assert.Equal(LiquidKind.Mercury, kilo.BucketLoads[1].Kind);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.GetCount(Material.Dirt));
    }

    private static object? Invoke(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Set(object target, string name, object? value) => target.GetType().GetProperty(name)!.SetValue(target, value);
}
