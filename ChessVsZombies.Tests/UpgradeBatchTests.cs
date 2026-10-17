using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class UpgradeBatchTests
{
    private static readonly UpgradeTemplate OneHealth = Template((UpgradeKind.HP, 2));

    [Fact]
    public void TwoHundredTurretsSpendOnlyMatchingTypesAndKeepCollectionCaps()
    {
        var game = new GameSession();
        for (int x = 1; x <= 30 && game.Turrets.Count < 200; x++)
            for (int z = 1; z <= 30 && game.Turrets.Count < 200; z++)
                if (WorldGrid.IsTurretZone(new Cell(x, 16, z)))
                    Assert.True(game.Deploy(PieceKind.Pawn, new Cell(x, 16, z)));
        Assert.Equal(200, game.Turrets.Count);
        Seed(game, UpgradeKind.HP, 400, 400);
        Seed(game, UpgradeKind.ATK, 400, 400);
        var template = Template((UpgradeKind.HP, 2), (UpgradeKind.ATK, 2));
        var ids = game.Turrets.Select(tower => tower.Id);
        var quote = game.GetUpgradeBatchCosts(ids, template);
        Assert.Equal(400, quote[UpgradeKind.HP]);
        Assert.Equal(400, quote[UpgradeKind.ATK]);

        Assert.True(game.SpendUpgradeBatchNow(ids, template), game.Message);

        Assert.Equal(0, Available(game, UpgradeKind.HP));
        Assert.Equal(0, Available(game, UpgradeKind.ATK));
        Assert.Equal(40, game.Economy.GetAllocationCap(UpgradeKind.HP));
        Assert.All(game.Turrets, tower =>
        {
            Assert.Equal(430, tower.Hp);
            Assert.Equal(41, game.Economy.GetAttack(tower));
        });
    }

    [Fact]
    public void SpendNowUsesOnlyAvailableMatchingTypesAndNeverResumes()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 11, 5);
        Seed(game, UpgradeKind.ATK, 2, 2);
        var template = Template((UpgradeKind.HP, 2), (UpgradeKind.ATK, 2));
        Assert.True(game.SpendUpgradeBatchNow(game.Turrets.Select(tower => tower.Id).Reverse(), template));
        Assert.Equal(1, Available(game, UpgradeKind.HP));
        Assert.Equal(0, Available(game, UpgradeKind.ATK));
        Assert.Equal(2, game.Turrets[0].UpgradePoints[UpgradeKind.HP]);
        Assert.Equal(1, game.Turrets[0].UpgradePoints[UpgradeKind.ATK]);
        Assert.Equal(2, game.Turrets[1].UpgradePoints[UpgradeKind.HP]);
        Assert.Empty(game.Turrets[2].UpgradePoints);

        PullUntil(game, UpgradeKind.HP, 2);
        game.Update(4);
        Assert.Empty(game.Turrets[2].UpgradePoints);
        Assert.Null(game.QueuedUpgradeBatch);
    }

    [Fact]
    public void OversizedBudgetsChargeOnlyTwoPerInitialStatAndKeepTheOriginalRankCap()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 21, 21);
        int currencyBefore = game.Economy.Currency;
        var targets = game.Turrets.Take(2).Select(tower => tower.Id).ToArray();
        var template = Template((UpgradeKind.HP, 3));
        Assert.Equal(4, game.GetUpgradeBatchCosts(targets, template)[UpgradeKind.HP]);
        Assert.True(game.SpendUpgradeBatchNow(targets, template));
        Assert.Equal(17, Available(game, UpgradeKind.HP));
        Assert.Equal(currencyBefore, game.Economy.Currency);
        Assert.All(game.Turrets.Take(2), tower => Assert.Equal(3, tower.UpgradePoints[UpgradeKind.HP]));
        Assert.False(game.SpendUpgradeBatchNow(targets, template));
        Assert.Equal(17, Available(game, UpgradeKind.HP));
    }

    [Theory]
    [InlineData(1, 2, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, true)]
    [InlineData(int.MaxValue, 2, true)]
    public void InitialPurchaseRequiresTwoPointsAndNeverAppliesAPartialRank(int budget, int available, bool succeeds)
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 191, available);
        var tower = game.Turrets[0];
        var template = Template((UpgradeKind.HP, budget));

        Assert.Equal(budget >= 2 ? 2 : 0,
            game.GetUpgradeBatchCosts([tower.Id], template).GetValueOrDefault(UpgradeKind.HP));
        Assert.Equal(succeeds, game.SpendUpgradeBatchNow([tower.Id], template));

        Assert.Equal(succeeds ? 20 : 0, game.Economy.GetAllocated(tower, UpgradeKind.HP));
        Assert.Equal(succeeds ? 230 : 30, tower.Hp);
        Assert.Equal(available - (succeeds ? 2 : 0), Available(game, UpgradeKind.HP));
        Assert.Null(game.QueuedUpgradeBatch);
    }

    [Fact]
    public void HighCapBatchChargesTwoForBothNewAndExistingStats()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 191, 6);
        game.Turrets[1].UpgradePoints[UpgradeKind.HP] = 1;
        game.Turrets[1].Hp = 40;
        game.Turrets[2].UpgradePoints[UpgradeKind.HP] = 7;
        game.Turrets[2].Hp = 100;
        var template = Template((UpgradeKind.HP, int.MaxValue));
        var ids = game.Turrets.Select(tower => tower.Id).ToArray();

        Assert.Equal(6, game.GetUpgradeBatchCosts(ids, template)[UpgradeKind.HP]);
        Assert.True(game.SpendUpgradeBatchNow(ids, template), game.Message);

        Assert.Equal(0, Available(game, UpgradeKind.HP));
        Assert.Equal(20, game.Economy.GetAllocationCap(UpgradeKind.HP));
        Assert.All(game.Turrets, tower =>
        {
            Assert.Equal(20, game.Economy.GetAllocated(tower, UpgradeKind.HP));
            Assert.Equal(230, tower.Hp);
        });
    }

    [Fact]
    public void OneBankedPointCannotPartiallyCatchUpAnExistingStat()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 191, 1);
        var tower = game.Turrets[0];
        tower.UpgradePoints[UpgradeKind.HP] = 7;
        tower.Hp = 100;
        Assert.Equal(2, game.GetUpgradeBatchCosts([tower.Id], OneHealth)[UpgradeKind.HP]);

        Assert.False(game.SpendUpgradeBatchNow([tower.Id], OneHealth));
        Assert.Equal(7, game.Economy.GetAllocated(tower, UpgradeKind.HP));
        Assert.Equal(100, tower.Hp);
        Assert.Equal(1, Available(game, UpgradeKind.HP));
        Assert.True(game.QueueUpgradeBatch([tower.Id], OneHealth));
        Assert.Null(game.QueuedUpgradeBatch!.GraceRemaining);

        game.Economy.GetProgress(UpgradeKind.HP).Available = 2;
        game.Update(.05f);
        game.Update(3.1f);
        Assert.Null(game.QueuedUpgradeBatch);
        Assert.Equal(20, game.Economy.GetAllocated(tower, UpgradeKind.HP));
        Assert.Equal(230, tower.Hp);
        Assert.Equal(0, Available(game, UpgradeKind.HP));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedPurchaseKeepsItsQuotedRankAfterCapGrowthAndNeedsTwoPointsToCatchUp(bool checkpoint)
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 21, 4);
        int towerId = game.Turrets[0].Id;
        Assert.True(game.QueueUpgradeBatch([towerId], OneHealth));
        Assert.Equal(2, game.QueuedUpgradeBatch!.Cost);
        Assert.Equal(3, game.QueuedUpgradeBatch.Targets[0].Additions[UpgradeKind.HP]);

        game.Economy.GetProgress(UpgradeKind.HP).PullCount = 41;
        Assert.Equal(5, game.Economy.GetAllocationCap(UpgradeKind.HP));
        Assert.Equal(0, game.Economy.GetAllocated(game.Turrets[0], UpgradeKind.HP));
        string path = Path.Combine(Path.GetTempPath(), $"cvz-batch-{Guid.NewGuid():N}.json");
        try
        {
            if (checkpoint)
            {
                SaveStore.Save(game, path);
                Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
                Assert.NotNull(restored);
                game = restored;
            }
            game.Update(3.1f);
            var tower = game.Turrets.Single(tower => tower.Id == towerId);
            Assert.Null(game.QueuedUpgradeBatch);
            Assert.Equal(3, game.Economy.GetAllocated(tower, UpgradeKind.HP));
            Assert.Equal(60, tower.Hp);
            Assert.Equal(2, Available(game, UpgradeKind.HP));

            Assert.Equal(2, game.GetUpgradeBatchCosts([towerId], OneHealth)[UpgradeKind.HP]);
            Assert.True(game.SpendUpgradeBatchNow([towerId], OneHealth), game.Message);
            Assert.Equal(5, game.Economy.GetAllocated(tower, UpgradeKind.HP));
            Assert.Equal(80, tower.Hp);
            Assert.Equal(0, Available(game, UpgradeKind.HP));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AnotherAttributesPointsCannotPayForHealthInEitherMode()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.ATK, 11, 4);
        var ids = game.Turrets.Take(2).Select(tower => tower.Id).ToArray();
        Assert.False(game.SpendUpgradeBatchNow(ids, OneHealth));
        Assert.Equal(4, Available(game, UpgradeKind.ATK));
        Assert.All(game.Turrets, tower => Assert.Empty(tower.UpgradePoints));

        Assert.True(game.QueueUpgradeBatch(ids, OneHealth));
        Assert.Equal(4, game.QueuedUpgradeBatch!.Costs[UpgradeKind.HP]);
        Assert.Null(game.QueuedUpgradeBatch.GraceRemaining);
        game.Update(4);
        Assert.Equal(4, Available(game, UpgradeKind.ATK));
        Assert.All(game.Turrets, tower => Assert.Empty(tower.UpgradePoints));
    }

    [Fact]
    public void QueueWaitsForTheEntireTypeVectorThenCanBeCancelledWithoutCharge()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 11, 4);
        Seed(game, UpgradeKind.ATK, 11, 2);
        var template = Template((UpgradeKind.HP, 2), (UpgradeKind.ATK, 2));
        var ids = game.Turrets.Take(2).Select(tower => tower.Id).ToArray();
        Assert.True(game.QueueUpgradeBatch(ids, template));
        game.Update(4);
        Assert.Null(game.QueuedUpgradeBatch!.GraceRemaining);
        Assert.Equal(4, Available(game, UpgradeKind.HP));
        Assert.Equal(2, Available(game, UpgradeKind.ATK));

        PullUntil(game, UpgradeKind.ATK, 4);
        game.Update(.05f);
        Assert.Equal(GameSession.UpgradeBatchGraceSeconds, game.QueuedUpgradeBatch!.GraceRemaining);
        int healthBeforeCancel = Available(game, UpgradeKind.HP);
        int attackBeforeCancel = Available(game, UpgradeKind.ATK);
        game.Update(1);
        Assert.Equal(healthBeforeCancel, Available(game, UpgradeKind.HP));
        Assert.Equal(attackBeforeCancel, Available(game, UpgradeKind.ATK));
        Assert.True(game.CancelUpgradeBatch());
        Assert.False(game.CancelUpgradeBatch());
        game.Update(4);
        Assert.Equal(healthBeforeCancel, Available(game, UpgradeKind.HP));
        Assert.Equal(attackBeforeCancel, Available(game, UpgradeKind.ATK));
        Assert.All(game.Turrets, tower => Assert.Empty(tower.UpgradePoints));
    }

    [Fact]
    public void AffordableQueueCommitsEveryTypeAndTargetAfterThreeSeconds()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 11, 4);
        Seed(game, UpgradeKind.ATK, 11, 4);
        var ids = game.Turrets.Take(2).Select(tower => tower.Id).ToArray();
        var template = Template((UpgradeKind.HP, 2), (UpgradeKind.ATK, 2));
        Assert.True(game.QueueUpgradeBatch(ids, template));
        Assert.False(game.QueueUpgradeBatch(ids, template));
        game.Update(2.5f);
        Assert.Equal(4, Available(game, UpgradeKind.HP));
        Assert.Equal(4, Available(game, UpgradeKind.ATK));
        Assert.All(game.Turrets, tower => Assert.Empty(tower.UpgradePoints));
        game.Update(.6f);
        Assert.Null(game.QueuedUpgradeBatch);
        Assert.Equal(0, Available(game, UpgradeKind.HP));
        Assert.Equal(0, Available(game, UpgradeKind.ATK));
        Assert.All(game.Turrets.Take(2), tower =>
        {
            Assert.Equal(50, tower.Hp);
            Assert.Equal(3, game.Economy.GetAttack(tower));
        });
        Assert.Empty(game.Turrets[2].UpgradePoints);
    }

    [Fact]
    public void LosingOneRequiredTypeOrChangingATargetCancelsWithoutPartialCharge()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 11, 4);
        Seed(game, UpgradeKind.ATK, 11, 3);
        var ids = game.Turrets.Take(2).Select(tower => tower.Id).ToArray();
        Assert.True(game.QueueUpgradeBatch(ids, OneHealth));
        Assert.True(game.SpendUpgradeBatchNow([game.Turrets[0].Id], OneHealth));
        Assert.Null(game.QueuedUpgradeBatch);
        Assert.Equal(2, Available(game, UpgradeKind.HP));
        Assert.Empty(game.Turrets[1].UpgradePoints);

        var both = Template((UpgradeKind.HP, 2), (UpgradeKind.ATK, 2));
        Assert.True(game.QueueUpgradeBatch(game.Turrets.Skip(1).Select(tower => tower.Id), both));
        Assert.Null(game.QueuedUpgradeBatch!.GraceRemaining); // HP 2 < batch HP 4.
        Assert.True(game.CancelUpgradeBatch());
        Assert.True(game.QueueUpgradeBatch([game.Turrets[1].Id], both));
        Assert.True(game.SpendUpgradeBatchNow([game.Turrets[2].Id], Template((UpgradeKind.ATK, 2))));
        game.Update(.05f);
        Assert.Null(game.QueuedUpgradeBatch);
        Assert.Equal(2, Available(game, UpgradeKind.HP));
        Assert.Equal(1, Available(game, UpgradeKind.ATK));
        Assert.Empty(game.Turrets[1].UpgradePoints);

        Assert.True(game.QueueUpgradeBatch([game.Turrets[1].Id], OneHealth));
        Assert.True(game.Remove(game.Turrets[1].Cell));
        game.Update(.05f);
        Assert.Null(game.QueuedUpgradeBatch);
        Assert.Equal(2, Available(game, UpgradeKind.HP));
    }

    [Fact]
    public void QueueSnapshotsTheTemplateWithoutChangingTheReusableTemplate()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 11, 4);
        Seed(game, UpgradeKind.ATK, 11, 2);
        var template = Template((UpgradeKind.HP, 2));
        Assert.True(game.QueueUpgradeBatch([game.Turrets[0].Id], template));
        template.Points[UpgradeKind.HP] = 1;
        template.Points[UpgradeKind.ATK] = 2;
        game.Update(3.1f);
        Assert.Equal(2, Available(game, UpgradeKind.HP));
        Assert.Equal(2, Available(game, UpgradeKind.ATK));
        Assert.Equal(2, game.Turrets[0].UpgradePoints[UpgradeKind.HP]);
        Assert.False(game.Turrets[0].UpgradePoints.ContainsKey(UpgradeKind.ATK));
        Assert.Equal(1, template.Points[UpgradeKind.HP]);
    }

    [Fact]
    public void TypedQueueAndGracePeriodSurvivePreparationCheckpoint()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 11, 4);
        Seed(game, UpgradeKind.ATK, 11, 2);
        var template = Template((UpgradeKind.HP, 2), (UpgradeKind.ATK, 2));
        Assert.True(game.QueueUpgradeBatch(game.Turrets.Take(2).Select(tower => tower.Id), template));
        string path = Path.Combine(Path.GetTempPath(), $"cvz-batch-{Guid.NewGuid():N}.json");
        try
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Equal(4, restored.QueuedUpgradeBatch!.Costs[UpgradeKind.HP]);
            Assert.Equal(4, restored.QueuedUpgradeBatch.Costs[UpgradeKind.ATK]);
            PullUntil(restored, UpgradeKind.ATK, 4);
            int healthBeforeCommit = Available(restored, UpgradeKind.HP);
            int attackBeforeCommit = Available(restored, UpgradeKind.ATK);
            restored.Update(.05f);
            restored.Update(3.1f);
            Assert.Equal(healthBeforeCommit - 4, Available(restored, UpgradeKind.HP));
            Assert.Equal(attackBeforeCommit - 4, Available(restored, UpgradeKind.ATK));
            Assert.All(restored.Turrets.Take(2), tower => Assert.Equal(50, tower.Hp));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AffordableQueueResumesItsRemainingGraceAfterCheckpointLoad()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 11, 4);
        Assert.True(game.QueueUpgradeBatch([game.Turrets[0].Id], OneHealth));
        game.Update(1);
        string path = Path.Combine(Path.GetTempPath(), $"cvz-batch-{Guid.NewGuid():N}.json");
        try
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.InRange(restored.QueuedUpgradeBatch!.GraceRemaining!.Value, 1.9f, 2.1f);
            restored.Update(1.5f);
            Assert.Equal(4, Available(restored, UpgradeKind.HP));
            restored.Update(.6f);
            Assert.Null(restored.QueuedUpgradeBatch);
            Assert.Equal(2, Available(restored, UpgradeKind.HP));
            Assert.Equal(50, restored.Turrets[0].Hp);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExportedQueueIsIndependentOfTheLiveQueue()
    {
        var game = ThreeTowers();
        Seed(game, UpgradeKind.HP, 11, 2);
        Assert.True(game.QueueUpgradeBatch([game.Turrets[0].Id], OneHealth));
        var exported = game.ExportState().QueuedUpgradeBatch!;
        exported.Costs[UpgradeKind.HP] = 99;
        exported.Targets[0].Additions[UpgradeKind.HP] = 99;
        exported.Targets.Clear();
        Assert.Single(game.QueuedUpgradeBatch!.Targets);
        Assert.Equal(2, game.QueuedUpgradeBatch.Costs[UpgradeKind.HP]);
        Assert.Equal(2, game.QueuedUpgradeBatch.Targets[0].Additions[UpgradeKind.HP]);
    }

    private static GameSession ThreeTowers()
    {
        var game = new GameSession();
        for (int x = 8; x < 11; x++) Assert.True(game.Deploy(PieceKind.Pawn, new Cell(x, 16, 8)));
        return game;
    }

    private static void Seed(GameSession game, UpgradeKind kind, int pulls, int available)
    {
        var progress = game.Economy.GetProgress(kind);
        progress.PullCount = pulls;
        progress.Available = available;
    }

    private static int Available(GameSession game, UpgradeKind kind) => game.Economy.GetProgress(kind).Available;

    private static void PullUntil(GameSession game, UpgradeKind kind, int available)
    {
        for (int i = 0; i < 100 && Available(game, kind) < available; i++)
            Assert.NotNull(game.Economy.Pull());
        Assert.True(Available(game, kind) >= available);
    }

    private static UpgradeTemplate Template(params (UpgradeKind Kind, int Count)[] points)
    {
        var template = new UpgradeTemplate();
        foreach (var (kind, count) in points) template.Points[kind] = count;
        return template;
    }
}
