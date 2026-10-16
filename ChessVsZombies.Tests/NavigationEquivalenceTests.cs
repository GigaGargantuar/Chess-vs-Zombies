using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class NavigationEquivalenceTests
{
    private static readonly Cell[] Horizontal = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)];
    private static readonly Cell[] Adjacent = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
    private delegate bool JumpPlanner(Cell from, Cell to, out float height, Zombie? zombie);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DenseNavigationPreservesEveryRouteAndTieFromTheOriginalGraph(int scenario)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Waves = [new() { Groups = [new() { Kind = ZombieKind.Basic, Tier = GearTier.Wood, Count = 1 },
                new() { Kind = ZombieKind.Armed, Tier = GearTier.Osmium, Count = 1 }] }]
        });
        var game = new GameSession(12345, levels);
        if (scenario != 0)
        {
            game.Economy.Credit(2000);
            Assert.True(game.BuyTool(ToolKind.Shovel));
            Assert.True(game.BuyTool(ToolKind.Pickaxe));
        }
        if (scenario == 1)
        {
            for (int x = 6; x <= 12; x++) Assert.True(game.Dig(new Cell(x, 8, 8)));
            Assert.True(game.Dig(new Cell(6, 9, 8)));
            Assert.True(game.Dig(new Cell(7, 9, 8)));
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(7, 8, 8)));
            Assert.Single(game.Turrets).Hp = 3;
            Assert.True(game.Inventory.Add(Material.Stone, 8));
            for (int z = 12; z <= 19; z++) Assert.True(game.BuildBarrier(new Cell(13, 16, z), Material.Stone));
        }
        if (scenario == 2)
        {
            for (int z = 0; z < WorldGrid.Size; z++) Assert.True(game.Dig(new Cell(13, 15, z)));
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 16, 15)));
            Assert.True(game.Deploy(PieceKind.Knight, new Cell(11, 17, 15)));
            game.Liquids.RestoreState(new LiquidState
            {
                Obsidian = [new(new Cell(8, 16, 8), 2, CrustFace.Bottom, 10),
                    new(new Cell(9, 16, 8), 2, CrustFace.Bottom, 10, 4),
                    new(new Cell(12, 16, 11), 3, CrustFace.West, 15)]
            });
        }

        Assert.True(game.StartLevel(), game.Message);
        AssertOriginalRouteFields(game);
    }

    [Fact]
    public void ReusedNavigationStorageRetainsExactRoutesAfterRepeatedTerrainChanges()
    {
        var game = new GameSession();
        Assert.True(game.StartLevel());
        var damage = Bind<Action<Cell, float, CrustFace?>>(game, "DamageProjectileTerrain");
        var rebuild = Bind<Action>(game, "RebuildNavigation");
        foreach (var cell in new[] { new Cell(8, 15, 8), new Cell(9, 15, 8), new Cell(10, 15, 8) })
        {
            damage(cell, 1000, null);
            Assert.Equal(Material.Air, game.World.GetMaterial(cell));
            rebuild();
            AssertOriginalRouteFields(game);
        }
    }

    private static void AssertOriginalRouteFields(GameSession game)
    {
        var expected = BuildOriginalRouteFields(game);
        var actual = (Dictionary<Cell, Cell>[])typeof(GameSession)
            .GetField("_routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;

        for (int lane = 0; lane < 4; lane++)
        {
            Assert.Equal(expected[lane].Count, actual[lane].Count);
            Assert.True(expected[lane].Keys.SequenceEqual(actual[lane].Keys), $"Lane {lane} changed route discovery order.");
            foreach (var (cell, next) in expected[lane])
                Assert.Equal(next, actual[lane][cell]);
        }
        // Deep stone remains represented; the optimization must not prune digging routes.
        Assert.All(actual, route => Assert.True(route.ContainsKey(new Cell(5, 5, 5))));
    }

    private static T Bind<T>(GameSession game, string name) where T : Delegate =>
        typeof(GameSession).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<T>(game);

    private static Dictionary<Cell, Cell>[] BuildOriginalRouteFields(GameSession game)
    {
        // Freeze the pre-optimization collection/search algorithm while sharing the
        // live physical edge rules. This isolates graph-storage and ordering regressions.
        var hasSupport = Bind<Func<Cell, Zombie?, bool>>(game, "HasZombieSupport");
        var canStep = Bind<Func<Cell, Cell, bool>>(game, "CanPlanStep");
        var canJump = Bind<JumpPlanner>(game, "CanPlanJump");
        var breach = Bind<Func<Cell, int, float>>(game, "BreachCost");
        var nodes = new HashSet<Cell>();
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 1; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (hasSupport(cell, null) && game.World.GetMaterial(cell) != Material.Core) nodes.Add(cell);
                }
        var goals = nodes.Where(cell => Adjacent.Any(direction => WorldGrid.IsCore(cell + direction))).ToArray();
        var incoming = new Dictionary<Cell, List<(Cell Previous, float Cost)>>(nodes.Count);
        var breachCosts = new Dictionary<Cell, float>();
        int referenceAttack = game.Level.Waves.SelectMany(wave => wave.Groups)
            .Select(group => LevelCatalog.ZombieAttack(group.Kind, group.Tier)).DefaultIfEmpty(Balance.BaseAttack).Max();
        float Breach(Cell cell)
        {
            if (!breachCosts.TryGetValue(cell, out var cost)) breachCosts[cell] = cost = breach(cell, referenceAttack);
            return cost;
        }
        foreach (var cell in nodes)
        {
            var edges = new List<(Cell Previous, float Cost)>(16);
            foreach (var horizontal in Horizontal)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    var previous = cell + horizontal + new Cell(0, dy, 0);
                    if (!nodes.Contains(previous) || !canStep(previous, cell)) continue;
                    Cell? clearance = cell.Y > previous.Y ? previous + new Cell(0, 1, 0)
                        : cell.Y < previous.Y ? cell + new Cell(0, 1, 0) : null;
                    var cost = (cell.Y == previous.Y ? 1f : 2f) + Breach(cell) +
                        (clearance.HasValue ? Breach(clearance.Value) : 0);
                    edges.Add((previous, cost));
                }
                var launch = cell + horizontal + horizontal;
                if (nodes.Contains(launch) && canJump(launch, cell, out _, null)) edges.Add((launch, 2));
            }
            incoming.Add(cell, edges);
        }
        Dictionary<Cell, Cell>[] routes = [[], [], [], []];
        for (int lane = 0; lane < 4; lane++)
        {
            var costs = new Dictionary<Cell, float>();
            var frontier = new PriorityQueue<Cell, float>();
            foreach (var goal in goals)
            {
                bool preferred = lane switch { 0 => goal.X == 14, 1 => goal.X == 17, 2 => goal.Z == 14, _ => goal.Z == 17 };
                float cost = preferred ? 0 : 8;
                costs[goal] = cost;
                frontier.Enqueue(goal, cost);
            }
            while (frontier.TryDequeue(out var cell, out var distance))
            {
                if (distance > costs[cell]) continue;
                foreach (var (previous, edgeCost) in incoming[cell])
                {
                    float cost = distance + edgeCost;
                    if (costs.TryGetValue(previous, out var known) && known <= cost) continue;
                    costs[previous] = cost;
                    routes[lane][previous] = cell;
                    frontier.Enqueue(previous, cost);
                }
            }
        }
        return routes;
    }
}
