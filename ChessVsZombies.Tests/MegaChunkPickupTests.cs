using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MegaChunkPickupTests
{
    [Fact]
    public void OneActionExtractsFourConnectedBlocksWithoutUsingTheBackpackOrOwnFooting()
    {
        var (game, mega) = Create();
        var before = Terrain(game);
        Assert.True(Grab(game, mega));
        var removed = before.Except(Terrain(game)).ToArray();

        Assert.Equal(4, removed.Length);
        AssertConnected(removed);
        Assert.Equal(4, game.MegaWeapons.Count);
        Assert.Equal(1, mega.CarriedBlocks);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.DirtCount);
        for (int x = 5; x <= 7; x++)
            for (int z = 8; z <= 10; z++) Assert.Equal(Material.Dirt, game.World.GetMaterial(new Cell(x, 15, z)));
        Assert.All(game.MegaWeapons, block => Assert.Equal(100, block.Hp));
        Assert.Equal(2, game.MegaWeapons.Count(block => block.HandIndex == 0));
        Assert.Equal(2, game.MegaWeapons.Count(block => block.HandIndex == 1));
    }

    [Fact]
    public void ExtractedMixedMaterialChunkRetainsEveryBlocksRemainingDurability()
    {
        var (game, mega) = Create();
        ClearPickupTerrain(game);
        var parts = new[]
        {
            (new Cell(3, 16, 8), Material.Copper, 77f),
            (new Cell(3, 16, 9), Material.Wood, 59f),
            (new Cell(4, 16, 8), Material.Stone, 311f),
            (new Cell(4, 16, 9), Material.Osmium, 1234f)
        };
        foreach (var (cell, material, hp) in parts) Add(game, cell, material, hp);

        Assert.True(Grab(game, mega));

        foreach (var (cell, material, hp) in parts)
        {
            Assert.Equal(Material.Air, game.World.GetMaterial(cell));
            var block = Assert.Single(game.MegaWeapons, block => block.Material == material);
            Assert.Equal(hp, block.Hp);
            Assert.True(block.WasBarrier);
        }
        Assert.Equal(1, mega.CarriedBlocks);
    }

    [Fact]
    public void InsufficientConnectedMaterialDoesNotPartiallyRemoveTerrainOrRaiseBackpack()
    {
        var (game, mega) = Create();
        ClearPickupTerrain(game);
        foreach (var cell in new[] { new Cell(3, 16, 8), new Cell(3, 16, 9), new Cell(4, 16, 8) }) Add(game, cell);
        var before = Terrain(game);

        Assert.False(Grab(game, mega));

        Assert.Equal(before.OrderBy(cell => cell.ToString()), Terrain(game).OrderBy(cell => cell.ToString()));
        Assert.Empty(game.MegaWeapons);
        Assert.Equal(1, mega.CarriedBlocks);
    }

    [Fact]
    public void FourDisconnectedBlocksCannotBeCollectedAsOneTerrainChunk()
    {
        var (game, mega) = Create();
        ClearPickupTerrain(game);
        foreach (var cell in new[] { new Cell(3, 16, 6), new Cell(3, 16, 12), new Cell(9, 16, 6), new Cell(9, 16, 12) }) Add(game, cell);
        int before = Terrain(game).Count;

        Assert.False(Grab(game, mega));

        Assert.Equal(before, Terrain(game).Count);
        Assert.Empty(game.MegaWeapons);
    }

    [Theory]
    [InlineData("tower")]
    [InlineData("ally")]
    public void AChunkCannotExtractATowerCellOrAnAlliesSupportingBlock(string occupant)
    {
        var (game, mega) = Create();
        ClearPickupTerrain(game);
        foreach (var cell in Square()) Add(game, cell);
        var occupied = new Cell(3, 16, 8);
        if (occupant == "tower") game.Turrets.Add(new Turret { Id = 200, Cell = occupied, Kind = PieceKind.Pawn });
        else
        {
            var ally = new Zombie { Id = 101 };
            ally.Position = ally.StandAt(occupied + new Cell(0, 1, 0));
            game.Zombies.Add(ally);
        }
        int before = Terrain(game).Count;

        Assert.False(Grab(game, mega));

        Assert.Equal(before, Terrain(game).Count);
        Assert.Empty(game.MegaWeapons);
    }

    [Fact]
    public void AConnectedNonSquareChunkCanBeRippedOutWhenNoCompleteFaceIsAvailable()
    {
        var (game, mega) = Create();
        ClearPickupTerrain(game);
        var cells = new[] { new Cell(3, 16, 8), new Cell(3, 16, 9), new Cell(3, 16, 10), new Cell(4, 16, 9) };
        foreach (var cell in cells) Add(game, cell);

        Assert.True(Grab(game, mega));

        Assert.Equal(4, game.MegaWeapons.Count);
        Assert.All(cells, cell => Assert.Equal(Material.Air, game.World.GetMaterial(cell)));
    }

    [Fact]
    public void DestroyedHandCapacityIsRefilledInOneConnectedExtractionWithoutReplacingOtherBlocks()
    {
        var (game, mega) = Create();
        Assert.True(Grab(game, mega));
        Invoke(game, "DamageKiloCover", mega.Id, 2, 17f);
        Invoke(game, "DamageKiloCover", mega.Id, 0, 1000f);
        Invoke(game, "DamageKiloCover", mega.Id, 1, 1000f);
        var surviving = game.MegaWeapons.ToArray();
        var before = Terrain(game);

        Assert.True(Grab(game, mega));

        var removed = before.Except(Terrain(game)).ToArray();
        Assert.Equal(2, removed.Length);
        AssertConnected(removed);
        Assert.Equal(4, game.MegaWeapons.Count);
        foreach (var block in surviving)
        {
            var after = Assert.Single(game.MegaWeapons, after => after.BlockIndex == block.BlockIndex);
            Assert.Equal(block.Hp, after.Hp);
            Assert.Equal(block.Material, after.Material);
        }
        Assert.Equal(1, mega.CarriedBlocks);
    }

    [Fact]
    public void AThrownChunkCanBeReplacedByOneNewFourBlockExtraction()
    {
        var (game, mega) = Create();
        Assert.True(Grab(game, mega));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 16, 9)));
        Assert.True((bool)Invoke(game, "TryThrowMegaWeapon", mega)!);
        int before = Terrain(game).Count;

        Assert.True(Grab(game, mega));

        Assert.Equal(before - 4, Terrain(game).Count);
        Assert.Equal(4, game.MegaWeapons.Count);
        Assert.Equal(4, game.MegaProjectiles.Count);
        Assert.Equal(1, mega.CarriedBlocks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void BoundaryResupplyKeepsTheFullHeldChunkAndItsSeparateBackpack(int backpackBlocks)
    {
        var (game, mega) = Create();
        Assert.True(Grab(game, mega));
        Invoke(game, "DamageKiloCover", mega.Id, 2, 17f);
        typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(mega, backpackBlocks);
        var expected = game.MegaWeapons.Select(block => (block.BlockIndex, block.Material, block.Hp, block.WasBarrier)).ToArray();
        int terrainCount = Terrain(game).Count;
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        game.Waves[0].BudgetSpent = 10;
        mega.Position = mega.StandAt(new Cell(0, 16, 8));
        var states = (System.Collections.IDictionary)typeof(GameSession)
            .GetField("_retreating", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var stateType = typeof(GameSession).GetNestedType("RetreatState", BindingFlags.NonPublic)!;
        states[mega.Id] = Activator.CreateInstance(stateType, nonPublic: true);

        Assert.True((bool)Invoke(game, "TryCompleteRetreat", mega, 1.1f)!);

        Assert.False(game.IsZombieRetreating(mega));
        Assert.Equal(Math.Max(1, backpackBlocks), mega.CarriedBlocks);
        Assert.Equal(expected, game.MegaWeapons.Select(block => (block.BlockIndex, block.Material, block.Hp, block.WasBarrier)).ToArray());
        Assert.Equal(terrainCount, Terrain(game).Count);
        Assert.Equal(1, mega.RetreatReturns);
        Assert.Equal(10, game.Waves[0].BudgetSpent);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BucketWorkAndMissingFootingPreventExtraction(bool buckets)
    {
        var (game, mega) = Create();
        if (buckets) Invoke(game, "BeginKiloBucketAction", mega);
        else mega.Position += Vector3.UnitY;
        int before = Terrain(game).Count;

        Assert.False(Grab(game, mega));

        Assert.Equal(before, Terrain(game).Count);
        Assert.Empty(game.MegaWeapons);
    }

    private static (GameSession Game, Zombie Mega) Create()
    {
        var game = new GameSession();
        var mega = new Zombie { Id = 100, Size = ZombieSize.Mega, Attack = 9, WaveIndex = 0 };
        mega.Position = mega.StandAt(new Cell(5, 16, 8));
        mega.FaceToward(Vector3.UnitX);
        game.Zombies.Add(mega);
        return (game, mega);
    }

    private static Cell[] Square() => [new(3, 16, 8), new(3, 16, 9), new(4, 16, 8), new(4, 16, 9)];

    private static void ClearPickupTerrain(GameSession game)
    {
        for (int x = 3; x <= 9; x++)
            for (int z = 6; z <= 12; z++)
                if (!(x is >= 5 and <= 7 && z is >= 8 and <= 10)) Invoke(game.World, "Dig", new Cell(x, 15, z));
    }

    private static void Add(GameSession game, Cell cell, Material material = Material.Dirt, float? hp = null) =>
        Assert.True((bool)Invoke(game.World, "AddBarrier", cell, material, hp)!);

    private static bool Grab(GameSession game, Zombie mega) => (bool)Invoke(game, "TryGrabMegaWorldBlock", mega)!;

    private static HashSet<Cell> Terrain(GameSession game)
    {
        var result = new HashSet<Cell>();
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 0; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (Balance.IsTerrainMaterial(game.World.GetMaterial(cell)) || game.World.GetBarrier(cell) != null) result.Add(cell);
                }
        return result;
    }

    private static void AssertConnected(Cell[] cells)
    {
        var reached = new HashSet<Cell> { cells[0] };
        while (true)
        {
            var next = cells.Where(cell => !reached.Contains(cell) && reached.Any(other => (cell - other).Manhattan == 1)).ToArray();
            if (next.Length == 0) break;
            reached.UnionWith(next);
        }
        Assert.Equal(cells.Length, reached.Count);
    }

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
