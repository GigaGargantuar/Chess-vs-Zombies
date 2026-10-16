using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class BuoyantRouteCacheTests
{
    // Query the decision routine directly so each assertion holds position fixed;
    // public integration coverage separately checks the resulting physical movement.
    private static Vector3? Query(GameSession game, Zombie zombie) =>
        (Vector3?)typeof(GameSession).GetMethod("FindBuoyantRouteStep", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, [zombie]);

    [Fact]
    public void AWaitingCrowdSharesAPathDecisionWithoutRepeatingTheSearch()
    {
        var game = Pool();
        var position = new Vector3(8.5f, 17.3f, 8.5f);
        Vector3? expected = null;
        for (int i = 0; i < 100; i++)
        {
            var result = Query(game, new Zombie { Id = i, Position = position });
            if (i == 0) expected = result;
            Assert.Equal(expected, result);
        }
        Assert.NotNull(expected);
        Assert.Equal(1, game.BuoyantRouteSearchCount);
    }

    [Fact]
    public void FailedRoutesAreCachedAndNewTerrainInvalidatesThem()
    {
        var game = new GameSession();
        var zombie = new Zombie { Position = new Cell(8, 14, 8).Center };
        Assert.Null(Query(game, zombie));
        Assert.Null(Query(game, zombie));
        Assert.Equal(1, game.BuoyantRouteSearchCount);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Dig(new Cell(8,14,8)));
        Assert.True(game.Dig(new Cell(9,14,8)));
        Assert.NotNull(Query(game, zombie));
        Assert.Equal(2, game.BuoyantRouteSearchCount);
    }

    [Fact]
    public void LiquidGeometryAndArmorBuoyancyArePartOfTheDecisionIdentity()
    {
        var game = Pool();
        var zombie = new Zombie { Position = new Vector3(8.5f,17.3f,8.5f), Kind = ZombieKind.Armored, Tier = GearTier.Osmium };
        Query(game, zombie);
        Query(game, zombie);
        Assert.Equal(1, game.BuoyantRouteSearchCount);
        game.DamageZombie(zombie, 420, DamageKind.Pierce, ShotKind.Pellet, -zombie.Facing);
        game.DamageZombie(zombie, 210, DamageKind.Blunt, ShotKind.Lob, -Vector3.UnitY);
        game.DamageZombie(zombie, 210, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitY);
        Assert.Empty(zombie.ArmorPieces);
        Query(game, zombie);
        Assert.Equal(2, game.BuoyantRouteSearchCount);
        Assert.True(game.Liquids.TryAdd(new Cell(8,16,8), LiquidKind.Mercury, 1));
        Query(game, zombie);
        Assert.Equal(3, game.BuoyantRouteSearchCount);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(9,16,8)));
        Query(game, zombie);
        Assert.Equal(4, game.BuoyantRouteSearchCount);
        game.Reset();
        Assert.Equal(0, game.BuoyantRouteSearchCount);
    }

    [Fact]
    public void FlowTimeAloneDoesNotChangeGeometryRevision()
    {
        var game = new GameSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Dig(new Cell(8,15,8)));
        Assert.True(game.Liquids.TryAdd(new Cell(8,15,8), LiquidKind.Water, 10));
        long revision = game.Liquids.Revision;
        game.Update(.5f);
        Assert.Equal(revision, game.Liquids.Revision);
        Assert.Equal(10, game.Liquids.GetUnits(new Cell(8,15,8), LiquidKind.Water));
    }

    private static GameSession Pool()
    {
        var game = new GameSession();
        game.Liquids.RestoreState(new LiquidState {
            Cells = [new(new Cell(8,16,8),0,7,0,2,CrustFace.Bottom)],
            Obsidian = [new(new Cell(8,16,8),2,CrustFace.Bottom,10)] });
        return game;
    }
}
