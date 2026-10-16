using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class BucketRouteWorkspaceTests
{
    private static readonly Cell[] Horizontal = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)];

    [Theory]
    [InlineData(ZombieSize.Standard, false)]
    [InlineData(ZombieSize.Standard, true)]
    [InlineData(ZombieSize.Milli, false)]
    [InlineData(ZombieSize.Milli, true)]
    [InlineData(ZombieSize.Kilo, false)]
    [InlineData(ZombieSize.Kilo, true)]
    [InlineData(ZombieSize.Mega, false)]
    [InlineData(ZombieSize.Mega, true)]
    public void RetainedRoutesMatchTheFreshBuilderAfterLiquidAndWorldChanges(ZombieSize size, bool attackLava)
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(new Cell(12, 16, 12), LiquidKind.Water, 10));
        Assert.True(game.Liquids.TryAdd(new Cell(13, 16, 12), LiquidKind.Water, 10));
        Assert.True(game.Liquids.TryAdd(new Cell(18, 16, 18), LiquidKind.Lava, 10));
        var refresh = Bind<Action>(game, "RefreshBucketRoutes");
        var build = Bind<BuildRoutes>(game, "BuildSizedBucketRoutes");
        var resolve = Bind<ResolveRoute>(game, "TryGetBucketWorkRoute");
        Dictionary<Cell, Cell>? previous = null;
        for (int stage = 0; stage < 4; stage++)
        {
            refresh();
            var expected = FreshRoutes(game, size, attackLava).ToArray();
            Dictionary<Cell, Cell> actual;
            if (size == ZombieSize.Standard && !attackLava)
                actual = build(size, attackLava);
            else
            {
                // Exercise production revision invalidation, rather than asking
                // the builder directly to refresh an already-published field.
                var shape = new Zombie { Size = size };
                resolve(shape, new Cell(10, 16, 12), out _, attackLava);
                var cache = Field<Dictionary<ZombieSize, Dictionary<Cell, Cell>>>(game,
                    attackLava ? "_waterAttackRoutes" : "_sizedBucketRoutes");
                actual = cache[size];
            }
            if (previous != null) Assert.Same(previous, actual);
            Assert.Equal(expected, actual.ToArray());
            previous = actual;

            switch (stage)
            {
                case 0:
                    Assert.Equal(5, game.Liquids.TakeSurfaceLiquid(new Cell(12, 16, 12), 5, out _));
                    Assert.True(game.Liquids.TryAdd(new Cell(12, 16, 14), LiquidKind.Water, 10));
                    break;
                case 1:
                    Assert.True(game.Inventory.Add(Material.Dirt));
                    Assert.True(game.BuildBarrier(new Cell(12, 16, 13)));
                    break;
                case 2:
                    Assert.True(game.Remove(new Cell(12, 16, 13)));
                    Assert.Equal(10, game.Liquids.TakeSurfaceLiquid(new Cell(18, 16, 18), 10, out _));
                    Assert.True(game.Liquids.TryAdd(new Cell(18, 16, 17), LiquidKind.Lava, 10));
                    break;
            }
        }
    }

    private static Dictionary<Cell, Cell> FreshRoutes(GameSession game, ZombieSize size, bool attackLava)
    {
        // Freeze the original fresh-allocation builder, sharing only the live
        // physical feasibility rules. Sequence comparison also checks source,
        // neighbor, breadth-first discovery, and jump tie order after rebuilding.
        var isStand = Bind<Func<Cell, Zombie, bool>>(game, "IsSizedBucketStand");
        var throwDirection = Bind<ThrowDirection>(game, "TryGetWaterThrowDirection");
        var reachLiquid = Bind<ReachLiquid>(game, "TryReachZombieBucketLiquid");
        var canStep = Bind<CanStep>(game, "CanYieldStep");
        var canJump = Bind<CanJump>(game, "CanPlanJump");
        var sources = Field<HashSet<Cell>>(game, "_bucketWorkCells");
        var shape = new Zombie { Size = size };
        var routes = new Dictionary<Cell, Cell>();
        var frontier = new Queue<Cell>();
        var valid = new Dictionary<Cell, bool>();
        bool IsStand(Cell cell)
        {
            if (!valid.TryGetValue(cell, out bool result)) valid[cell] = result = isStand(cell, shape);
            return result;
        }
        foreach (var source in sources)
        {
            if (!game.Liquids.TryGetSurfaceLiquid(source, out var liquid, out _) || attackLava && liquid != LiquidKind.Lava) continue;
            for (int x = attackLava ? -5 : -3; x <= (attackLava ? 4 : 2); x++)
                for (int y = -2; y <= 2; y++)
                    for (int z = attackLava ? -5 : -3; z <= (attackLava ? 4 : 2); z++)
                    {
                        var stand = source + new Cell(x, y, z);
                        if (routes.ContainsKey(stand) || !IsStand(stand)) continue;
                        shape.Position = shape.StandAt(stand);
                        if (!(attackLava ? throwDirection(shape, out _, source) : reachLiquid(shape, out _))) continue;
                        routes[stand] = stand;
                        frontier.Enqueue(stand);
                    }
        }
        while (frontier.TryDequeue(out var destination))
            foreach (var horizontal in Horizontal)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    var previous = destination + horizontal + new Cell(0, dy, 0);
                    if (routes.ContainsKey(previous) || !IsStand(previous) || !canStep(previous, destination, shape)) continue;
                    routes[previous] = destination;
                    frontier.Enqueue(previous);
                }
                var launch = destination + horizontal + horizontal;
                if (!routes.ContainsKey(launch) && IsStand(launch) && canJump(launch, destination, out _, shape))
                {
                    routes[launch] = destination;
                    frontier.Enqueue(launch);
                }
            }
        return routes;
    }

    private delegate Dictionary<Cell, Cell> BuildRoutes(ZombieSize size, bool attackLava);
    private delegate bool ResolveRoute(Zombie zombie, Cell current, out Cell next, bool attackLava);
    private delegate bool ThrowDirection(Zombie zombie, out Vector3 direction, Cell? targetSource);
    private delegate bool ReachLiquid(Zombie zombie, out Cell source);
    private delegate bool CanStep(Cell from, Cell to, Zombie? zombie);
    private delegate bool CanJump(Cell from, Cell to, out float height, Zombie? zombie);
    private static T Bind<T>(GameSession game, string method) where T : Delegate => typeof(GameSession)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<T>(game);
    private static T Field<T>(GameSession game, string field) => (T)typeof(GameSession)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
}
