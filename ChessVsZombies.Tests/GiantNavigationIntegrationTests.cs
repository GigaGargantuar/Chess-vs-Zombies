using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GiantNavigationIntegrationTests
{
    private delegate bool SizedRoute(Zombie zombie, Cell current, out Cell next, bool fallback);
    private delegate bool GiantStepCost(Cell from, Cell to, int footprint, byte[] occupied, float[] costs, int attack, out float cost);

    [Fact]
    public void AMegaCannotFitATwoCubeTunnelButCanActuallyWalkAfterItIsExpandedToThree()
    {
        var game = new GameSession();
        Carve(game, 5, 11, 8, 9, 5, 6);
        var anchor = new Cell(5, 8, 5);
        var kilo = new Zombie { Size = ZombieSize.Kilo };
        var mega = new Zombie { Id = 100, Size = ZombieSize.Mega, Attack = 9, WaveIndex = -1 };
        mega.Position = mega.StandAt(anchor);
        Assert.True((bool)Invoke(game, "IsBodyClear", kilo.StandAt(anchor), kilo)!);
        Assert.False((bool)Invoke(game, "IsBodyClear", mega.Position, mega)!);

        Carve(game, 5, 11, 8, 10, 5, 7);
        Assert.True((bool)Invoke(game, "IsBodyClear", mega.Position, mega)!);
        Assert.True((bool)Invoke(game, "HasZombieSupport", anchor, mega)!);
        game.Zombies.Add(mega);
        var destination = anchor + new Cell(1, 0, 0);
        mega.NextCell = destination;

        CompleteStep(game, mega);

        AssertNear(mega.StandAt(destination), mega.Position);
        Assert.Equal(90, mega.BodyHp);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMegaActuallyClimbsAndStepsDownAOneBlockLedgeWithoutCuttingThroughItsCorner(bool climb)
    {
        var game = new GameSession();
        for (int x = 8; x <= 10; x++)
            for (int z = 8; z <= 10; z++)
                Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", new Cell(x, 16, z), Material.Stone)!);
        var from = climb ? new Cell(5, 16, 8) : new Cell(10, 17, 8);
        var to = climb ? new Cell(6, 17, 8) : new Cell(11, 16, 8);
        var mega = new Zombie { Id = 100, Size = ZombieSize.Mega, WaveIndex = -1, Attack = 9 };
        mega.Position = mega.StandAt(from);
        game.Zombies.Add(mega);
        Assert.True((bool)Invoke(game, "HasZombieSupport", from, mega)!);
        Assert.True((bool)Invoke(game, "HasZombieSupport", to, mega)!);
        mega.NextCell = to;

        CompleteStep(game, mega);

        AssertNear(mega.StandAt(to), mega.Position);
        Assert.True((bool)Invoke(game, "HasZombieSupport", mega.Cell, mega)!);
        Assert.Equal(90, mega.BodyHp);
    }

    [Fact]
    public void TheSharedMegaFieldLeadsTheRealThreeCubeBodyToTheCore()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Size = ZombieSize.Mega, IsBoss = true }] }] });
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var mega = new Zombie { Id = 100, Size = ZombieSize.Mega, IsBoss = true, Attack = 9 };
        mega.Position = mega.StandAt(new Cell(9, 16, 15));
        var origin = mega.Position;
        game.Zombies.Add(mega);
        var cooldowns = (IDictionary<int, float>)typeof(GameSession)
            .GetField("_megaActionCooldowns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        cooldowns[mega.Id] = 100; // Isolate walking from the separately tested terrain pickup/throw action.

        for (int frame = 0; frame < 500 && game.CoreHp == Balance.CoreHp; frame++)
        {
            game.Update(.02f);
            Assert.Equal(17.5f, mega.Position.Y, 4);
            Assert.True((bool)Invoke(game, "IsBodyClear", mega.Position, mega)!);
            Assert.Equal(90, mega.BodyHp);
        }

        Assert.True(mega.Position.X > origin.X + 2.9f, $"Mega stopped at {mega.Position}.");
        Assert.True(game.CoreHp < Balance.CoreHp);
        var min = mega.Position - mega.BodyHalfExtents;
        var max = mega.Position + mega.BodyHalfExtents;
        Assert.True(max.X == 15 || min.X == 17 || max.Z == 15 || min.Z == 17);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OptimizedMegaGridCostsMatchPhysicalSweepsForLevelClimbingAndDescendingEdges(int scenario)
    {
        var game = new GameSession();
        var shape = new Zombie { Size = ZombieSize.Mega };
        if (scenario == 1)
        {
            Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", new Cell(9, 17, 9), Material.Copper)!);
            Assert.True((bool)Invoke(game.World, "AddBarrier", new Cell(8, 18, 8), Material.Stone, (float?)123.5f)!);
        }
        if (scenario == 2)
            game.Liquids.RestoreState(new LiquidState { Obsidian =
                [new(new Cell(8, 16, 8), 2, CrustFace.West, 10), new(new Cell(9, 18, 9), 3, CrustFace.Top, 15)] });
        int count = WorldGrid.Size * WorldGrid.Size * WorldGrid.Size;
        var occupied = new byte[count];
        var costs = new float[count];
        Array.Fill(costs, float.NaN);
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 0; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                {
                    var material = game.World.GetMaterial(new Cell(x, y, z));
                    occupied[(x * WorldGrid.Size + y) * WorldGrid.Size + z] = material == Material.Core ? (byte)2 :
                        material == Material.Air ? (byte)0 : (byte)1;
                }
        var optimized = Bind<GiantStepCost>(game, "TryGiantGridStepCost");
        var physical = Bind<Func<Vector3, Vector3, Zombie, HashSet<Cell>?, bool>>(game, "TrySizedSegmentBreaches");
        var breachCost = Bind<Func<Cell, int, float>>(game, "BreachCost");
        var directions = new[] { new Cell(1, 0, 0), new Cell(-1, 0, 0), new Cell(0, 0, 1), new Cell(0, 0, -1) };
        foreach (var from in new[] { new Cell(7, 16, 8), new Cell(12, 16, 15) })
            foreach (var direction in directions)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var to = from + direction + new Cell(0, dy, 0);
                    var waypoint = dy > 0 ? from + new Cell(0, 1, 0) : dy < 0 ? to + new Cell(0, 1, 0) : to;
                    var breaches = new HashSet<Cell>();
                    // A sub-contact-epsilon displacement bypasses the canonical
                    // whole-cell shortcut and exercises the actual runtime slab
                    // sweeps without changing which physical solids are contacted.
                    var epsilon = new Vector3(.000002f);
                    bool expected = physical(shape.StandAt(from) + epsilon, shape.StandAt(waypoint) + epsilon, shape, breaches) &&
                        physical(shape.StandAt(waypoint) + epsilon, shape.StandAt(to) + epsilon, shape, breaches);

                    bool actual = optimized(from, to, 3, occupied, costs, 9, out float cost);

                    Assert.Equal(expected, actual);
                    if (expected)
                    {
                        float expectedCost = (dy == 0 ? 1 : 2) + (float)breaches.Sum(cell => (double)breachCost(cell, 9));
                        Assert.InRange(Math.Abs(expectedCost - cost), 0, .0001f);
                    }
                }
    }

    [Fact]
    public void AWholeMegaFieldStaysWithinTheExistingAllocationBudgetAndIsSharedByRepeatedQueries()
    {
        var game = new GameSession();
        var mega = new Zombie { Size = ZombieSize.Mega };
        var route = Bind<SizedRoute>(game, "TryGetSizedRoute");
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(route(mega, new Cell(2, 16, 12), out _, true));

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 64L * 1024 * 1024);
        var fields = (IDictionary)typeof(GameSession).GetField("_sizeRoutes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var first = fields[ZombieSize.Mega];
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int query = 0; query < 100; query++) Assert.True(route(mega, new Cell(2 + query % 4, 16, 12), out _, true));
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(first, fields[ZombieSize.Mega]);
        Assert.InRange(allocated, 0, 64L * 1024);
    }

    [Theory]
    [InlineData(-1.4f)]
    [InlineData(1.4f)]
    public void ARealPelletHitsTheMegasCubeCornerBeyondTheOldSphericalHitbox(float corner)
    {
        var game = new GameSession();
        var mega = new Zombie { Id = 100, WaveIndex = -1, Size = ZombieSize.Mega, Position = new Vector3(10.5f, 20.5f, 10.5f) };
        game.Zombies.Add(mega);
        var offset = new Vector3(0, corner, corner);
        game.Projectiles.Add(new Projectile
        {
            Origin = mega.Position + offset - Vector3.UnitX * 4, Target = mega.Position + offset + Vector3.UnitX * 4,
            Kind = ShotKind.Pellet, Damage = 3, Duration = .1f
        });

        Invoke(game, "UpdateProjectiles", .1f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(87, mega.BodyHp);
    }

    private static void CompleteStep(GameSession game, Zombie zombie)
    {
        for (int frame = 0; frame < 120 && zombie.NextCell.HasValue; frame++)
        {
            Invoke(game, "UpdateZombie", zombie, .05f);
            Assert.True((bool)Invoke(game, "IsBodyClear", zombie.Position, zombie)!, $"Body entered terrain at {zombie.Position}.");
        }
        Assert.Null(zombie.NextCell);
    }

    private static void Carve(GameSession game, int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        for (int x = minX; x <= maxX; x++)
            for (int y = minY; y <= maxY; y++)
                for (int z = minZ; z <= maxZ; z++)
                    if (game.World.GetMaterial(new Cell(x, y, z)) != Material.Air)
                        Assert.True((bool)Invoke(game.World, "Dig", new Cell(x, y, z))!);
    }

    private static T Bind<T>(GameSession game, string name) where T : Delegate => typeof(GameSession)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<T>(game);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void AssertNear(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, .00002f);
}
