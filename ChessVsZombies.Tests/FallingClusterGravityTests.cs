using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class FallingClusterGravityTests
{
    [Theory]
    [InlineData(Material.Dirt, false)]
    [InlineData(Material.Stone, false)]
    [InlineData(Material.Wood, true)]
    public void AnAlreadyFallingBlockPassesAGroundedSideWallUntilItLands(Material material, bool barrier)
    {
        var game = new GameSession();
        for (int y = 16; y <= 20; y++) Place(game, new Cell(9, y, 8), Material.Stone);
        var source = new Cell(8, 23, 8);
        var destination = new Cell(8, 16, 8);
        Place(game, source, material, barrier, 17);

        game.Update(.001f);

        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        AssertBlock(game, destination, material, barrier, 17);
        var fall = Assert.Single(game.Falls.Blocks);
        Assert.Equal(destination, fall.Destination);
        Assert.Equal(source.Center, fall.Position);
        Assert.Equal(barrier ? Material.Barrier : material, fall.Material);
        Assert.Equal(barrier ? material : (Material?)null, fall.Content);
        for (int y = 16; y <= 20; y++) Assert.Equal(Material.Stone, game.World.GetMaterial(new Cell(9, y, 8)));
        game.Update(3);
        Assert.Empty(game.Falls.Blocks);
        AssertBlock(game, destination, material, barrier, 17);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADetachedMixedClusterFallsTogetherAndStopsAtItsFirstRealFloor(bool unevenFloor)
    {
        var game = new GameSession();
        if (unevenFloor)
            for (int y = 16; y <= 18; y++) Place(game, new Cell(9, y, 8), Material.Stone);
        (Cell Source, Material Material, bool Barrier, float Hp)[] blocks =
        [
            (new(8, 23, 8), Material.Dirt, false, 33),
            (new(9, 23, 8), Material.Stone, false, 234),
            (new(9, 24, 8), Material.Wood, true, 17)
        ];
        foreach (var block in blocks) Place(game, block.Source, block.Material, block.Barrier, block.Hp);
        var inventory = Enum.GetValues<Material>().Select(game.Inventory.GetCount).ToArray();
        int distance = unevenFloor ? 4 : 7;

        game.Update(.001f);

        Assert.Equal(blocks.Length, game.Falls.Blocks.Count);
        foreach (var block in blocks)
        {
            var destination = block.Source - new Cell(0, distance, 0);
            Assert.Equal(Material.Air, game.World.GetMaterial(block.Source));
            AssertBlock(game, destination, block.Material, block.Barrier, block.Hp);
            Assert.Equal(block.Source.Center, Assert.Single(game.Falls.Blocks, fall => fall.Destination == destination).Position);
        }
        game.Update(.5f);
        foreach (var block in blocks)
        {
            var destination = block.Source - new Cell(0, distance, 0);
            var fall = Assert.Single(game.Falls.Blocks, item => item.Destination == destination);
            Assert.Equal(block.Source.Center.Y - FallAnimations.DistanceAfter(.5f), fall.Position.Y, 4);
            Assert.Equal(block.Source.Center.X, fall.Position.X);
            Assert.Equal(block.Source.Center.Z, fall.Position.Z);
        }
        Assert.Equal(inventory, Enum.GetValues<Material>().Select(game.Inventory.GetCount));
        game.Update(3);
        Assert.Empty(game.Falls.Blocks);
        foreach (var block in blocks)
            AssertBlock(game, block.Source - new Cell(0, distance, 0), block.Material, block.Barrier, block.Hp);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AShortDropKeepsEveryTallClusterMembersDistinctAnimationOrigin(int distance)
    {
        var game = new GameSession();
        (Cell Source, Material Material, bool Barrier, float Hp)[] blocks =
        [
            (new(8, 16 + distance, 8), Material.Dirt, false, 33),
            (new(8, 17 + distance, 8), Material.Stone, false, 234),
            (new(8, 18 + distance, 8), Material.Wood, true, 17),
            (new(8, 19 + distance, 8), Material.Copper, false, 63)
        ];
        foreach (var block in blocks) Place(game, block.Source, block.Material, block.Barrier, block.Hp);

        game.Update(.001f);

        Assert.Equal(blocks.Length, game.Falls.Blocks.Count);
        foreach (var block in blocks)
        {
            var destination = block.Source - new Cell(0, distance, 0);
            AssertBlock(game, destination, block.Material, block.Barrier, block.Hp);
            var fall = Assert.Single(game.Falls.Blocks, item => item.Destination == destination);
            Assert.Equal(block.Source.Center, fall.Position);
            Assert.Equal(block.Barrier ? Material.Barrier : block.Material, fall.Material);
            Assert.Equal(block.Barrier ? block.Material : (Material?)null, fall.Content);
        }
        game.Update(.5f);
        foreach (var block in blocks)
        {
            var destination = block.Source - new Cell(0, distance, 0);
            var fall = Assert.Single(game.Falls.Blocks, item => item.Destination == destination);
            Assert.Equal(block.Source.Center.Y - FallAnimations.DistanceAfter(.5f), fall.Position.Y, 4);
        }
        game.Update(2);
        Assert.Empty(game.Falls.Blocks);
        foreach (var block in blocks)
            AssertBlock(game, block.Source - new Cell(0, distance, 0), block.Material, block.Barrier, block.Hp);
    }

    [Fact]
    public void AFallenMixedClusterConservesDisplacedLiquidsAndRestoresItsMaterialAndDurability()
    {
        var game = new GameSession();
        game.Liquids.RestoreState(new LiquidState
        {
            Cells = [new(new Cell(8, 16, 8), 7, 0, 0), new(new Cell(9, 17, 8), 0, 4, 0)]
        });
        (Cell Source, Material Material, bool Barrier, float Hp)[] blocks =
        [
            (new(8, 20, 8), Material.Dirt, false, 33),
            (new(9, 20, 8), Material.Stone, false, 234),
            (new(9, 21, 8), Material.Wood, true, 17)
        ];
        foreach (var block in blocks) Place(game, block.Source, block.Material, block.Barrier, block.Hp);

        game.Update(.001f);

        Assert.Equal(7, game.Liquids.Cells.Sum(cell => cell.Water));
        Assert.Equal(4, game.Liquids.Cells.Sum(cell => cell.Mercury));
        Assert.Equal(0, game.Liquids.GetEscapedUnits(LiquidKind.Water));
        Assert.Equal(0, game.Liquids.GetEscapedUnits(LiquidKind.Mercury));
        Assert.All(game.Liquids.Cells, cell => Assert.Equal(Material.Air, game.World.GetMaterial(cell.Cell)));
        var checkpoint = game.ExportState();
        var restored = new GameSession();
        typeof(GameSession).GetMethod("RestoreCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(restored, [checkpoint]);

        Assert.Empty(restored.Falls.Blocks);
        foreach (var block in blocks)
            AssertBlock(restored, block.Source - new Cell(0, 4, 0), block.Material, block.Barrier, block.Hp);
        Assert.Equal(checkpoint.Liquids!.Cells.OrderBy(cell => cell.Cell.X).ThenBy(cell => cell.Cell.Y).ThenBy(cell => cell.Cell.Z),
            restored.ExportState().Liquids!.Cells.OrderBy(cell => cell.Cell.X).ThenBy(cell => cell.Cell.Y).ThenBy(cell => cell.Cell.Z));
        restored.Update(.001f);
        foreach (var block in blocks)
            AssertBlock(restored, block.Source - new Cell(0, 4, 0), block.Material, block.Barrier, block.Hp);
        Assert.Equal(7, restored.Liquids.Cells.Sum(cell => cell.Water));
        Assert.Equal(4, restored.Liquids.Cells.Sum(cell => cell.Mercury));
    }

    [Fact]
    public void ASideAttachedSpanRemainsUntilItsGroundedConnectionIsSevered()
    {
        var game = new GameSession();
        for (int y = 16; y <= 20; y++) Place(game, new Cell(6, y, 8), Material.Stone);
        Place(game, new Cell(7, 20, 8), Material.Wood, barrier: true, hp: 17);
        var span = new[] { new Cell(8, 20, 8), new Cell(9, 20, 8), new Cell(9, 21, 8) };
        foreach (var cell in span) Place(game, cell, Material.Dirt, hp: 33);

        game.Update(.001f);

        Assert.Empty(game.Falls.Blocks);
        foreach (var cell in span) AssertBlock(game, cell, Material.Dirt, false, 33);
        Assert.True(game.Remove(new Cell(7, 20, 8)));
        foreach (var cell in span)
        {
            Assert.Equal(Material.Air, game.World.GetMaterial(cell));
            AssertBlock(game, cell - new Cell(0, 4, 0), Material.Dirt, false, 33);
        }
        Assert.Equal(span.Length, game.Falls.Blocks.Count);
        for (int y = 16; y <= 20; y++) Assert.Equal(Material.Stone, game.World.GetMaterial(new Cell(6, y, 8)));
    }

    [Fact]
    public void AReleasedCorpseBlockFallsEvenWhenItsReleaseCellTouchesAGroundedWall()
    {
        var game = new GameSession();
        for (int y = 16; y <= 23; y++) Place(game, new Cell(9, y, 8), Material.Stone);
        var source = new Cell(8, 23, 8);
        var zombie = new Zombie { Id = 999, WaveIndex = -1, Position = source.Center };
        game.Zombies.Add(zombie);

        game.DamageZombie(zombie, Balance.ZombieHp);
        game.Update(.001f);

        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        AssertBlock(game, new Cell(8, 16, 8), Material.Dirt, false, Balance.DirtBlockHp);
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Empty(game.ExportState().PendingBlockDrops!);
        var fall = Assert.Single(game.Falls.Blocks);
        Assert.Equal(new Cell(8, 16, 8), fall.Destination);
        Assert.Equal(source.Center, fall.Position);
        Assert.Equal(0, game.Inventory.DirtCount);
    }

    [Theory]
    [InlineData(CrustFace.West)]
    [InlineData(CrustFace.Bottom)]
    public void AWideThinSupportedClusterStopsTogetherWhenAnotherMemberReachesTheFloor(CrustFace face)
    {
        var game = new GameSession();
        for (int y = 16; y <= 18; y++) Place(game, new Cell(9, y, 8), Material.Stone);
        var crustSource = new Cell(8, 19, 8);
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(crustSource, 2, face, 7)] });
        Place(game, new Cell(8, 20, 8), Material.Dirt, hp: 33);
        Place(game, new Cell(9, 20, 8), Material.Stone, hp: 234);

        game.Update(.001f);

        AssertBlock(game, new Cell(8, 19, 8), Material.Dirt, false, 33);
        AssertBlock(game, new Cell(9, 19, 8), Material.Stone, false, 234);
        var crust = game.World.GetObsidian(new Cell(8, 18, 8));
        Assert.NotNull(crust);
        Assert.Equal(face, crust.Face);
        Assert.Equal(2, crust.Units);
        Assert.Equal(7, crust.Hp);
        Assert.Equal(3, game.Falls.Blocks.Count);
        Assert.Empty(game.Feedback.Hits);
        game.Update(2);
        AssertBlock(game, new Cell(8, 19, 8), Material.Dirt, false, 33);
        AssertBlock(game, new Cell(9, 19, 8), Material.Stone, false, 234);
        Assert.Equal(7, game.World.GetObsidian(new Cell(8, 18, 8))!.Hp);
    }

    [Fact]
    public void BothImpactedThinLeavesShatterOnceBeforeTheirWideLoadFinishesFalling()
    {
        var game = new GameSession();
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(new Cell(8, 19, 8), 4, CrustFace.West, 20, OppositeUnits: 2, OppositeHp: 10)]
        });
        Place(game, new Cell(8, 20, 8), Material.Dirt, hp: 33);
        Place(game, new Cell(9, 20, 8), Material.Stone, hp: 234);
        var bystander = new Zombie { Id = 900, WaveIndex = -1, Size = ZombieSize.Kilo, Position = new(8.5f, 17, 10.7f) };
        game.Zombies.Add(bystander);

        game.Update(.001f);

        Assert.Empty(game.World.ObsidianCells);
        AssertBlock(game, new Cell(8, 16, 8), Material.Dirt, false, 33);
        AssertBlock(game, new Cell(9, 16, 8), Material.Stone, false, 234);
        Assert.Equal(20, bystander.BodyHp);
        Assert.Equal(20, Assert.Single(game.Feedback.Hits).Amount);
        Assert.Equal(2, game.Falls.Blocks.Count);
        Assert.Equal(new Cell(8, 20, 8).Center,
            Assert.Single(game.Falls.Blocks, fall => fall.Destination == new Cell(8, 16, 8)).Position);
    }

    [Fact]
    public void AFallingMixedClusterPassesADisjointGroundedLeafWithoutMovingOrDamagingIt()
    {
        var game = new GameSession();
        for (int y = 16; y <= 18; y++) Place(game, new Cell(7, y, 8), Material.Stone);
        var stationary = new ObsidianCell(new Cell(8, 18, 8), 2, CrustFace.West, 7);
        var falling = new ObsidianCell(new Cell(8, 21, 8), 2, CrustFace.East, 9);
        game.Liquids.RestoreState(new LiquidState { Obsidian = [stationary, falling] });
        Place(game, new Cell(9, 21, 8), Material.Dirt, hp: 33);

        game.Update(.001f);

        Assert.Equal(stationary, game.World.GetObsidian(stationary.Cell));
        Assert.Equal(falling with { Cell = new Cell(8, 16, 8) }, game.World.GetObsidian(new Cell(8, 16, 8)));
        AssertBlock(game, new Cell(9, 16, 8), Material.Dirt, false, 33);
        Assert.Equal(2, game.Falls.Blocks.Count);
        Assert.Empty(game.Feedback.Hits);
    }

    [Fact]
    public void ALandedThinMemberKeepsItsSideAttachedFullBlockAtTheSameHeight()
    {
        var game = new GameSession();
        for (int y = 16; y <= 17; y++) Place(game, new Cell(8, y, 8), Material.Stone);
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(new Cell(8, 21, 8), 2, CrustFace.East, 9)]
        });
        Place(game, new Cell(9, 21, 8), Material.Dirt, hp: 33);

        game.Update(.001f);

        Assert.Equal(new ObsidianCell(new Cell(8, 18, 8), 2, CrustFace.East, 9), game.World.GetObsidian(new Cell(8, 18, 8)));
        AssertBlock(game, new Cell(9, 18, 8), Material.Dirt, false, 33);
        Assert.Equal(2, game.Falls.Blocks.Count);
        Assert.Empty(game.Feedback.Hits);
        game.Update(2);
        AssertBlock(game, new Cell(9, 18, 8), Material.Dirt, false, 33);
    }

    private static void Place(GameSession game, Cell cell, Material material, bool barrier = false, float? hp = null)
    {
        bool placed = barrier
            ? (bool)typeof(WorldGrid).GetMethod("AddBarrier", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(game.World, [cell, material, hp])!
            : (bool)typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(game.World, [cell, material])!;
        Assert.True(placed);
        if (!barrier && hp.HasValue)
            Assert.True((bool)typeof(WorldGrid).GetMethod("RestoreTerrainHp", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(game.World, [cell, hp.Value])!);
    }

    private static void AssertBlock(GameSession game, Cell cell, Material material, bool barrier, float hp)
    {
        Assert.Equal(barrier ? Material.Barrier : material, game.World.GetMaterial(cell));
        if (barrier)
        {
            var block = game.World.GetBarrier(cell);
            Assert.NotNull(block);
            Assert.Equal(material, block.Content);
            Assert.Equal(hp, block.Hp);
        }
        else Assert.Equal(hp, game.World.GetTerrainHp(cell));
    }
}
