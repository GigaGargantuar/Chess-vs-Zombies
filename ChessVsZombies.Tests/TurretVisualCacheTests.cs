using ChessVsZombies.Core;
using ChessVsZombies.Rendering;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class TurretVisualCacheTests
{
    [Fact]
    public void CombatValuesAndFurtherPointsDoNotRebuildUnchangedGeometry()
    {
        var game = TowerGame();
        var turret = game.Turrets[0];
        var cache = new TurretVisualCache();
        Assert.True(Refresh(cache, game));
        Assert.False(Refresh(cache, game));

        turret.Hp -= 5;
        turret.Cooldown = .2f;
        turret.UpgradePoints[UpgradeKind.HP] = 0;
        Assert.False(Refresh(cache, game));
        turret.UpgradePoints[UpgradeKind.HP] = 1;
        Assert.True(Refresh(cache, game));
        turret.UpgradePoints[UpgradeKind.HP] = 2;
        turret.UpgradePoints[UpgradeKind.ATK] = 1;
        Assert.False(Refresh(cache, game));
        turret.UpgradePoints.Clear();
        Assert.True(Refresh(cache, game));
        Assert.False(Refresh(cache, game));
    }

    [Fact]
    public void SelectionAndRelocationInvalidateTheCache()
    {
        var game = TowerGame();
        var turret = game.Turrets[0];
        var cache = new TurretVisualCache();
        Assert.True(Refresh(cache, game));
        Assert.True(Refresh(cache, game, turret));
        Assert.False(Refresh(cache, game, turret));
        Assert.True(Refresh(cache, game));
        turret.Cell = new Cell(9, 16, 8);
        Assert.True(Refresh(cache, game));
        Assert.False(Refresh(cache, game));
    }

    [Fact]
    public void MembershipOrderAndReplacementInvalidateTheCache()
    {
        var game = TowerGame();
        var cache = new TurretVisualCache();
        Refresh(cache, game);
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(9, 16, 8)));
        Assert.True(Refresh(cache, game));
        game.Turrets.Reverse();
        Assert.True(Refresh(cache, game));
        var previous = game.Turrets[0];
        game.Turrets[0] = new Turret { Id = previous.Id, Kind = PieceKind.Bishop, Cell = previous.Cell };
        Assert.True(Refresh(cache, game));
        game.Turrets.RemoveAt(0);
        Assert.True(Refresh(cache, game));
        Assert.False(Refresh(cache, game));
    }

    [Fact]
    public void ChangingWorldsInvalidatesEvenWhenTheTurretListIsReused()
    {
        var game = TowerGame();
        var cache = new TurretVisualCache();
        Assert.True(Refresh(cache, game));
        Assert.False(Refresh(cache, game));
        var world = new GameSession().World;
        Assert.True(cache.Refresh(game.Turrets, game.Falls, null, world));
        Assert.False(cache.Refresh(game.Turrets, game.Falls, null, world));
    }

    [Fact]
    public void FallingPositionsInvalidateContinuouslyWithoutARevisionChange()
    {
        var game = TowerGame();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 17, 8)));
        var cache = new TurretVisualCache();
        Refresh(cache, game);
        Assert.True(game.Remove(new Cell(8, 16, 8)));
        Assert.True(Refresh(cache, game));
        Assert.False(Refresh(cache, game));
        int revision = game.Falls.Revision;
        game.Update(.25f);
        Assert.Equal(revision, game.Falls.Revision);
        Assert.True(Refresh(cache, game));
        game.Update(.25f);
        Assert.Equal(revision, game.Falls.Revision);
        Assert.True(Refresh(cache, game));
        game.Update(.5f);
        Assert.True(Refresh(cache, game));
        Assert.False(Refresh(cache, game));
    }

    [Fact]
    public void EmptyListsInvalidateOnceAndAfterTheFinalTurretIsRemoved()
    {
        var game = new GameSession();
        var cache = new TurretVisualCache();
        Assert.True(Refresh(cache, game));
        Assert.False(Refresh(cache, game));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
        Assert.True(Refresh(cache, game));
        game.Turrets.Clear();
        Assert.True(Refresh(cache, game));
        Assert.False(Refresh(cache, game));
    }

    private static GameSession TowerGame()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
        return game;
    }

    private static bool Refresh(TurretVisualCache cache, GameSession game, Turret? selected = null) =>
        cache.Refresh(game.Turrets, game.Falls, selected, game.World);
}
