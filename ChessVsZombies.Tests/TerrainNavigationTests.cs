using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class TerrainNavigationTests
{
    [Theory]
    [InlineData(14, Material.Dirt, 100)]
    [InlineData(5, Material.Stone, 500)]
    public void NaturalTerrainTakesTimedZombieHitsUntilItsMaterialHpIsExhausted(int height, Material material, int hp)
    {
        var game = NewSession();
        var start = new Cell(10, height, 10);
        Assert.True(game.Dig(start));
        int dirtBefore = game.Inventory.DirtCount;
        int stoneBefore = game.Inventory.StoneCount;
        int currencyBefore = game.Economy.Currency;
        var zombie = StartAt(game, start);

        game.Update(.01f);

        var damaged = Assert.Single(game.World.DamagedTerrain);
        var target = damaged.Key;
        Assert.Equal(material, game.World.GetMaterial(target));
        Assert.Equal(hp - zombie.Attack, damaged.Value);
        Assert.Equal(hp - zombie.Attack, game.World.GetTerrainHp(target));
        int revisionAfterFirstHit = game.World.Revision;
        game.Update(.5f);
        Assert.Equal(hp - zombie.Attack, game.World.GetTerrainHp(target));
        Assert.Equal(revisionAfterFirstHit, game.World.Revision);
        game.Update(.51f);
        Assert.Equal(hp - 2 * zombie.Attack, game.World.GetTerrainHp(target));

        int hitsRequired = (hp + zombie.Attack - 1) / zombie.Attack;
        for (int hit = 3; hit < hitsRequired; hit++)
        {
            game.Update(1.01f);
            Assert.Equal(material, game.World.GetMaterial(target));
            Assert.Equal(hp - hit * zombie.Attack, game.World.GetTerrainHp(target));
        }
        game.Update(1.01f);

        Assert.Equal(Material.Air, game.World.GetMaterial(target));
        Assert.Equal(0, game.World.GetTerrainHp(target));
        Assert.DoesNotContain(game.World.DamagedTerrain, pair => pair.Key == target);
        Assert.True(game.World.Revision > revisionAfterFirstHit);
        Assert.Equal(dirtBefore, game.Inventory.DirtCount);
        Assert.Equal(stoneBefore, game.Inventory.StoneCount);
        Assert.Equal(currencyBefore, game.Economy.Currency);
    }

    [Fact]
    public void ATrappedUndergroundZombieBreachesNaturalTerrainAndReachesTheObjective()
    {
        var game = NewSession();
        var start = new Cell(12, 14, 15);
        Assert.True(game.Dig(start));
        var zombie = StartAt(game, start);
        int revisionBefore = game.World.Revision;
        int dirtBefore = game.Inventory.DirtCount;
        int stoneBefore = game.Inventory.StoneCount;

        AdvanceUntilCoreHit(game, zombie, 90);

        Assert.True(game.World.Revision > revisionBefore);
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Equal(dirtBefore, game.Inventory.DirtCount);
        Assert.Equal(stoneBefore, game.Inventory.StoneCount);
    }

    [Fact]
    public void ZombiesDescendIntoAOneBlockTrenchAndClimbOutWithoutCuttingThroughItsCorners()
    {
        var game = NewSession();
        // A full-width trench prevents an equally short route around the hole.
        for (int z = 0; z < WorldGrid.Size; z++) Assert.True(game.Dig(new Cell(13, 15, z)));
        // A supported ceiling prevents jumping across, preserving the climb/drop case.
        Assert.True(game.Inventory.Add(Material.Dirt, 9));
        Assert.True(game.BuildBarrier(new Cell(12, 16, 12)));
        for (int z = 12; z <= 19; z++) Assert.True(game.BuildBarrier(new Cell(12, 17, z)));
        var zombie = StartAt(game, new Cell(12, 16, 15));
        float lowest = zombie.Position.Y;

        AdvanceUntilCoreHit(game, zombie, 15, () =>
        {
            lowest = Math.Min(lowest, zombie.Position.Y);
            Assert.InRange(zombie.Position.Z, 15.499f, 15.501f);
            // Descend only after crossing above the hole; climb before crossing its far rim.
            if (zombie.Position.Y < 16.499f)
                Assert.InRange(zombie.Position.X, 13.499f, 13.501f);
        });

        Assert.InRange(lowest, 15.499f, 15.501f);
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Empty(game.World.DamagedTerrain);
    }

    [Fact]
    public void ZombiesClimbOverAOneBlockObstacleBeforeDescendingOnTheOtherSide()
    {
        var game = NewSession();
        var obstacle = new Cell(13, 16, 15);
        Assert.True(game.Inventory.Add(Material.Dirt, 8));
        for (int z = 12; z <= 19; z++) Assert.True(game.BuildBarrier(new Cell(13, 16, z)));
        var zombie = StartAt(game, new Cell(12, 16, 15));
        float highest = zombie.Position.Y;

        AdvanceUntilCoreHit(game, zombie, 15, () =>
        {
            highest = Math.Max(highest, zombie.Position.Y);
            Assert.InRange(zombie.Position.Z, 15.499f, 15.501f);
            // The complete climb precedes crossing the block's side; the descent follows it.
            if (zombie.Position.Y < 17.499f)
                Assert.True(zombie.Position.X <= 12.501f || zombie.Position.X >= 14.499f,
                    $"Zombie clipped an obstacle corner at {zombie.Position}.");
        });

        Assert.InRange(highest, 17.499f, 17.501f);
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Equal(100, game.World.GetBarrier(obstacle)!.Hp);
        Assert.Empty(game.World.DamagedTerrain);
    }

    [Fact]
    public void OpenSurfaceRoutesDoNotNeedlesslyDamageNaturalTerrain()
    {
        var game = NewSession();
        var zombie = StartAt(game, new Cell(10, 16, 15));

        AdvanceUntilCoreHit(game, zombie, 15);

        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Empty(game.World.DamagedTerrain);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(new Cell(11, 15, 15)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStepWaitsUntilTheNaturalBlockInItsRequiredHeadroomIsDestroyed(bool climb)
    {
        var game = NewSession();
        var start = new Cell(12, 14, 15);
        var headroom = climb ? new Cell(12, 15, 15) : new Cell(13, 14, 15);
        Assert.True(game.Dig(start));
        if (climb)
        {
            Assert.True(game.Dig(new Cell(13, 15, 15)));
            Assert.True(game.Dig(new Cell(14, 15, 15)));
        }
        else
        {
            Assert.True(game.Dig(new Cell(13, 13, 15)));
            Assert.True(game.Dig(new Cell(14, 13, 15)));
            Assert.True(game.Dig(new Cell(14, 14, 15)));
            Assert.True(game.Dig(new Cell(15, 14, 15)));
        }
        var zombie = StartAt(game, start);

        game.Update(.01f);
        Assert.Equal(65, game.World.GetTerrainHp(headroom));
        Assert.Equal(start.Center, zombie.Position);
        Assert.Null(zombie.NextCell);
        game.Update(.5f);
        Assert.Equal(65, game.World.GetTerrainHp(headroom));
        Assert.Equal(start.Center, zombie.Position);

        AdvanceUntilCoreHit(game, zombie, 40);

        Assert.Equal(Material.Air, game.World.GetMaterial(headroom));
        Assert.True(game.CoreHp < Balance.CoreHp);
    }

    [Fact]
    public void TwoZombiesAccumulateDamageAgainstTheSameNaturalBlock()
    {
        var game = NewSession(2);
        var start = new Cell(12, 14, 15);
        var headroom = new Cell(12, 15, 15);
        Assert.True(game.Dig(start));
        Assert.True(game.Dig(new Cell(13, 15, 15)));
        Assert.True(game.Dig(new Cell(14, 15, 15)));
        int dirtBefore = game.Inventory.DirtCount;
        Assert.True(game.StartLevel(), game.Message);
        game.Update(.03f);
        Assert.Equal(2, game.Zombies.Count);
        foreach (var zombie in game.Zombies)
        {
            zombie.Position = start.Center;
            zombie.NextCell = null;
            zombie.Cooldown = 0;
            zombie.Lane = 0;
        }

        game.Update(.01f);
        Assert.Equal(30, game.World.GetTerrainHp(headroom));
        Assert.Equal(Material.Dirt, game.World.GetMaterial(headroom));
        game.Update(.5f);
        Assert.Equal(30, game.World.GetTerrainHp(headroom));
        game.Update(.51f);

        Assert.Equal(Material.Air, game.World.GetMaterial(headroom));
        Assert.Equal(dirtBefore, game.Inventory.DirtCount);
        Assert.All(game.Zombies, zombie => Assert.True(zombie.Hp > 0));
    }

    [Fact]
    public void UndamagedNaturalBlocksHaveTheSameHpAsTheirFortificationMaterial()
    {
        var world = new WorldGrid();
        Assert.Equal(Balance.BlockHp(Material.Dirt), world.GetTerrainHp(new Cell(8, 15, 8)));
        Assert.Equal(Balance.BlockHp(Material.Stone), world.GetTerrainHp(new Cell(8, 11, 8)));
        Assert.Equal(0, world.GetTerrainHp(new Cell(8, 16, 8)));
        Assert.Equal(0, world.GetTerrainHp(new Cell(15, 15, 15)));
        Assert.Equal(0, world.GetTerrainHp(new Cell(-1, 11, 8)));
        Assert.Empty(world.DamagedTerrain);
    }

    private static GameSession NewSession(int count = 1)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Name = "Terrain traversal",
            Waves = [new() { SpawnInterval = .01f, Groups = [new() { Count = count, Kind = ZombieKind.Armed, Tier = GearTier.Osmium }] }]
        });
        var game = new GameSession(12345, levels);
        game.Economy.Credit(100);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        return game;
    }

    private static Zombie StartAt(GameSession game, Cell start)
    {
        Assert.True(game.StartLevel(), game.Message);
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = start.Center;
        zombie.NextCell = null;
        zombie.Cooldown = 0;
        zombie.Lane = 0;
        return zombie;
    }

    private static void AdvanceUntilCoreHit(GameSession game, Zombie zombie, float timeout, Action? inspect = null)
    {
        for (float elapsed = 0; elapsed < timeout && game.CoreHp == Balance.CoreHp; elapsed += .02f)
        {
            game.Update(.02f);
            Assert.Equal(Material.Air, game.World.GetMaterial(zombie.Cell));
            Assert.True(zombie.Hp > 0);
            inspect?.Invoke();
        }
    }
}
