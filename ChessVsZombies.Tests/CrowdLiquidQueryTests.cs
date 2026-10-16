using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class CrowdLiquidQueryTests
{
    private delegate bool SurfaceQuery(GameSession game, Zombie zombie, Vector3 position, out float surface);
    private static readonly SurfaceQuery Surface = Method("TryBuoyantSurface").CreateDelegate<SurfaceQuery>();
    private static readonly Func<GameSession, Zombie, float, bool> Buoyancy =
        Method("UpdateBuoyancy").CreateDelegate<Func<GameSession, Zombie, float, bool>>();
    private static readonly Action<GameSession, Zombie, float> Exposure =
        Method("UpdateLiquidExposure").CreateDelegate<Action<GameSession, Zombie, float>>();

    [Fact]
    public void DryCrowdLiquidQueriesDoNotAllocatePerZombie()
    {
        var game = new GameSession();
        var zombie = new Zombie { Position = new Cell(8, 16, 8).Center };
        for (int i = 0; i < 100; i++) Query();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) Query();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 1024);
        Assert.Equal(new Cell(8, 16, 8).Center, zombie.Position);

        void Query()
        {
            game.Liquids.GetLiquidAt(zombie.Position);
            Surface(game, zombie, zombie.Position, out _);
            Buoyancy(game, zombie, .01f);
        }
    }

    [Fact]
    public void DryColumnsStayAllocationFreeWhenAnotherPartOfTheMapIsWet()
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(new Cell(20, 17, 20), LiquidKind.Mercury, 10));
        var zombie = new Zombie { Position = new Cell(8, 16, 8).Center };
        for (int i = 0; i < 100; i++) Buoyancy(game, zombie, .01f);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) Buoyancy(game, zombie, .01f);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 1024);
        Assert.False(Surface(game, zombie, zombie.Position, out _));
    }

    [Theory]
    [InlineData(ZombieSize.Standard, 16.5f, false)]
    [InlineData(ZombieSize.Standard, 17.5f, true)]
    [InlineData(ZombieSize.Standard, 18.5f, true)]
    [InlineData(ZombieSize.Milli, 18.5f, false)]
    [InlineData(ZombieSize.Kilo, 16.5f, true)]
    [InlineData(ZombieSize.Mega, 16.5f, true)]
    public void SurfaceQueriesRetainTheThreeCellColumnAndActualBodyContact(ZombieSize size, float y, bool expected)
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(new Cell(8, 17, 8), LiquidKind.Mercury, 10));
        var zombie = new Zombie { Size = size, Position = new Vector3(8.5f, y, 8.5f) };

        Assert.Equal(expected, Surface(game, zombie, zombie.Position, out float surface));
        if (expected) Assert.Equal(18, surface);
        else Assert.Equal(float.NegativeInfinity, surface);
    }

    [Fact]
    public void ArmorThatSinksInMercuryDoesNotBecomeBuoyant()
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(new Cell(8, 17, 8), LiquidKind.Mercury, 10));
        var zombie = new Zombie
        {
            Kind = ZombieKind.Armored, Tier = GearTier.Osmium, Position = new Cell(8, 17, 8).Center
        };

        Assert.False(Surface(game, zombie, zombie.Position, out _));
    }

    [Fact]
    public void ShorelineMovementFinishesWhenTheLastLiquidDisappears()
    {
        var game = new GameSession();
        var zombie = new Zombie { Id = 10, Position = new Cell(8, 16, 8).Center };
        var destination = new Cell(9, 16, 8).Center;
        Field<Dictionary<int, Vector3>>(game, "_liquidOrigins")[zombie.Id] = zombie.Position;
        Field<Dictionary<int, Vector3>>(game, "_liquidDestinations")[zombie.Id] = destination;

        Assert.True(Buoyancy(game, zombie, .05f));

        Assert.InRange(zombie.Position.X, 8.5001f, destination.X);
        Assert.Equal(16.5f, zombie.Position.Y);
    }

    [Fact]
    public void CrowdExposureReadsCloudsWithoutCopyingThemPerZombie()
    {
        var game = new GameSession();
        game.Liquids.RestoreState(new LiquidState
        {
            Hazards = [new(new Cell(20, 17, 20), 2, DamageKind.Chemical, 20, 2, 1)]
        });
        var zombie = new Zombie { Position = new Cell(8, 16, 8).Center };
        for (int i = 0; i < 100; i++) Exposure(game, zombie, .01f);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) Exposure(game, zombie, .01f);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 1024);
        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);
        Assert.Equal(Balance.AirSeconds, zombie.AirRemaining);
    }

    [Fact]
    public void PublicHazardEnumerationRetainsItsSnapshotAfterCloudsExpire()
    {
        var liquids = new LiquidSystem(new WorldGrid());
        liquids.RestoreState(new LiquidState
        {
            Hazards = [new(new Cell(8, 17, 8), 2, DamageKind.Chemical, .1f, 2, 1)]
        });
        var snapshot = liquids.Hazards;

        liquids.Update(.1f);

        Assert.Empty(liquids.Hazards);
        Assert.Equal(.1f, Assert.Single(snapshot).RemainingSeconds);
    }

    [Fact]
    public void TurretSelectionKeepsListOrderAndSeesDirectPublicStateChanges()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        var first = new Turret { Id = 1, Cell = cell };
        var second = new Turret { Id = 2, Cell = cell };
        game.Turrets.AddRange([first, second]);
        Assert.Same(first, game.TrySelectTurret(cell));

        first.Hp = 0;
        Assert.Same(second, game.TrySelectTurret(cell));
        second.Cell = new Cell(9, 16, 8);
        Assert.Null(game.TrySelectTurret(cell));
        Assert.Same(second, game.TrySelectTurret(second.Cell));
    }

    private static MethodInfo Method(string name) =>
        typeof(GameSession).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static T Field<T>(GameSession game, string name) =>
        (T)typeof(GameSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
}
