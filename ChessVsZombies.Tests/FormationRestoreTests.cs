using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class FormationRestoreTests
{
    [Fact]
    public void SavedFormationIsIndependentOfOriginalTowersAndRetainedWhenAnEmptySaveIsRejected()
    {
        var game = ConfiguredKnight();
        var original = Assert.Single(game.Turrets);
        Assert.True(game.SaveFormation());
        var snapshot = Assert.Single(game.SavedFormation!.Towers);
        original.UpgradePoints.Clear();
        original.JumpPattern.Clear();
        original.Cell = new Cell(9, 16, 8);

        Assert.Equal(new Cell(8, 16, 8), snapshot.Cell);
        Assert.Equal(2, snapshot.UpgradePoints[UpgradeKind.HP]);
        Assert.Equal(new Cell(3, 1, 0), Assert.Single(snapshot.JumpPattern));
        Assert.Single(game.MissingFormationTurrets);
        original.Hp = 0;
        Assert.False(game.SaveFormation());
        Assert.Same(snapshot, Assert.Single(game.SavedFormation.Towers));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<UpgradeKind, int>)snapshot.UpgradePoints).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<Cell>)snapshot.JumpPattern).Clear());
    }

    [Fact]
    public void RestoreConstructsOneTowerPerIntervalAndUpdateAndBuildsStacksBottomUp()
    {
        var game = new GameSession();
        Cell lower = new(8, 16, 8), upper = new(8, 17, 8), side = new(10, 16, 8);
        Assert.True(game.Deploy(PieceKind.Pawn, lower));
        Assert.True(game.Deploy(PieceKind.Rook, upper));
        Assert.True(game.Deploy(PieceKind.Bishop, side));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(upper));
        Assert.True(game.Remove(lower));
        Assert.True(game.Remove(side));
        int before = game.Economy.Currency;
        long total = game.MissingFormationCurrencyCost;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.2f);
        Assert.Empty(game.Turrets);
        game.Update(.2f);
        Assert.Equal(lower, Assert.Single(game.Turrets).Cell);
        Assert.Equal(before - Balance.PieceCost(PieceKind.Pawn), game.Economy.Currency);
        game.Update(5f);
        Assert.Equal(2, game.Turrets.Count);
        Assert.Null(game.TrySelectTurret(upper));
        game.Update(.36f);
        Assert.Equal(PieceKind.Rook, game.TrySelectTurret(upper)!.Kind);
        Assert.Equal(3, game.Turrets.Count);
        Assert.Equal(before - total, game.Economy.Currency);
        Assert.Empty(game.MissingFormationTurrets);
        Assert.False(game.IsAutoRestoringFormation);
        Assert.Contains("restored", game.FormationRestoreStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullSavedConfigurationMustBeAffordableBeforeAnyCurrencyOrComponentsAreSpent()
    {
        var game = ConfiguredKnight();
        var cell = Assert.Single(game.Turrets).Cell;
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(cell));
        foreach (var kind in Enum.GetValues<UpgradeKind>()) game.Economy.GetProgress(kind).Available = 0;
        game.Economy.GetProgress(UpgradeKind.HP).Available = 1;
        int before = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);
        Assert.Empty(game.Turrets);
        Assert.True(game.IsAutoRestoringFormation);
        Assert.Contains("component", game.FormationRestoreStatus);
        Assert.Equal(before, game.Economy.Currency);
        Assert.Equal(1, game.Economy.GetProgress(UpgradeKind.HP).Available);
        Assert.Equal(2, game.MissingFormationUpgradeCosts[UpgradeKind.HP]);
        game.Economy.GetProgress(UpgradeKind.HP).Available = 2;
        game.Economy.GetProgress(UpgradeKind.ATK).Available = 1;
        game.Economy.GetProgress(UpgradeKind.Jump).Available = 3;
        game.Update(.36f);

        var restored = Assert.Single(game.Turrets);
        Assert.Equal(2, restored.UpgradePoints[UpgradeKind.HP]);
        Assert.Equal(1, restored.UpgradePoints[UpgradeKind.ATK]);
        Assert.Equal(3, restored.UpgradePoints[UpgradeKind.Jump]);
        Assert.Equal(new Cell(3, 1, 0), Assert.Single(restored.JumpPattern));
        Assert.Equal(game.Economy.GetMaxHp(restored), restored.Hp);
        Assert.Equal(before - Balance.PieceCost(PieceKind.Knight), game.Economy.Currency);
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.HP).Available);
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.ATK).Available);
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.False(game.IsAutoRestoringFormation);
    }

    [Fact]
    public void InsufficientCurrencyWaitsAndResumesAfterBudgetArrives()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        Assert.True(game.Deploy(PieceKind.Rook, cell));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(cell));
        Assert.True(game.Economy.TrySpend(game.Economy.Currency));
        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);
        Assert.Empty(game.Turrets);
        Assert.Equal(0, game.Economy.Currency);
        Assert.Contains("currency", game.FormationRestoreStatus);

        game.Economy.Credit(Balance.PieceCost(PieceKind.Rook));
        game.Update(.36f);
        Assert.Equal(PieceKind.Rook, Assert.Single(game.Turrets).Kind);
        Assert.Equal(0, game.Economy.Currency);
    }

    [Fact]
    public void WrongKindOccupancyBlocksOnlyItsOwnSlotAndNeverOverwritesTheLivingTower()
    {
        var game = new GameSession();
        Cell blocked = new(8, 16, 8), open = new(9, 16, 8);
        Assert.True(game.Deploy(PieceKind.Rook, blocked));
        Assert.True(game.Deploy(PieceKind.Pawn, open));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(blocked));
        Assert.True(game.Remove(open));
        Assert.True(game.Deploy(PieceKind.Bishop, blocked));
        var blocker = Assert.Single(game.Turrets);
        blocker.Hp = 7;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);
        Assert.Equal(2, game.Turrets.Count);
        Assert.Same(blocker, game.TrySelectTurret(blocked));
        Assert.Equal(7, blocker.Hp);
        Assert.Equal(PieceKind.Pawn, game.TrySelectTurret(open)!.Kind);
        game.Update(.36f);
        Assert.Contains("occupied", game.FormationRestoreStatus);
        Assert.True(game.IsAutoRestoringFormation);
        Assert.True(game.CancelAutoRestoreFormation());
        Assert.False(game.IsAutoRestoringFormation);
        Assert.Single(game.MissingFormationTurrets);
        Assert.True(game.Remove(blocked));
        game.Update(1f);
        Assert.Null(game.TrySelectTurret(blocked));
    }

    [Fact]
    public void RestoreWaitsForCameraClearanceWithoutChargingForTheBlockedTower()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        Assert.True(game.Deploy(PieceKind.Rook, cell));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(cell));
        var camera = new FlyCamera(cell.Center);
        int before = game.Economy.Currency;
        Assert.True(game.StartAutoRestoreFormation(location => !camera.Intersects(location)));

        game.Update(.36f);

        Assert.Empty(game.Turrets);
        Assert.Equal(before, game.Economy.Currency);
        Assert.Contains("Move away", game.FormationRestoreStatus);
        camera.Move(System.Numerics.Vector3.UnitY, 2, _ => false);
        game.Update(.36f);
        Assert.Equal(cell, Assert.Single(game.Turrets).Cell);
        Assert.Equal(before - Balance.PieceCost(PieceKind.Rook), game.Economy.Currency);
        Assert.False(game.IsAutoRestoringFormation);
    }

    [Fact]
    public void ExistingSameKindTowerIsRetainedWithItsDamageAndCurrentUpgrades()
    {
        var game = ConfiguredKnight();
        Assert.True(game.SaveFormation());
        var turret = Assert.Single(game.Turrets);
        turret.Hp = 3;
        turret.UpgradePoints.Clear();
        Assert.Empty(game.MissingFormationTurrets);
        Assert.False(game.StartAutoRestoreFormation());
        Assert.Equal(3, turret.Hp);
        Assert.Empty(turret.UpgradePoints);
    }

    [Fact]
    public void RestoreWorksDuringCombatAndStopsWhenTheCoreIsDefeated()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        Assert.True(game.Deploy(PieceKind.Pawn, cell));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(cell));
        Assert.True(game.StartLevel());
        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);
        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.Equal(cell, Assert.Single(game.Turrets).Cell);

        Assert.True(game.Remove(cell));
        Assert.True(game.StartAutoRestoreFormation());
        game.Zombies.Clear();
        game.Zombies.Add(new Zombie { Id = 90000, Position = new Cell(14, 16, 15).Center, Attack = Balance.CoreHp });
        game.Update(.01f);
        Assert.Equal(GamePhase.Defeat, game.Phase);
        Assert.False(game.IsAutoRestoringFormation);
        Assert.NotNull(game.SavedFormation);
        Assert.False(game.StartAutoRestoreFormation());
    }

    private static GameSession ConfiguredKnight()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(8, 16, 8)));
        var turret = Assert.Single(game.Turrets);
        foreach (var kind in new[] { UpgradeKind.HP, UpgradeKind.ATK, UpgradeKind.Jump })
        {
            game.Economy.GetProgress(kind).PullCount = 21;
            game.Economy.GetProgress(kind).Available = 3;
            Assert.True(game.Economy.Apply(turret, kind));
        }
        Assert.True(game.Economy.Apply(turret, UpgradeKind.HP));
        Assert.True(game.Economy.TrySetJumpPattern(turret, [new Cell(3, 1, 0)], out _));
        turret.Hp -= 11;
        return game;
    }
}
