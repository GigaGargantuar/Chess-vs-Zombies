using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloShorelineStallTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void APartialGridKiloAbandonsABlockedCachedCrossingAndActuallyMovesAway()
    {
        var (game, kilo) = Scenario();
        var origin = kilo.Position;
        var blockedCorner = new Cell(26, 16, 14);
        Field<Dictionary<int, Vector3>>(game, "_liquidOrigins")[kilo.Id] = origin;
        Field<Dictionary<int, Vector3>>(game, "_liquidDestinations")[kilo.Id] = new Vector3(28, 17, 15);

        Invoke(game, "UpdateZombie", kilo, .05f);

        Assert.False(Field<Dictionary<int, Vector3>>(game, "_liquidDestinations").ContainsKey(kilo.Id));
        Assert.Equal(100, game.World.GetTerrainHp(blockedCorner));
        Assert.Equal(new Vector3(28, 17, 16), kilo.Position);
        for (int step = 0; step < 100; step++) Invoke(game, "UpdateZombie", kilo, .05f);
        Assert.True(Vector3.DistanceSquared(origin, kilo.Position) > 1,
            $"The Kilo remained at its interrupted shoreline crossing: {kilo.Position}.");
        Assert.Equal(kilo.MaxHp, kilo.Hp);
    }

    [Fact]
    public void APartialGridBuoyantRouteSweepsTheWholeBodyBetweenItsClearEndpoints()
    {
        var (game, kilo) = Scenario();

        var next = (Vector3?)Invoke(game, "FindBuoyantRouteStep", kilo);

        Assert.True(next.HasValue);
        // Both ends of the old diagonal route were clear while its rear corner
        // clipped the block at (26,16,14) immediately after departure.
        Assert.False((bool)Invoke(game, "TryFindBodyObstacle",
            [kilo.Position, next!.Value, default(Cell), null, kilo])!);
        Assert.NotEqual(new Vector3(28, 17, 15), next.Value);
    }

    [Theory]
    [InlineData(ZombieSize.Kilo)]
    [InlineData(ZombieSize.Standard)]
    [InlineData(ZombieSize.Milli)]
    public void AShallowPuddleDoesNotMakeAZombieDigItsOwnFloorAndItActuallyLeaves(ZombieSize size)
    {
        var game = new GameSession();
        var kilo = new Zombie { Id = 100, Size = size, Attack = 1 };
        var supported = kilo.StandAt(new Cell(5, 16, 5));
        kilo.Position = supported + Vector3.UnitY * .035276f;
        game.Zombies.Add(kilo);
        var liquid = new Cell((int)supported.X, 16, (int)supported.Z);
        Assert.True(game.Liquids.TryAdd(liquid, LiquidKind.Mercury, 1));
        var origin = kilo.Position;
        var upperY = Math.Max(origin.Y, 16.1f + .4f * kilo.BodyScale);

        for (int step = 0; step < 60; step++)
        {
            Invoke(game, "UpdateBuoyancy", kilo, .05f);
            Assert.InRange(kilo.Position.Y, supported.Y, upperY + .00001f);
            Assert.Equal(100, game.World.GetTerrainHp(new Cell(5, 15, 5)));
            Assert.Equal(100, game.World.GetTerrainHp(new Cell(6, 15, 6)));
        }

        Assert.True(Vector2.Distance(new Vector2(origin.X, origin.Z), new Vector2(kilo.Position.X, kilo.Position.Z)) > 1,
            $"The {size} remained in its shallow puddle: {kilo.Position}.");
        Assert.Equal(kilo.MaxHp, kilo.Hp);
    }

    [Fact]
    public void ATrulyFloatingKiloWithNoClearRouteBreachesItsActualUpperBlockerAtNormalCooldown()
    {
        var game = new GameSession();
        var kilo = new Zombie { Id = 100, Size = ZombieSize.Kilo, Attack = 1, Position = new Vector3(6, 17, 6) };
        game.Zombies.Add(kilo);
        Assert.True(game.Liquids.TryAdd(new Cell(6, 16, 6), LiquidKind.Mercury, 2));
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", Hidden)!
            .CreateDelegate<Func<Cell, Material, bool>>(game.World);
        // A connected overhang surrounds only the body's upper half; the
        // foot-cell neighbors are all empty and cannot select these blockers.
        for (int x = 4; x <= 7; x++)
            for (int z = 4; z <= 7; z++)
                if (x is 4 or 7 || z is 4 or 7) Assert.True(place(new Cell(x, 17, z), Material.Dirt));
        var upperBlocker = new Cell(7, 17, 5);
        Field<Dictionary<int, Vector3>>(game, "_liquidOrigins")[kilo.Id] = kilo.Position;
        Field<Dictionary<int, Vector3>>(game, "_liquidDestinations")[kilo.Id] = kilo.Position + Vector3.UnitX;

        Invoke(game, "UpdateBuoyancy", kilo, .05f);
        Assert.False(Field<Dictionary<int, Vector3>>(game, "_liquidDestinations").ContainsKey(kilo.Id));
        Assert.Equal(100, game.World.GetTerrainHp(upperBlocker));
        Invoke(game, "UpdateBuoyancy", kilo, .05f);
        Assert.Equal(99, game.World.GetTerrainHp(upperBlocker));
        for (int step = 0; step < 19; step++) Invoke(game, "UpdateZombie", kilo, .05f);
        Assert.Equal(99, game.World.GetTerrainHp(upperBlocker));
        for (int step = 0; step < 2; step++) Invoke(game, "UpdateZombie", kilo, .05f);

        Assert.Equal(98, game.World.GetTerrainHp(upperBlocker));
        Assert.Equal(1, game.BuoyantRouteSearchCount);
        Assert.Equal(100, game.World.GetTerrainHp(new Cell(5, 15, 5)));
        Assert.Equal(new Vector3(6, 17, 6), kilo.Position);
    }

    [Fact]
    public void ABuoyantKiloAlreadyAtABodyValidCoreApproachDoesNotSearchForAnotherApproach()
    {
        var game = new GameSession();
        var kilo = new Zombie { Id = 100, Size = ZombieSize.Kilo };
        kilo.Position = kilo.StandAt(new Cell(13, 16, 14));
        game.Zombies.Add(kilo);
        Assert.True(game.Liquids.TryAdd(new Cell(14, 16, 15), LiquidKind.Mercury, 2));
        Assert.True((bool)typeof(GameSession).GetMethod("IsCoreApproach", Hidden | BindingFlags.Static)!
            .Invoke(null, [kilo.Cell, kilo])!);

        Assert.Null((Vector3?)Invoke(game, "FindBuoyantRouteStep", kilo));

        Assert.Equal(1, game.BuoyantRouteSearchCount);
    }

    private static (GameSession Game, Zombie Kilo) Scenario()
    {
        var game = new GameSession();
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", Hidden)!
            .CreateDelegate<Func<Cell, Material, bool>>(game.World);
        Assert.True(place(new Cell(26, 16, 14), Material.Dirt));
        Assert.True(place(new Cell(26, 17, 14), Material.Dirt));
        Assert.True(place(new Cell(29, 16, 16), Material.Dirt));
        var kilo = new Zombie
        {
            Id = 100, Size = ZombieSize.Kilo, Attack = 1,
            Position = new Vector3(27.952187f, 17, 16)
        };
        game.Zombies.Add(kilo);
        Field<Dictionary<int, float>>(game, "_kiloActionCooldowns")[kilo.Id] = 100;
        return (game, kilo);
    }

    private static T Field<T>(GameSession game, string name) =>
        (T)typeof(GameSession).GetField(name, Hidden)!.GetValue(game)!;

    private static object? Invoke(GameSession game, string name, params object?[] args) =>
        typeof(GameSession).GetMethod(name, Hidden)!.Invoke(game, args);
}
