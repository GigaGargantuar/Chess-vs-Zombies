using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloCooperativeGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClumpedKiloUsesOneRealHandToCoverItsNeighborsExposedBack(bool reverseOrder)
    {
        var (game, protectedKilo, guard) = ClumpedKilos(reverseOrder);
        var before = game.KiloCovers.ToArray();
        var positions = game.Zombies.Select(zombie => zombie.Position).ToArray();
        var aim = protectedKilo.Position + new Vector3(.8f, .8f, 0);

        Fire(game, aim - Vector3.UnitZ * 4, aim);

        Assert.Empty(game.Projectiles);
        Assert.All(game.Zombies, zombie => Assert.Equal(zombie.MaxBodyHp, zombie.BodyHp));
        Assert.Equal(positions, game.Zombies.Select(zombie => zombie.Position));
        var damaged = Assert.Single(game.KiloCovers,
            cover => cover.Hp != before.Single(old => old.CarrierId == cover.CarrierId && old.BlockIndex == cover.BlockIndex).Hp);
        Assert.Equal(guard.Id, damaged.CarrierId);
        Assert.Equal(1, damaged.BlockIndex);
        Assert.Equal(before.Single(old => old.CarrierId == damaged.CarrierId && old.BlockIndex == damaged.BlockIndex).Hp - 3, damaged.Hp);
        Assert.Equal(4, game.KiloCovers.Count);
        AssertGeometry(game);
        Assert.Single(game.KiloCovers, cover => Vector3.Distance(cover.Min,
            before.Single(old => old.CarrierId == cover.CarrierId && old.BlockIndex == cover.BlockIndex).Min) > .00001f);
    }

    [Fact]
    public void AGuardCanCoverABusyBucketWorkerWithoutReactivatingTheWorkersHands()
    {
        var (game, worker, guard) = ClumpedKilos();
        Invoke(game, "BeginKiloBucketAction", worker);
        var before = game.KiloCovers.ToArray();
        var aim = worker.Position + new Vector3(.8f, .8f, 0);

        Fire(game, aim - Vector3.UnitZ * 4, aim);

        Assert.Equal(worker.MaxBodyHp, worker.BodyHp);
        Assert.True(game.IsKiloUsingBuckets(worker));
        Assert.All(game.KiloCovers.Where(cover => cover.CarrierId == worker.Id), cover =>
        {
            Assert.False(cover.IsActive);
            Assert.Equal(before.Single(old => old.CarrierId == worker.Id && old.BlockIndex == cover.BlockIndex).Hp, cover.Hp);
        });
        Assert.Equal(before.Where(cover => cover.CarrierId == guard.Id).Sum(cover => cover.Hp) - 3,
            game.KiloCovers.Where(cover => cover.CarrierId == guard.Id).Sum(cover => cover.Hp));
        AssertGeometry(game);
    }

    [Fact]
    public void BusyCarriersCannotLendTheirStowedCoverToAnotherZombie()
    {
        var (game, target, guard) = ClumpedKilos();
        Invoke(game, "DamageKiloCover", target.Id, 0, 10000f);
        Invoke(game, "DamageKiloCover", target.Id, 1, 10000f);
        Invoke(game, "BeginKiloBucketAction", guard);
        var before = game.KiloCovers.ToArray();
        var aim = target.Position + new Vector3(.8f, .8f, 0);

        Fire(game, aim - Vector3.UnitZ * 4, aim);

        Assert.Equal(target.MaxBodyHp - 3, target.BodyHp);
        Assert.Equal(before, game.KiloCovers);
        Assert.True(game.IsKiloUsingBuckets(guard));
    }

    [Fact]
    public void ACarrierCannotProjectItsCoverAcrossAGapOutsideItsBodyVolume()
    {
        var (game, target, guard) = ClumpedKilos();
        Invoke(game, "DamageKiloCover", target.Id, 0, 10000f);
        Invoke(game, "DamageKiloCover", target.Id, 1, 10000f);
        guard.Position = target.Position + new Vector3(2.1f, 0, 0);
        var before = game.KiloCovers.ToArray();
        var aim = target.Position + new Vector3(.8f, .8f, 0);

        Fire(game, aim - Vector3.UnitZ * 4, aim);

        Assert.Equal(target.MaxBodyHp - 3, target.BodyHp);
        Assert.Equal(before, game.KiloCovers);
        AssertGeometry(game);
    }

    [Fact]
    public void TerrainStillStopsAProjectileBeforeAnyGroupGuardCanReact()
    {
        var (game, target, _) = ClumpedKilos();
        var cell = new Cell(8, 17, 5);
        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", cell, Material.Stone)!);
        var before = game.KiloCovers.ToArray();
        var aim = target.Position + new Vector3(.8f, .8f, 0);

        Fire(game, aim - Vector3.UnitZ * 4, aim);

        Assert.Equal(before, game.KiloCovers);
        Assert.All(game.Zombies, zombie => Assert.Equal(zombie.MaxBodyHp, zombie.BodyHp));
    }

    private static (GameSession Game, Zombie Target, Zombie Guard) ClumpedKilos(bool reverseOrder = false)
    {
        var game = new GameSession();
        var target = Equip(game, 200, new Cell(5, 16, 5));
        var guard = Equip(game, 100, new Cell(10, 16, 5));
        target.Position = new Vector3(8, 17, 8);
        guard.Position = target.Position + new Vector3(.25f, 0, 0);
        if (reverseOrder) game.Zombies.Reverse();
        return (game, target, guard);
    }

    private static Zombie Equip(GameSession game, int id, Cell cell)
    {
        var kilo = new Zombie { Id = id, WaveIndex = -1, Size = ZombieSize.Kilo, Kind = ZombieKind.Basic };
        kilo.Position = kilo.StandAt(cell);
        game.Zombies.Add(kilo);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.Equal(2, game.KiloCovers.Count(cover => cover.CarrierId == kilo.Id));
        return kilo;
    }

    private static void Fire(GameSession game, Vector3 from, Vector3 to)
    {
        game.Projectiles.Add(new Projectile { Origin = from, Target = to, Damage = 3, Kind = ShotKind.Pellet, Duration = .1f });
        Invoke(game, "UpdateProjectiles", .1f);
    }

    private static void AssertGeometry(GameSession game)
    {
        foreach (var cover in game.KiloCovers)
        {
            var owner = game.Zombies.Single(zombie => zombie.Id == cover.CarrierId);
            var low = cover.Min - (owner.Position - Vector3.One);
            var high = owner.Position + Vector3.One - cover.Max;
            Assert.True(low.X >= -.00001f && low.Y >= -.00001f && low.Z >= -.00001f);
            Assert.True(high.X >= -.00001f && high.Y >= -.00001f && high.Z >= -.00001f);
            Assert.Equal(Vector3.One, cover.Max - cover.Min);
        }
    }

    private static object? Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(target, args);
}
