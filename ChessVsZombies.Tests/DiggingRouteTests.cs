using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class DiggingRouteTests
{
    [Theory]
    [InlineData(100, 3, 169.575f)]
    [InlineData(100, 35, 11.475f)]
    [InlineData(500, 35, 72.675f)]
    public void DiggingCostIncludesAttackWaitsAndStationaryExposure(float hp, int attack, float expected)
    {
        var cost = NavigationCosts.Breach(hp, attack);
        Assert.Equal(expected, cost, 3);
        Assert.True(cost > hp * .1f, "Intact dirt and stone should receive more caution than the old durability-only cost.");
        Assert.True(float.IsFinite(cost));
    }

    [Fact]
    public void CurrentDurabilityAndSeparateHeadroomMatterWithoutMakingOneHitObstaclesForbidden()
    {
        Assert.Equal(0, NavigationCosts.Breach(0, 35));
        var oneHit = NavigationCosts.Breach(35, 35);
        Assert.Equal(1.275f, oneHit, 3);
        Assert.Equal(oneHit, NavigationCosts.Breach(1, 35));
        Assert.True(oneHit < 2); // A one-hit piece can still beat a two-tile detour.
        Assert.True(NavigationCosts.Breach(36, 35) > oneHit);
        Assert.True(NavigationCosts.Breach(100, 35) > NavigationCosts.Breach(65, 35));
        // Two physical obstacles require two separate initial strikes, even if their
        // combined durability could otherwise fit inside one 35-ATK hit.
        Assert.True(NavigationCosts.Breach(10, 35) + NavigationCosts.Breach(10, 35) > NavigationCosts.Breach(20, 35));
    }

    [Fact]
    public void ALongerOpenTunnelWinsOverTheShortSingleDirtBreach()
    {
        var game = Battle(GearTier.Wood);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        var open = new HashSet<Cell>();
        // Both routes end beneath the core. The open U-shaped tunnel adds twelve
        // walking tiles; the direct route has one untouched 100-HP dirt block.
        for (int z = 9; z <= 15; z++)
        {
            open.Add(new Cell(8, 14, z));
            open.Add(new Cell(10, 14, z));
        }
        for (int x = 8; x <= 10; x++) open.Add(new Cell(x, 14, 9));
        for (int x = 10; x <= 15; x++) open.Add(new Cell(x, 14, 15));
        foreach (var cell in open) Assert.True(game.Dig(cell));
        var shortcut = new Cell(9, 14, 15);
        var zombie = BeginAt(game, new Cell(8, 14, 15));
        bool reachedFarEndOfDetour = false;

        for (int step = 0; step < 1800 && game.CoreHp == Balance.CoreHp; step++)
        {
            game.Update(.02f);
            reachedFarEndOfDetour |= zombie.Cell.Z == 9;
            Assert.True(zombie.Hp > 0);
        }

        Assert.True(reachedFarEndOfDetour, "The zombie should accept the longer walking route instead of digging a shortcut.");
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(shortcut));
        Assert.Equal(Balance.DirtBlockHp, game.World.GetTerrainHp(shortcut));
        Assert.Empty(game.World.DamagedTerrain);
    }

    [Fact]
    public void ACompletelyEnclosedZombieStillDigsAndReachesTheCore()
    {
        var game = Battle(GearTier.Osmium);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        var start = new Cell(12, 14, 15);
        Assert.True(game.Dig(start));
        int dirtBefore = game.Inventory.DirtCount;
        int revisionBefore = game.World.Revision;
        var zombie = BeginAt(game, start);

        game.Update(.01f);
        Assert.NotEmpty(game.World.DamagedTerrain);
        for (int step = 0; step < 2250 && game.CoreHp == Balance.CoreHp; step++)
        {
            game.Update(.02f);
            Assert.True(zombie.Hp > 0);
        }

        Assert.True(game.World.Revision > revisionBefore);
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Equal(dirtBefore, game.Inventory.DirtCount);
    }

    private static GameSession Battle(GearTier tier)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Kind = ZombieKind.Armed, Tier = tier }] }] });
        return new GameSession(12345, levels);
    }

    private static Zombie BeginAt(GameSession game, Cell cell)
    {
        Assert.True(game.StartLevel());
        game.Update(.001f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = cell.Center;
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        zombie.Cooldown = 0;
        zombie.Lane = 0;
        return zombie;
    }
}
