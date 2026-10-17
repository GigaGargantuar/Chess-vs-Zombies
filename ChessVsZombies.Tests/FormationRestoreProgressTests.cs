using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class FormationRestoreProgressTests
{
    [Fact]
    public void MissingComponentsSkipOnlyTheirTowerAndResumeWithTheFullSavedConfiguration()
    {
        var game = new GameSession();
        Cell upgradedCell = new(8, 16, 8), affordableCell = new(10, 16, 8);
        Assert.True(game.Deploy(PieceKind.Rook, upgradedCell));
        var upgraded = Assert.Single(game.Turrets);
        Allocate(game, upgraded, UpgradeKind.HP, 2);
        Allocate(game, upgraded, UpgradeKind.ATK, 1);
        Assert.True(game.Deploy(PieceKind.Pawn, affordableCell));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(upgradedCell));
        Assert.True(game.Remove(affordableCell));
        game.Economy.GetProgress(UpgradeKind.HP).Available = 1;
        game.Economy.GetProgress(UpgradeKind.ATK).Available = 1;
        int before = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Equal(affordableCell, Assert.Single(game.Turrets).Cell);
        Assert.Null(game.TrySelectTurret(upgradedCell));
        Assert.Equal(before - Balance.PieceCost(PieceKind.Pawn), game.Economy.Currency);
        Assert.Equal(1, game.Economy.GetProgress(UpgradeKind.HP).Available);
        Assert.Equal(1, game.Economy.GetProgress(UpgradeKind.ATK).Available);
        Assert.Equal(2, game.MissingFormationUpgradeCosts[UpgradeKind.HP]);
        Assert.Equal(2, game.MissingFormationUpgradeCosts[UpgradeKind.ATK]);
        Assert.True(game.IsAutoRestoringFormation);

        game.Economy.GetProgress(UpgradeKind.HP).Available = 2;
        game.Economy.GetProgress(UpgradeKind.ATK).Available = 2;
        game.Update(.36f);

        var restored = game.TrySelectTurret(upgradedCell)!;
        Assert.NotNull(restored);
        Assert.Equal(2, restored.UpgradePoints[UpgradeKind.HP]);
        Assert.Equal(1, restored.UpgradePoints[UpgradeKind.ATK]);
        Assert.Equal(game.Economy.GetMaxHp(restored), restored.Hp);
        Assert.Equal(before - Balance.PieceCost(PieceKind.Pawn) - Balance.PieceCost(PieceKind.Rook), game.Economy.Currency);
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.HP).Available);
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.ATK).Available);
        Assert.Empty(game.MissingFormationTurrets);
        Assert.False(game.IsAutoRestoringFormation);
    }

    [Fact]
    public void LowerCollectionCapSkipsOnlyItsTowerUntilTheCapIncreases()
    {
        var game = new GameSession();
        Cell upgradedCell = new(8, 16, 8), affordableCell = new(10, 16, 8);
        Assert.True(game.Deploy(PieceKind.Rook, upgradedCell));
        Allocate(game, Assert.Single(game.Turrets), UpgradeKind.HP, 2);
        Assert.True(game.Deploy(PieceKind.Pawn, affordableCell));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(upgradedCell));
        Assert.True(game.Remove(affordableCell));
        var hp = game.Economy.GetProgress(UpgradeKind.HP);
        hp.PullCount = 2;
        hp.Available = 2;
        int before = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Equal(affordableCell, Assert.Single(game.Turrets).Cell);
        Assert.Null(game.TrySelectTurret(upgradedCell));
        Assert.Equal(before - Balance.PieceCost(PieceKind.Pawn), game.Economy.Currency);
        Assert.Equal(2, hp.Available);
        Assert.True(game.IsAutoRestoringFormation);
        game.Update(.36f);
        Assert.Contains("cap", game.FormationRestoreStatus);
        Assert.Single(game.Turrets);

        hp.PullCount = 11;
        game.Update(.36f);

        Assert.Equal(2, game.TrySelectTurret(upgradedCell)!.UpgradePoints[UpgradeKind.HP]);
        Assert.Equal(0, hp.Available);
        Assert.Empty(game.MissingFormationTurrets);
        Assert.False(game.IsAutoRestoringFormation);
    }

    [Fact]
    public void ExpensiveTowerDoesNotPreventACheaperTowerFromUsingTheRemainingCurrency()
    {
        var game = new GameSession();
        Cell expensiveCell = new(8, 16, 8), cheapCell = new(10, 16, 8);
        Assert.True(game.Deploy(PieceKind.Rook, expensiveCell));
        Assert.True(game.Deploy(PieceKind.Pawn, cheapCell));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(expensiveCell));
        Assert.True(game.Remove(cheapCell));
        Assert.True(game.Economy.TrySpend(game.Economy.Currency - Balance.PieceCost(PieceKind.Pawn)));

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Equal(cheapCell, Assert.Single(game.Turrets).Cell);
        Assert.Null(game.TrySelectTurret(expensiveCell));
        Assert.Equal(0, game.Economy.Currency);
        Assert.True(game.IsAutoRestoringFormation);

        game.Economy.Credit(Balance.PieceCost(PieceKind.Rook));
        game.Update(.36f);

        Assert.Equal(PieceKind.Rook, game.TrySelectTurret(expensiveCell)!.Kind);
        Assert.Equal(0, game.Economy.Currency);
        Assert.Empty(game.MissingFormationTurrets);
        Assert.False(game.IsAutoRestoringFormation);
    }

    [Fact]
    public void UnaffordableLowerTowerCanTemporarilySupportTheAffordableUpperTowerWithDirt()
    {
        var game = new GameSession();
        Cell lowerCell = new(8, 16, 8), upperCell = new(8, 17, 8);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Inventory.Add(Material.Dirt, 5));
        Assert.True(game.Deploy(PieceKind.Pawn, lowerCell));
        Allocate(game, Assert.Single(game.Turrets), UpgradeKind.HP, 2);
        Assert.True(game.Deploy(PieceKind.Rook, upperCell));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(upperCell));
        Assert.True(game.Remove(lowerCell));
        var hp = game.Economy.GetProgress(UpgradeKind.HP);
        hp.Available = 1;
        int before = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        for (int step = 0; step < 40 && game.TrySelectTurret(upperCell) == null; step++) game.Update(.36f);

        var upper = game.TrySelectTurret(upperCell);
        Assert.NotNull(upper);
        Assert.Null(game.TrySelectTurret(lowerCell));
        Assert.Equal(Material.Dirt, game.World.GetBarrier(lowerCell)!.Content);
        Assert.Equal(before - Balance.PieceCost(PieceKind.Rook), game.Economy.Currency);
        Assert.Equal(1, hp.Available);
        Assert.True(game.IsAutoRestoringFormation);

        hp.Available = 2;
        for (int step = 0; step < 40 && game.IsAutoRestoringFormation; step++) game.Update(.36f);

        Assert.Same(upper, game.TrySelectTurret(upperCell));
        Assert.Equal(upperCell, upper.Cell);
        Assert.Equal(Balance.TurretHp, upper.Hp);
        Assert.Equal(PieceKind.Pawn, game.TrySelectTurret(lowerCell)!.Kind);
        Assert.Equal(2, game.TrySelectTurret(lowerCell)!.UpgradePoints[UpgradeKind.HP]);
        Assert.Null(game.World.GetBarrier(lowerCell));
        Assert.Equal(0, hp.Available);
        Assert.Empty(game.MissingFormationTurrets);
        Assert.False(game.IsAutoRestoringFormation);
    }

    private static void Allocate(GameSession game, Turret tower, UpgradeKind kind, int points)
    {
        var progress = game.Economy.GetProgress(kind);
        progress.PullCount = Math.Max(2, 1 + (points - 1) * Balance.DuplicatesPerRank);
        progress.Available = 2;
        Assert.True(game.Economy.Apply(tower, kind));
    }
}
