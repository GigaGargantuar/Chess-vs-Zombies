using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class SizeRouteTests
{
    private delegate bool Route(Zombie zombie, Cell current, out Cell next, bool includeFallback);
    private delegate bool BridgeStep(Zombie zombie, Cell from, Cell to);

    [Theory]
    [InlineData(ZombieSize.Milli)]
    [InlineData(ZombieSize.Kilo)]
    public void SizedZombiesActuallyWalkToTheCoreAtTheirOwnStandingHeight(ZombieSize size)
    {
        var (game, zombie, buddy) = RuntimeBattle(size, new Cell(11, 16, 15));
        // Exercise the walking field independently of the optional real-block
        // pickup now available even when other carriers are nearby.
        if (size == ZombieSize.Kilo)
        {
            var cooldowns = (Dictionary<int, float>)typeof(GameSession).GetField("_kiloActionCooldowns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
            cooldowns[zombie.Id] = cooldowns[buddy!.Id] = 100;
        }
        var origin = zombie.Position;
        for (int step = 0; step < 500 && game.CoreHp == Balance.CoreHp; step++)
        {
            KeepBuddyNearby(zombie, buddy);
            game.Update(.02f);
            Assert.Equal(16 + zombie.StandingHeight, zombie.Position.Y, precision: 4);
            AssertRuntimeBodyClear(game, zombie);
            Assert.Equal(zombie.MaxHp, zombie.Hp);
        }
        Assert.True(zombie.Position.X > origin.X + (size == ZombieSize.Kilo ? 1.99f : 2.49f), $"Size {size} stalled at {zombie.Position}.");
        AssertAtCore(zombie, zombie.Cell);
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Empty(game.KiloCovers);
    }

    [Fact]
    public void AKiloActuallyBreaksHeadHeightCoverAndContinuesWhenThereIsNoOpening()
    {
        var (game, zombie, buddy) = RuntimeBattle(ZombieSize.Kilo, new Cell(11, 16, 15));
        // Isolate navigation's head-height breach from the separate ability to
        // recover adjacent blocks (which now legitimately includes this wall).
        var cooldowns = (Dictionary<int, float>)typeof(GameSession).GetField("_kiloActionCooldowns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        cooldowns[zombie.Id] = cooldowns[buddy!.Id] = 100;
        var add = typeof(WorldGrid).GetMethod("AddBarrier", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, Material, float?, bool>>(game.World);
        // The whole row blocks a Kilo's head while its intended foot cells are
        // empty. One remaining HP makes the actual breach observable promptly.
        for (int z = 0; z < WorldGrid.Size; z++) Assert.True(add(new Cell(13, 17, z), Material.Dirt, 1));
        // Ground both ends so breaching the middle does not drop either half
        // of the head-height cover into the walking path.
        Assert.True(add(new Cell(13, 16, 0), Material.Dirt, 1));
        Assert.True(add(new Cell(13, 16, WorldGrid.Size - 1), Material.Dirt, 1));
        var headCover = new Cell(13, 17, 15);
        Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(13, 16, 15)));
        var origin = zombie.Position;

        KeepBuddyNearby(zombie, buddy);
        game.Update(.02f);

        Assert.Equal(Material.Air, game.World.GetMaterial(headCover));
        Assert.Equal(origin, zombie.Position); // The attack spends this slice before walking starts.
        for (int step = 0; step < 500 && game.CoreHp == Balance.CoreHp; step++)
        {
            KeepBuddyNearby(zombie, buddy);
            game.Update(.02f);
            Assert.Equal(17, zombie.Position.Y, precision: 4);
            AssertRuntimeBodyClear(game, zombie);
        }
        Assert.True(zombie.Position.X >= 13.999f, $"Kilo stopped after the head breach at {zombie.Position}.");
        Assert.True(game.CoreHp < Balance.CoreHp);
        // The nearby Kilo can legitimately breach its own column at z=16
        // while following. All cover outside those two approaches stays intact.
        Assert.InRange(game.World.Barriers.Count(), 31, 32);
        for (int z = 0; z < WorldGrid.Size; z++)
            if (z is not (15 or 16 or 17))
                Assert.Equal(1, Assert.IsType<ChessVsZombies.Core.Barrier>(game.World.GetBarrier(new Cell(13, 17, z))).Hp);
        Assert.Empty(game.KiloCovers);
    }

    [Theory]
    [InlineData(ZombieSize.Milli)]
    [InlineData(ZombieSize.Kilo)]
    public void ASizeSpecificFieldReachesTheCoreWithoutPuttingTheBodyInsideIt(ZombieSize size)
    {
        var game = new GameSession();
        var zombie = new Zombie { Size = size };
        var current = new Cell(1, 16, 15);
        var route = Routes(game);
        var clear = typeof(GameSession).GetMethod("IsBodyClear", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Vector3, Zombie?, bool>>(game);
        int moves = 0;
        while (true)
        {
            Assert.True(route(zombie, current, out var next, false));
            Assert.True(clear(zombie.StandAt(next), zombie), $"Size {size} was routed into a solid at {next}.");
            if (next == current) break;
            Assert.True(++moves < 64, "Shared size field must converge instead of cycling.");
            current = next;
        }
        AssertAtCore(zombie, current);
        Assert.True(moves > 0);
    }

    [Fact]
    public void AKiloCannotBeRoutedOrBridgedIntoTheUndersideOfTheFloatingCore()
    {
        var game = new GameSession();
        var step = typeof(GameSession).GetMethod("CanUseSizedBridgeStep", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<BridgeStep>(game);
        var from = new Cell(14, 14, 15);
        var belowCore = new Cell(15, 14, 15);
        Assert.False(step(new Zombie { Size = ZombieSize.Kilo }, from, belowCore));
        Assert.True(step(new Zombie { Size = ZombieSize.Milli }, from, belowCore));
        Assert.False(Routes(game)(new Zombie { Size = ZombieSize.Kilo }, belowCore, out _, false));
    }

    [Fact]
    public void AKiloChoosesAnOpeningInAHeadHeightWallInsteadOfFollowingAStandardRouteThroughIt()
    {
        var game = new GameSession();
        var add = typeof(WorldGrid).GetMethod("AddBarrier", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, Material, float?, bool>>(game.World);
        for (int z = 0; z < WorldGrid.Size; z++)
            if (z is not (15 or 16)) Assert.True(add(new Cell(10, 17, z), Material.Dirt, null));
        var zombie = new Zombie { Size = ZombieSize.Kilo };
        var current = new Cell(8, 16, 10);
        var route = Routes(game);
        bool crossed = false;
        for (int moves = 0; moves < 64; moves++)
        {
            Assert.True(route(zombie, current, out var next, false));
            if (next.X == 9 && current.X == 8)
            {
                Assert.Equal(15, next.Z);
                crossed = true;
            }
            if (next == current) break;
            current = next;
        }
        Assert.True(crossed);
        AssertAtCore(zombie, current);
    }

    [Fact]
    public void NonstandardUnitsShareTheirSizeFieldAndRebuildItOnlyAfterGeometryChanges()
    {
        var game = new GameSession();
        var route = Routes(game);
        var first = new Zombie { Id = 1, Size = ZombieSize.Kilo };
        var second = new Zombie { Id = 2, Size = ZombieSize.Kilo };
        Assert.True(route(first, new Cell(2, 16, 15), out _, true));
        var field = Fields(game)[ZombieSize.Kilo];
        Assert.True(route(second, new Cell(3, 16, 15), out _, true));
        Assert.Same(field, Fields(game)[ZombieSize.Kilo]);
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        Assert.True(dig(new Cell(5, 15, 5)));
        Assert.True(route(second, new Cell(3, 16, 15), out _, true));
        Assert.NotSame(field, Fields(game)[ZombieSize.Kilo]);
    }

    [Fact]
    public void AnInvalidKiloCellReturnsAnExplicitWaitInsteadOfFallingThroughToAStandardGoal()
    {
        var game = new GameSession();
        var cell = new Cell(15, 14, 15);
        Assert.True(Routes(game)(new Zombie { Size = ZombieSize.Kilo }, cell, out var next, true));
        Assert.Equal(cell, next);
    }

    private static Route Routes(GameSession game) => typeof(GameSession)
        .GetMethod("TryGetSizedRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Route>(game);

    private static IDictionary Fields(GameSession game) => (IDictionary)typeof(GameSession)
        .GetField("_sizeRoutes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;

    private static (GameSession Game, Zombie Zombie, Zombie? Buddy) RuntimeBattle(ZombieSize size, Cell cell)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Size = size }] }] });
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        // Place this authored roster entry at a reproducible approach before
        // the first simulation slice. Navigation and movement run normally.
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        game.Waves[0].BudgetSpent = LevelCatalog.ZombieMaxHp(ZombieKind.Basic, GearTier.Wood, size);
        var zombie = new Zombie { Id = 100, WaveIndex = 0, Size = size, Attack = 1 };
        zombie.Position = zombie.StandAt(cell);
        game.Zombies.Add(zombie);
        Zombie? buddy = null;
        if (size == ZombieSize.Kilo)
        {
            // Another Kilo prevents the separate lone-carrier cover action;
            // Kilos cannot hurl each other, so this isolates walking/breaching.
            buddy = new Zombie { Id = 101, WaveIndex = -1, Size = ZombieSize.Kilo, Attack = 1 };
            KeepBuddyNearby(zombie, buddy);
            game.Zombies.Add(buddy);
        }
        return (game, zombie, buddy);
    }

    private static void KeepBuddyNearby(Zombie zombie, Zombie? buddy)
    {
        if (buddy == null) return;
        buddy.Position = zombie.Position + new Vector3(-1, 0, 1);
        buddy.NextCell = null;
        buddy.MoveProgress = 0;
    }

    private static void AssertRuntimeBodyClear(GameSession game, Zombie zombie)
    {
        var min = zombie.Position - zombie.BodyHalfExtents;
        var max = zombie.Position + zombie.BodyHalfExtents;
        for (int x = (int)MathF.Floor(min.X); x < (int)MathF.Ceiling(max.X); x++)
            for (int y = (int)MathF.Floor(min.Y); y < (int)MathF.Ceiling(max.Y); y++)
                for (int z = (int)MathF.Floor(min.Z); z < (int)MathF.Ceiling(max.Z); z++)
                    Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(x, y, z)));
    }

    private static void AssertAtCore(Zombie zombie, Cell cell)
    {
        if (zombie.Size != ZombieSize.Kilo) { Assert.Contains(Neighbors(cell), WorldGrid.IsCore); return; }
        var min = zombie.StandAt(cell) - Vector3.One;
        var max = min + new Vector3(2);
        Assert.True(max.X == 15 || min.X == 17 || max.Z == 15 || min.Z == 17 || max.Y == 15 || min.Y == 17);
    }

    private static IEnumerable<Cell> Neighbors(Cell cell)
    {
        yield return cell + new Cell(1, 0, 0);
        yield return cell + new Cell(-1, 0, 0);
        yield return cell + new Cell(0, 1, 0);
        yield return cell + new Cell(0, -1, 0);
        yield return cell + new Cell(0, 0, 1);
        yield return cell + new Cell(0, 0, -1);
    }
}
