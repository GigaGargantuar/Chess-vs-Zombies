using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GiantDeathBlockTests
{
    [Theory]
    [InlineData(ZombieSize.Kilo, false)]
    [InlineData(ZombieSize.Kilo, true)]
    [InlineData(ZombieSize.Mega, false)]
    [InlineData(ZombieSize.Mega, true)]
    public void DeathReleasesEveryHandOrStowedBlockToTheGroundInsteadOfCreatingAFloatingCluster(ZombieSize size, bool scooping)
    {
        var (game, giant, expected) = Equipped(size);
        if (scooping) Invoke(game, "BeginKiloBucketAction", giant);
        Assert.All(game.KiloCovers, cover => Assert.Equal(!scooping, cover.IsActive));

        game.DamageZombie(giant, 10_000, DamageKind.Chemical);

        Assert.Empty(game.KiloCovers);
        Assert.Empty(game.MegaWeapons);
        Assert.Equal(expected.Length, game.PendingBlockDrops.Count);
        game.Update(.05f);

        Assert.Empty(game.PendingBlockDrops);
        var released = ReleasedBlocks(game).ToArray();
        Assert.Equal(expected.Length, released.Length);
        foreach (var held in expected)
        {
            var actual = Assert.Single(released, block => block.Hp == held.Hp);
            Assert.Equal(held.Material, actual.Material);
            Assert.Equal(held.WasBarrier, actual.WasBarrier);
            Assert.InRange(actual.Cell.Y, 16, size == ZombieSize.Mega ? 17 : 16);
        }
        Assert.Equal(expected.Length, game.Falls.Blocks.Count);
        Assert.All(game.Falls.Blocks, fall => Assert.True(fall.Position.Y > fall.Destination.Center.Y));

        // Repeated cleanup and subsequent presentation updates neither duplicate
        // the finite carried blocks nor leave a phantom held-cover renderer.
        Invoke(game, "HandleKiloDeath", giant);
        Invoke(game, "QueueDeathBlock", giant);
        game.Update(3);
        Assert.Equal(released, ReleasedBlocks(game).ToArray());
        Assert.Empty(game.PendingBlockDrops);
        Assert.Empty(game.Falls.Blocks);
        Assert.Empty(game.KiloCovers);
    }

    [Theory]
    [InlineData(ZombieSize.Kilo)]
    [InlineData(ZombieSize.Mega)]
    public void IndependentlyPositionedHandsReleasingIntoTheSameColumnStackWithoutLosingBlocks(ZombieSize size)
    {
        var (game, giant, expected) = Equipped(size);
        var blocks = HeldBlocks(game, giant);
        foreach (var block in blocks)
            block!.GetType().GetProperty("Offset")!.SetValue(block, new Vector3(.1f, 0, .1f));

        game.DamageZombie(giant, 10_000, DamageKind.Chemical);
        game.Update(.05f);

        Assert.Empty(game.PendingBlockDrops);
        var released = ReleasedBlocks(game).ToArray();
        Assert.Equal(expected.Length, released.Length);
        Assert.Single(released.Select(block => (block.Cell.X, block.Cell.Z)).Distinct());
        Assert.Equal(Enumerable.Range(16, expected.Length), released.Select(block => block.Cell.Y).Order());
        Assert.Equal(expected.Select(block => block.Hp).Order(), released.Select(block => block.Hp).Order());
    }

    [Fact]
    public void ReleasedBlockMayStillAttachToAnExistingSupportedWall()
    {
        var game = new GameSession();
        for (int y = 16; y <= 21; y++)
            Assert.True((bool)Invoke(game.World, "AddBarrier", new Cell(7, y, 5), Material.Stone, (float?)500)!);
        Pending(game).Add(new BlockDrop(new Cell(6, 21, 5), Material.Copper, 321, true));
        Pending(game).Add(new BlockDrop(new Cell(5, 21, 5), Material.Iron, 777));

        Invoke(game, "ResolveBlockDrops");

        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(321, game.World.GetBarrier(new Cell(6, 21, 5))!.Hp);
        Assert.Equal(777, game.World.GetTerrainHp(new Cell(5, 21, 5)));
        Assert.Empty(game.Falls.Blocks);
    }

    [Fact]
    public void ReleasedFallingBlockStillCrushesALivingZombieBelowIt()
    {
        var game = new GameSession();
        var victim = new Zombie { Id = 200, WaveIndex = -1, Position = new Cell(5, 16, 5).Center };
        game.Zombies.Add(victim);
        Pending(game).Add(new BlockDrop(new Cell(5, 23, 5), Material.Stone, 311));

        Invoke(game, "ResolveBlockDrops");

        Assert.Equal(0, victim.Hp);
        Assert.Equal(Material.Stone, game.World.GetMaterial(new Cell(5, 16, 5)));
        Assert.Equal(311, game.World.GetTerrainHp(new Cell(5, 16, 5)));
        Assert.Single(game.Falls.Blocks);
    }

    [Fact]
    public void CompletelyOccupiedDeathColumnKeepsItsFinitePendingBlockUntilAnOpeningExists()
    {
        var game = new GameSession();
        for (int y = 16; y < WorldGrid.Size; y++)
            Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", new Cell(5, y, 5), Material.Stone)!);
        var drop = new BlockDrop(new Cell(5, 20, 5), Material.Copper, 321, true);
        Pending(game).Add(drop);

        Invoke(game, "ResolveBlockDrops");
        Invoke(game, "ResolveBlockDrops");

        Assert.Equal(drop, Assert.Single(game.PendingBlockDrops));
        Assert.True((bool)Invoke(game.World, "Dig", new Cell(5, 25, 5))!);
        Invoke(game, "ResolveBlockDrops");
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(321, game.World.GetBarrier(new Cell(5, 25, 5))!.Hp);
    }

    private static (GameSession Game, Zombie Giant, Held[] Blocks) Equipped(ZombieSize size)
    {
        var game = new GameSession();
        var giant = new Zombie { Id = 100, Size = size, WaveIndex = -1 };
        giant.Position = giant.StandAt(new Cell(5, 21, 5));
        game.Zombies.Add(giant);
        typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(giant, 0);
        var blocks = new[]
        {
            new Held(Material.Dirt, 83, false), new Held(Material.Copper, 321, true),
            new Held(Material.Stone, 207, false), new Held(Material.Iron, 777, true)
        }.Take(size == ZombieSize.Mega ? 4 : 2).ToArray();
        var heldType = typeof(GameSession).GetNestedType("HeldKiloBlock", BindingFlags.NonPublic)!;
        var held = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(heldType))!;
        for (int index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            var real = Activator.CreateInstance(heldType, index, block.Material, block.Hp, block.WasBarrier)!;
            heldType.GetProperty("Offset")!.SetValue(real,
                new Vector3(index % 2 == 0 ? -.5f : .5f, size == ZombieSize.Mega ? index / 2 - .5f : 0, .5f));
            held.Add(real);
        }
        var ownership = (IDictionary)typeof(GameSession).GetField("_kiloBlocks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        ownership[giant.Id] = held;
        return (game, giant, blocks);
    }

    private static IList HeldBlocks(GameSession game, Zombie giant) => (IList)((IDictionary)typeof(GameSession)
        .GetField("_kiloBlocks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!)[giant.Id]!;

    private static List<BlockDrop> Pending(GameSession game) => (List<BlockDrop>)typeof(GameSession)
        .GetField("_pendingBlockDrops", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;

    private static IEnumerable<Placed> ReleasedBlocks(GameSession game)
    {
        for (int x = 3; x <= 9; x++)
            for (int y = 16; y < WorldGrid.Size; y++)
                for (int z = 3; z <= 9; z++)
                {
                    var cell = new Cell(x, y, z);
                    var barrier = game.World.GetBarrier(cell);
                    var material = barrier?.Content ?? game.World.GetMaterial(cell);
                    if (Balance.IsTerrainMaterial(material))
                        yield return new Placed(cell, material, barrier?.Hp ?? game.World.GetTerrainHp(cell), barrier != null);
                }
    }

    private sealed record Held(Material Material, float Hp, bool WasBarrier);
    private sealed record Placed(Cell Cell, Material Material, float Hp, bool WasBarrier);
    private static object? Invoke(object target, string method, params object?[] arguments) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
