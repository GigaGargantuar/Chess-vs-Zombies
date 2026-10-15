using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class SizedSpawnCacheTests
{
    [Fact]
    public void KiloSpawnsShareCandidatesUntilTerrainOrTowerGeometryChanges()
    {
        var game = new GameSession();
        var shape = new Zombie { Size = ZombieSize.Kilo };
        var candidates = typeof(GameSession).GetMethod("FindSizedSpawnCells", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Func<Zombie, List<Cell>>>(game);
        var cell = new Cell(0, 16, 8);
        var first = candidates(shape);
        Assert.Contains(cell, first);
        Assert.Same(first, candidates(shape));

        // The tower lies in the second column of this Kilo's occupied volume.
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(1, 16, 8)));
        Assert.True(game.StartLevel()); // Refresh the live collision index before spawning.
        var blocked = candidates(shape);
        Assert.NotSame(first, blocked);
        Assert.DoesNotContain(cell, blocked);
        Assert.Same(blocked, candidates(shape));
    }

    [Fact]
    public void RemovingTheFourFloorCellsInvalidatesTheCachedSpawnAndResetRestoresIt()
    {
        var game = new GameSession();
        var shape = new Zombie { Size = ZombieSize.Kilo };
        var candidates = typeof(GameSession).GetMethod("FindSizedSpawnCells", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Func<Zombie, List<Cell>>>(game);
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        var cell = new Cell(0, 16, 8);
        var first = candidates(shape);
        for (int x = 0; x < 2; x++)
            for (int z = 8; z < 10; z++) Assert.True(dig(new Cell(x, 15, z)));
        var unsupported = candidates(shape);
        Assert.NotSame(first, unsupported);
        Assert.DoesNotContain(cell, unsupported);

        game.Reset();

        var restored = candidates(shape);
        Assert.NotSame(unsupported, restored);
        Assert.Contains(cell, restored);
    }
}
