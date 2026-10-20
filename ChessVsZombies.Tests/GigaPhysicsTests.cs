using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GigaPhysicsTests
{
    [Fact]
    public void WalkingMovesTheFullCubeAtOneFifthOfNormalBasicSpeed()
    {
        var game = Battle();
        var giga = Add(game, new Cell(4, 16, 4));
        giga.NextCell = new Cell(5, 16, 4);
        var origin = giga.Position;

        game.Update(.25f);

        Assert.Equal(origin.X + .255f * .25f, giga.Position.X, 5);
        Assert.Equal(origin.Y, giga.Position.Y);
        Assert.Equal(origin.Z, giga.Position.Z);
    }

    [Fact]
    public void HeadHeightObstacleBlocksTheActualFourBlockVolume()
    {
        var game = Battle();
        var giga = Add(game, new Cell(4, 16, 4));
        game.Turrets.Add(new Turret { Id = 101, Cell = new Cell(8, 19, 5) });
        typeof(GameSession).GetMethod("RebuildNavigation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, []);

        Assert.True(BodyClear(game, giga.Position, giga));
        Assert.False(BodyClear(game, giga.Position + new Vector3(.1f, 0, 0), giga));
    }

    [Fact]
    public void NearbyTurretDoesNotSuspendAnUnsupportedGigaInMidair()
    {
        var game = Battle();
        var giga = Add(game, new Cell(4, 22, 4));
        game.Turrets.Add(new Turret { Id = 101, Kind = PieceKind.Pawn, Cell = new Cell(11, 16, 6), Cooldown = 100 });
        float height = giga.Position.Y;

        game.Update(.25f);

        Assert.True(giga.Position.Y < height);
        Assert.Empty(game.GigaProjectiles);
    }

    private static bool BodyClear(GameSession game, Vector3 position, Zombie giga) =>
        (bool)typeof(GameSession).GetMethod("IsBodyClear", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, [position, giga])!;

    private static GameSession Battle()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new WaveDefinition { Groups = [new ZombieGroup()] }] });
        var game = new GameSession(levels: levels);
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        return game;
    }

    private static Zombie Add(GameSession game, Cell cell)
    {
        var giga = new Zombie { Id = 100, Size = ZombieSize.Giga, WaveIndex = 0 };
        giga.Position = giga.StandAt(cell);
        game.Zombies.Add(giga);
        return giga;
    }
}
