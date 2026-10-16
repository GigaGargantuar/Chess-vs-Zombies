using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class TowerBatchSelectionTests
{
    [Fact]
    public void ReversedCornersIncludeEveryAxisBoundaryButExcludeOtherLayers()
    {
        Turret[] towers =
        [
            Tower(1, new(3, 4, 5)), Tower(2, new(7, 8, 9)), Tower(3, new(5, 6, 7)),
            Tower(4, new(2, 6, 7)), Tower(5, new(8, 6, 7)), Tower(6, new(5, 3, 7)),
            Tower(7, new(5, 9, 7)), Tower(8, new(5, 6, 4)), Tower(9, new(5, 6, 10)),
            Tower(10, new(3, 8, 9)), Tower(11, new(7, 4, 5))
        ];
        var selection = new TowerBatchSelection();

        Assert.True(selection.SelectCorner(new Cell(7, 8, 9), towers));
        Assert.True(selection.IsChoosingSecondCorner);
        Assert.Empty(selection.SelectedIds);
        Assert.True(selection.SelectCorner(new Cell(3, 4, 5), towers));

        Assert.False(selection.IsChoosingSecondCorner);
        Assert.Equal(new Cell(7, 8, 9), selection.FirstCorner);
        Assert.Equal(new Cell(3, 4, 5), selection.SecondCorner);
        Assert.Equal(new[] { 1, 2, 3, 10, 11 }, selection.GetTargets(towers).Select(tower => tower.Id));
    }

    [Fact]
    public void NewBoxesAndIndividualAddsAccumulateWithoutDuplicatingTowers()
    {
        Turret[] towers = [Tower(1, new(1, 2, 3)), Tower(2, new(4, 5, 6)), Tower(3, new(7, 8, 9))];
        var selection = new TowerBatchSelection();
        Assert.True(selection.Add(towers[2]));
        Assert.False(selection.Add(towers[2]));
        Assert.True(selection.SelectCorner(towers[0].Cell, towers));
        Assert.True(selection.SelectCorner(towers[0].Cell, towers));
        Assert.True(selection.SelectCorner(towers[1].Cell, towers));
        Assert.Null(selection.SecondCorner);
        Assert.True(selection.IsChoosingSecondCorner);
        Assert.Equal(new[] { 1, 3 }, selection.GetTargets(towers).Select(tower => tower.Id));
        Assert.True(selection.SelectCorner(towers[2].Cell, towers));
        Assert.Equal(new[] { 1, 2, 3 }, selection.GetTargets(towers).Select(tower => tower.Id));
    }

    [Fact]
    public void DeselectRemovesOnlyTheAimedTowerAndClearResetsTheBox()
    {
        Turret[] towers = [Tower(1, new(1, 2, 3)), Tower(2, new(4, 5, 6))];
        var selection = new TowerBatchSelection();
        selection.SelectCorner(towers[0].Cell, towers);
        selection.SelectCorner(towers[1].Cell, towers);

        Assert.True(selection.Remove(towers[0]));
        Assert.False(selection.Remove(towers[0].Id));
        Assert.Equal(towers[1], Assert.Single(selection.GetTargets(towers)));
        Assert.False(selection.IsChoosingSecondCorner);
        selection.Clear();
        Assert.Empty(selection.SelectedIds);
        Assert.Null(selection.FirstCorner);
        Assert.Null(selection.SecondCorner);
        Assert.False(selection.IsChoosingSecondCorner);
        Assert.False(selection.Remove(towers[1]));
    }

    [Theory]
    [InlineData(-1, 4, 4)]
    [InlineData(4, -1, 4)]
    [InlineData(4, 4, -1)]
    [InlineData(32, 4, 4)]
    [InlineData(4, 32, 4)]
    [InlineData(4, 4, 32)]
    public void OutsideCornersCannotStartOrCompleteThePendingBox(int x, int y, int z)
    {
        var tower = Tower(1, new(4, 4, 4));
        var selection = new TowerBatchSelection();
        var invalid = new Cell(x, y, z);
        Assert.False(selection.SelectCorner(invalid, [tower]));
        Assert.Null(selection.FirstCorner);
        selection.SelectCorner(tower.Cell, [tower]);
        Assert.False(selection.SelectCorner(invalid, [tower]));
        Assert.True(selection.IsChoosingSecondCorner);
        Assert.Null(selection.SecondCorner);
        Assert.Empty(selection.SelectedIds);
        Assert.True(selection.SelectCorner(tower.Cell, [tower]));
        Assert.Equal(tower, Assert.Single(selection.GetTargets([tower])));
    }

    [Fact]
    public void DeadInvalidAndOutsideTowersCannotJoinTheSelection()
    {
        Turret[] towers =
        [
            Tower(1, new(5, 5, 5)), Tower(2, new(5, 5, 5), 0), Tower(3, new(5, 5, 5), -1),
            Tower(4, new(5, 5, 5), float.NaN), Tower(5, new(5, 5, 5), float.PositiveInfinity),
            Tower(0, new(5, 5, 5)), Tower(6, new(-1, 5, 5))
        ];
        var selection = new TowerBatchSelection();
        Assert.All(towers.Skip(1), tower => Assert.False(selection.Add(tower)));
        selection.SelectCorner(new Cell(0, 0, 0), towers);
        selection.SelectCorner(new Cell(31, 31, 31), towers);
        Assert.Equal(towers[0], Assert.Single(selection.GetTargets(towers)));
    }

    [Fact]
    public void IdentityFollowsFallingTowerAndNeverSelectsItsReplacement()
    {
        var falling = Tower(3, new(8, 20, 8));
        var dead = Tower(2, new(9, 20, 8));
        var removed = Tower(1, new(10, 20, 8));
        var selection = new TowerBatchSelection();
        selection.SelectCorner(falling.Cell, [falling, dead, removed]);
        selection.SelectCorner(removed.Cell, [falling, dead, removed]);
        var replacement = Tower(4, falling.Cell);
        falling.Cell = new Cell(8, 16, 8);
        dead.Hp = 0;
        Turret[] livingCollection = [replacement, dead, falling];

        Assert.Equal(falling, Assert.Single(selection.GetTargets(livingCollection)));
        selection.Prune(livingCollection);
        Assert.Equal(falling.Id, Assert.Single(selection.SelectedIds));
        Assert.DoesNotContain(replacement.Id, selection.SelectedIds);
    }

    [Fact]
    public void UpgradeSpendingChargesAndUpgradesOnlyTheFinalSelectedTowers()
    {
        var game = ThreeTowers();
        var selection = SelectThreeExceptMiddle(game);
        var health = game.Economy.GetProgress(UpgradeKind.HP);
        health.PullCount = 11;
        health.Available = 5;
        var template = new UpgradeTemplate { Points = { [UpgradeKind.HP] = 2 } };
        var ids = selection.GetTargets(game.Turrets).Select(tower => tower.Id);

        Assert.Equal(4, game.GetUpgradeBatchCosts(ids, template)[UpgradeKind.HP]);
        Assert.True(game.SpendUpgradeBatchNow(ids, template), game.Message);

        Assert.Equal(1, health.Available);
        Assert.Equal(50, game.Turrets[0].Hp);
        Assert.Equal(30, game.Turrets[1].Hp);
        Assert.Empty(game.Turrets[1].UpgradePoints);
        Assert.Equal(50, game.Turrets[2].Hp);
    }

    [Fact]
    public void QueuedUpgradesKeepTheChosenIdentitiesAfterSelectionChanges()
    {
        var game = ThreeTowers();
        var selection = SelectThreeExceptMiddle(game);
        var health = game.Economy.GetProgress(UpgradeKind.HP);
        health.PullCount = 11;
        health.Available = 2;
        var template = new UpgradeTemplate { Points = { [UpgradeKind.HP] = 1 } };
        Assert.True(game.QueueUpgradeBatch(selection.SelectedIds, template), game.Message);

        selection.Clear();
        selection.Add(game.Turrets[1]);
        game.Update(GameSession.UpgradeBatchGraceSeconds + .1f);

        Assert.Null(game.QueuedUpgradeBatch);
        Assert.Equal(0, health.Available);
        Assert.Equal(40, game.Turrets[0].Hp);
        Assert.Equal(30, game.Turrets[1].Hp);
        Assert.Equal(40, game.Turrets[2].Hp);
    }

    private static Turret Tower(int id, Cell cell, float hp = Balance.TurretHp) =>
        new() { Id = id, Kind = PieceKind.Pawn, Cell = cell, Hp = hp };

    private static GameSession ThreeTowers()
    {
        var game = new GameSession();
        for (int x = 8; x <= 10; x++) Assert.True(game.Deploy(PieceKind.Pawn, new Cell(x, 16, 8)));
        return game;
    }

    private static TowerBatchSelection SelectThreeExceptMiddle(GameSession game)
    {
        var selection = new TowerBatchSelection();
        selection.SelectCorner(game.Turrets[0].Cell, game.Turrets);
        selection.SelectCorner(game.Turrets[2].Cell, game.Turrets);
        Assert.True(selection.Remove(game.Turrets[1]));
        return selection;
    }
}
