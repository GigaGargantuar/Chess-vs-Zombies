using System.Numerics;
using System.Collections;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class BottomLayerZombieTests
{
    public static IEnumerable<object[]> Sizes() => Enum.GetValues<ZombieSize>().Select(size => new object[] { size });

    public static IEnumerable<object[]> SpawnCases()
    {
        foreach (var size in Enum.GetValues<ZombieSize>())
            foreach (bool returning in new[] { false, true }) yield return [size, returning];
    }

    [Theory]
    [MemberData(nameof(SpawnCases))]
    public void OrdinaryAndReturningZombiesSpawnOnTheBottomSideRingWithoutUsingInteriorFloorOrCeilingCells(ZombieSize size, bool returning)
    {
        var game = NewBattle(size);
        var shape = new Zombie { Size = size };
        Excavate(game, new Cell(0, 0, 6), new Cell(13, 3, 13));
        PrepareTopAnchors(game, shape);
        Assert.True(game.StartLevel());
        var anchor = new Cell(0, 0, 8);
        // Inspect every real candidate, including supported top anchors and an
        // excavated void floor. Ordinary slots already hold their chosen cell;
        // returning zombies still use the live candidate list.
        var candidates = (List<Cell>)Invoke(game, "FindSizedSpawnCells", shape)!;
        var planned = (Array)((IList)typeof(GameSession).GetField("_standbyWaves",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!)[0]!;
        var plannedCell = (Cell)planned.GetValue(0)!.GetType().GetProperty("Cell")!.GetValue(planned.GetValue(0)!)!;
        Assert.Contains(anchor, candidates);
        Assert.DoesNotContain(new Cell(8, 0, 8), candidates);
        Assert.Contains(new Cell(0, 0, 6), candidates);
        Assert.Contains(new Cell(0, WorldGrid.Size - shape.GridFootprint, 20), candidates);
        Assert.DoesNotContain(new Cell(20, WorldGrid.Size - shape.GridFootprint, 20), candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.InRange(candidate.Y, 0, WorldGrid.Size - shape.GridFootprint);
            Assert.True(candidate.X == 0 || candidate.Z == 0 ||
                candidate.X + shape.GridFootprint == WorldGrid.Size || candidate.Z + shape.GridFootprint == WorldGrid.Size);
        });
        candidates.Clear();
        candidates.Add(anchor);
        if (returning) AddReturningZombie(game, shape);

        Assert.True((bool)Invoke(game, "Spawn", Assert.Single(game.Waves))!);

        var zombie = Assert.Single(game.Zombies);
        Assert.Equal(returning ? 500 : 1, zombie.Id);
        Assert.Equal(size, zombie.Size);
        var expectedCell = returning ? anchor : plannedCell;
        Assert.Equal(expectedCell, zombie.Cell);
        Assert.Equal(zombie.StandAt(expectedCell), zombie.Position);
        if (returning) Assert.Equal(0, zombie.Position.Y - zombie.StandingHeight, 5);
        Assert.True((bool)Invoke(game, "IsBodyClear", zombie.Position, zombie)!);
        Assert.True((bool)Invoke(game, "HasSolidFooting", zombie.Position, zombie)!);
        Assert.Equal(0, game.PendingReinforcementCount);
    }

    [Theory]
    [MemberData(nameof(SpawnCases))]
    public void BlockedSideSpawnsWaitWithoutSpendingQuotaEvenWhenInteriorBottomAndTopAnchorsAreClear(ZombieSize size, bool returning)
    {
        var game = NewBattle(size);
        var shape = new Zombie { Size = size };
        Excavate(game, new Cell(6, 0, 6), new Cell(13, 3, 13));
        var place = TerrainPlacer(game);
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 16; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                    if (x == 0 || z == 0 || x == WorldGrid.Size - 1 || z == WorldGrid.Size - 1)
                        Assert.True(place(new Cell(x, y, z), Material.Stone));
        PrepareTopAnchors(game, shape, includeSide: false);
        Assert.True(game.StartLevel());
        if (returning) AddReturningZombie(game, shape);
        Assert.True((bool)Invoke(game, "HasZombieSupport", new Cell(8, 0, 8), shape)!);
        Assert.True((bool)Invoke(game, "IsBodyClear", shape.StandAt(new Cell(8, 0, 8)), shape)!);
        var top = new Cell(20, WorldGrid.Size - shape.GridFootprint, 20);
        Assert.True((bool)Invoke(game, "HasZombieSupport", top, shape)!);
        Assert.True((bool)Invoke(game, "IsBodyClear", shape.StandAt(top), shape)!);
        Assert.Empty((List<Cell>)Invoke(game, "FindSizedSpawnCells", shape)!);

        game.Update(.5f);

        var wave = Assert.Single(game.Waves);
        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.Empty(game.Zombies);
        Assert.Equal(0, wave.Spawned);
        Assert.Equal(0, wave.BudgetSpent);
        Assert.Equal(returning ? 1 : 0, game.PendingReinforcementCount);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void BottomRoutesReachTheCoreAndEverySizedBodyCanWalkAcrossTheVoid(ZombieSize size)
    {
        var game = NewBattle(size);
        Excavate(game, new Cell(6, 0, 6), new Cell(13, 3, 13));
        BeginWithoutSpawns(game);
        var zombie = AddZombie(game, size, new Cell(8, 0, 8));
        // This traverses the real normal/sized route field from a Y=0 node.
        Assert.True((bool)Invoke(game, "CanZombieOpenCoreRoute", zombie)!);
        var start = zombie.Position;

        game.Update(.2f);

        Assert.True(Vector2.Distance(new Vector2(start.X, start.Z), new Vector2(zombie.Position.X, zombie.Position.Z)) > .01f);
        Assert.Equal(start.Y, zombie.Position.Y, 5);
        Assert.Equal(0, zombie.Cell.Y);
        Assert.Equal(zombie.MaxBodyHp, zombie.BodyHp);
        Assert.True((bool)Invoke(game, "HasSolidFooting", zombie.Position, zombie)!);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void AShortFallLandsAliveOnTheBottomPlane(ZombieSize size)
    {
        var game = NewBattle(size);
        Excavate(game, new Cell(6, 0, 6), new Cell(13, 5, 13));
        BeginWithoutSpawns(game);
        var zombie = AddZombie(game, size, new Cell(8, 2, 8));

        for (int step = 0; step < 200 && zombie.Position.Y > zombie.StandingHeight + .0001f; step++)
            game.Update(.02f);

        Assert.Contains(zombie, game.Zombies);
        Assert.Equal(zombie.MaxBodyHp, zombie.BodyHp);
        Assert.Equal(zombie.StandingHeight, zombie.Position.Y, 4);
        Assert.Equal(0, zombie.Cell.Y);
        Assert.True((bool)Invoke(game, "HasSolidFooting", zombie.Position, zombie)!);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RetreatRoutesWalkBackToSuppliesAlongTheBottomPlane(ZombieSize size)
    {
        var game = NewBattle(size);
        Excavate(game, new Cell(0, 0, 6), new Cell(13, 3, 13));
        BeginWithoutSpawns(game);
        var zombie = AddZombie(game, size, new Cell(8, 0, 8));
        Invoke(game, "BeginZombieRetreat", zombie);
        object?[] stepArguments = [zombie, zombie.Cell, default(Cell)];

        Assert.True((bool)Invoke(game, "TryGetRetreatStep", stepArguments)!);

        var next = (Cell)stepArguments[2]!;
        Assert.NotEqual(zombie.Cell, next);
        Assert.Equal(0, next.Y);
        Assert.True(next.X < zombie.Cell.X);
        Assert.True((bool)Invoke(game, "HasZombieSupport", next, zombie)!);

        zombie.Position = zombie.StandAt(new Cell(0, 0, 8));
        Assert.True((bool)Invoke(game, "TryCompleteRetreat", zombie, 1.1f)!);
        Assert.False(game.IsZombieRetreating(zombie));
        Assert.Equal(1, zombie.RetreatReturns);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EverySizedBottomRetreatAnchorReachesSuppliesAtAllFourHorizontalEdges(ZombieSize size)
    {
        var game = NewBattle(size);
        var zombie = new Zombie { Size = size };
        int edge = WorldGrid.Size - zombie.GridFootprint;
        foreach (var anchor in new[] { new Cell(0, 0, 8), new Cell(edge, 0, 8), new Cell(8, 0, 0), new Cell(8, 0, edge) })
        {
            var position = zombie.StandAt(anchor);
            Assert.True((bool)Invoke(game, "AtSupplyBoundary", zombie, position)!);
            var inward = Vector3.Normalize(new Vector3(16, position.Y, 16) - position);
            Assert.False((bool)Invoke(game, "AtSupplyBoundary", zombie, position + inward)!);
        }
    }

    private static GameSession NewBattle(ZombieSize size)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Waves = [new() { Groups = [new() { Size = size, Kind = ZombieKind.Armed, Count = 1 }] }]
        });
        return new GameSession(12345, levels);
    }

    private static void BeginWithoutSpawns(GameSession game)
    {
        Assert.True(game.StartLevel());
        foreach (var wave in game.Waves) wave.Spawned = wave.TotalCount;
    }

    private static void AddReturningZombie(GameSession game, Zombie shape)
    {
        var saved = new SavedReturningZombie
        {
            TargetLevel = game.LevelNumber, TargetWaveIndex = 0, Id = 500,
            Kind = ZombieKind.Armed, Tier = GearTier.Wood, Size = shape.Size,
            BodyHp = shape.MaxBodyHp, OriginalBudgetCost = 1
        };
        var pending = (List<SavedReturningZombie>)typeof(GameSession)
            .GetField("_returningZombies", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        pending.Add(saved);
    }

    private static void PrepareTopAnchors(GameSession game, Zombie shape, bool includeSide = true)
    {
        var place = TerrainPlacer(game);
        // These supports make clear anchors whose bodies occupy Y=31. Only
        // the one on the horizontal outer ring belongs to the spawn area.
        for (int y = 16; y < WorldGrid.Size - shape.GridFootprint; y++)
        {
            Assert.True(place(new Cell(20, y, 20), Material.Stone));
            if (includeSide) Assert.True(place(new Cell(0, y, 20), Material.Stone));
        }
    }

    private static Func<Cell, Material, bool> TerrainPlacer(GameSession game) =>
        typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, Material, bool>>(game.World);

    private static Zombie AddZombie(GameSession game, ZombieSize size, Cell anchor)
    {
        var zombie = new Zombie { Id = 100, WaveIndex = -1, Size = size, Kind = ZombieKind.Armed, Attack = 1 };
        zombie.Position = zombie.StandAt(anchor);
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static void Excavate(GameSession game, Cell min, Cell max)
    {
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        for (int x = min.X; x <= max.X; x++)
            for (int y = min.Y; y <= max.Y; y++)
                for (int z = min.Z; z <= max.Z; z++)
                    Assert.True(dig(new Cell(x, y, z)));
    }

    private static object? Invoke(GameSession game, string method, params object?[] arguments) =>
        typeof(GameSession).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, arguments);
}
