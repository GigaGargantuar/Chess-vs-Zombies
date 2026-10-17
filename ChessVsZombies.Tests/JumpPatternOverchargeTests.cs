using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class JumpPatternOverchargeTests
{
    [Theory]
    [InlineData(10, 10, 10)]
    [InlineData(int.MaxValue, 10, 10)]
    [InlineData(71, 71, 71)]
    public void OversizedJumpRequestBuysTheCurrentCapWithTwoPoints(int requested, int available, int expectedLevel)
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        Seed(economy, UpgradeKind.Jump, available);
        economy.GetProgress(UpgradeKind.Jump).PullCount = Math.Max(91, (available - 1) * 10 + 1);
        var template = PatternTemplate(requested, new Cell(2, 0, 0));

        Assert.True(economy.TryApplyTemplate(knight, template, out var error), error);

        Assert.Equal(expectedLevel, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(available - 2, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(new Cell(2, 0, 0), Assert.Single(knight.JumpPattern));
        Assert.Equal(requested, template.Points[UpgradeKind.Jump]);
    }

    [Fact]
    public void ReapplyingTheSameDestinationsInAnotherOrderNeverSpendsAgain()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        Seed(economy, UpgradeKind.Jump, 10);
        var template = PatternTemplate(10, new Cell(2, 0, 0), new Cell(-2, 1, 0));
        Assert.True(economy.TryApplyTemplate(knight, template, out var error), error);

        template.JumpPattern.Reverse();
        for (int attempt = 0; attempt < 3; attempt++)
            Assert.True(economy.TryApplyTemplate(knight, template, out error), error);

        Assert.Equal(10, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(8, economy.GetProgress(UpgradeKind.Jump).Available);
    }

    [Fact]
    public void UnchangedLegacyPatternDoesNotChargeForAnOversizedPointRequest()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        knight.JumpPattern.Clear();
        knight.JumpPattern.Add(new Cell(2, 0, 0));
        Seed(economy, UpgradeKind.Jump, 10);

        Assert.True(economy.TryApplyTemplate(knight, PatternTemplate(10, new Cell(2, 0, 0)), out var error), error);

        Assert.Equal(0, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(10, economy.GetProgress(UpgradeKind.Jump).Available);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    public void ChangedFormationWithinPurchasedJumpLevelsCostsNothing(int initialRadius, int nextRadius)
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        Seed(economy, UpgradeKind.Jump, 10);
        Assert.True(economy.TrySetJumpPattern(knight, [new Cell(initialRadius, 0, 0)], out var error), error);
        int before = economy.GetProgress(UpgradeKind.Jump).Available;

        Assert.True(economy.TryApplyTemplate(knight, PatternTemplate(10, new Cell(-nextRadius, 0, 0)), out error), error);

        Assert.Equal(10, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(before, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(new Cell(-nextRadius, 0, 0), Assert.Single(knight.JumpPattern));
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 10)]
    [InlineData(false, 71)]
    [InlineData(true, 71)]
    public void OneSelectedTowerBatchQuotesAndSpendsTwoWithAnOversizedTemplate(bool queued, int requested)
    {
        var game = ThreeKnights();
        var selected = game.Turrets[1];
        var originalPattern = selected.JumpPattern.ToArray();
        Seed(game.Economy, UpgradeKind.Jump, requested);
        game.Economy.GetProgress(UpgradeKind.Jump).PullCount = Math.Max(91, (requested - 1) * 10 + 1);
        var template = PatternTemplate(requested, new Cell(2, 0, 0));
        int[] ids = [selected.Id];

        Assert.Equal(2, game.GetUpgradeBatchCosts(ids, template)[UpgradeKind.Jump]);
        CommitBatch(game, ids, template, queued, 2);

        Assert.Equal(Math.Max(10, requested), game.Economy.GetAllocated(selected, UpgradeKind.Jump));
        Assert.Equal(requested - 2, game.Economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(originalPattern, selected.JumpPattern);
        Assert.Empty(game.Turrets[0].UpgradePoints);
        Assert.Empty(game.Turrets[2].UpgradePoints);
        Assert.False(game.GetUpgradeBatchCosts(ids, template).ContainsKey(UpgradeKind.Jump));
        Assert.False(queued ? game.QueueUpgradeBatch(ids, template) : game.SpendUpgradeBatchNow(ids, template));
        Assert.Equal(requested - 2, game.Economy.GetProgress(UpgradeKind.Jump).Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiplePreviouslyUpgradedTowersEachCatchUpToCurrentJumpCapForTwoPoints(bool queued)
    {
        var game = ThreeKnights();
        var first = game.Turrets[0];
        var second = game.Turrets[1];
        first.UpgradePoints[UpgradeKind.Jump] = 1;
        second.UpgradePoints[UpgradeKind.Jump] = 2;
        Seed(game.Economy, UpgradeKind.Jump, 10);
        var template = PatternTemplate(10, new Cell(3, 0, 0));
        int[] ids = [second.Id, first.Id];

        Assert.Equal(4, game.GetUpgradeBatchCosts(ids, template)[UpgradeKind.Jump]);
        CommitBatch(game, ids, template, queued, 4);

        Assert.Equal(10, game.Economy.GetAllocated(first, UpgradeKind.Jump));
        Assert.Equal(10, game.Economy.GetAllocated(second, UpgradeKind.Jump));
        Assert.Equal(6, game.Economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Empty(game.Turrets[2].UpgradePoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchDoesNotBuyJumpPointsForAnUnchangedPattern(bool queued)
    {
        var game = ThreeKnights();
        var selected = game.Turrets[0];
        Seed(game.Economy, UpgradeKind.Jump, 10);
        Seed(game.Economy, UpgradeKind.HP, 2);
        var template = PatternTemplate(10, selected.JumpPattern.ToArray());
        template.Points[UpgradeKind.HP] = 2;
        int[] ids = [selected.Id];

        var quote = game.GetUpgradeBatchCosts(ids, template);
        Assert.False(quote.ContainsKey(UpgradeKind.Jump));
        Assert.Equal(2, quote[UpgradeKind.HP]);
        CommitBatch(game, ids, template, queued, 2);

        Assert.Equal(10, game.Economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(0, game.Economy.GetAllocated(selected, UpgradeKind.Jump));
        Assert.Equal(10, game.Economy.GetAllocated(selected, UpgradeKind.HP));
    }

    [Fact]
    public void FormationAndOtherRequestedStatsEachUseTheirOwnTwoPointCost()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight, Hp = 17 };
        Seed(economy, UpgradeKind.Jump, 10);
        Seed(economy, UpgradeKind.HP, 4);
        Seed(economy, UpgradeKind.ATK, 4);
        var template = PatternTemplate(10, new Cell(2, 0, 0));
        template.Points[UpgradeKind.HP] = 2;
        template.Points[UpgradeKind.ATK] = 3;

        Assert.True(economy.TryApplyTemplate(knight, template, out var error), error);

        Assert.Equal(8, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(2, economy.GetProgress(UpgradeKind.HP).Available);
        Assert.Equal(2, economy.GetProgress(UpgradeKind.ATK).Available);
        Assert.Equal(117, knight.Hp);
        Assert.Equal(11, economy.GetAttack(knight));
    }

    [Fact]
    public void WithPatternDisabledJumpStillBuysTheCurrentCapOnlyOnce()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        Seed(economy, UpgradeKind.Jump, 10);
        var template = PatternTemplate(4, new Cell(2, 0, 0));
        template.UseJumpPattern = false;
        var before = knight.JumpPattern.ToArray();

        Assert.True(economy.TryApplyTemplate(knight, template, out var error), error);
        Assert.False(economy.TryApplyTemplate(knight, template, out error));

        Assert.Equal(10, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(8, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(before, knight.JumpPattern);
    }

    [Fact]
    public void InvalidPatternStillAllowsExplicitMaxStatPurchasesWithoutChangingDestinations()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        Seed(economy, UpgradeKind.Jump, 10);
        Seed(economy, UpgradeKind.HP, 2);
        var before = knight.JumpPattern.ToArray();
        var template = PatternTemplate(3, new Cell(1, 0, 0));
        template.Points[UpgradeKind.HP] = 2;

        Assert.True(economy.TryApplyTemplate(knight, template, out var error), error);

        Assert.Equal(10, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(8, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(10, economy.GetAllocated(knight, UpgradeKind.HP));
        Assert.Equal(before, knight.JumpPattern);
        Assert.Contains("Jump pattern skipped", economy.LastMessage);
    }

    [Fact]
    public void UnaffordablePatternNeverPartiallyBuysJumpWhileOtherStatsCanApply()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        Seed(economy, UpgradeKind.Jump, 1);
        Seed(economy, UpgradeKind.ATK, 2);
        var before = knight.JumpPattern.ToArray();
        var template = PatternTemplate(10, new Cell(2, 0, 0));
        template.Points[UpgradeKind.ATK] = 2;

        Assert.True(economy.TryApplyTemplate(knight, template, out var error), error);

        Assert.Equal(0, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(1, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(10, economy.GetAllocated(knight, UpgradeKind.ATK));
        Assert.Equal(before, knight.JumpPattern);
        Assert.Contains("Jump pattern skipped", economy.LastMessage);
    }

    [Theory]
    [InlineData(PieceKind.Pawn)]
    [InlineData(PieceKind.Rook)]
    [InlineData(PieceKind.Bishop)]
    public void MatchingLatentPatternBuysTheCurrentCapForTwoPointsBeforeActivatingJump(PieceKind kind)
    {
        var economy = new Economy();
        var tower = new Turret { Kind = kind };
        var latentPattern = tower.JumpPattern.ToArray();
        Assert.Equal(8, latentPattern.Length);
        Assert.Equal(2, JumpRules.FormationRadius(latentPattern));
        Assert.False(economy.HasJump(tower));
        Seed(economy, UpgradeKind.Jump, 71);
        economy.GetProgress(UpgradeKind.Jump).PullCount = 701;
        var template = PatternTemplate(71, latentPattern);

        Assert.True(economy.TryApplyTemplate(tower, template, out var error), error);

        Assert.True(economy.HasJump(tower));
        Assert.Equal(71, economy.GetAllocated(tower, UpgradeKind.Jump));
        Assert.Equal(69, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(latentPattern, tower.JumpPattern);
        Assert.True(economy.TryApplyTemplate(tower, template, out error), error);
        Assert.Equal(69, economy.GetProgress(UpgradeKind.Jump).Available);
    }

    [Theory]
    [InlineData(PieceKind.Pawn)]
    [InlineData(PieceKind.Rook)]
    [InlineData(PieceKind.Bishop)]
    public void PatternOnlyApplicationBuysCurrentCapToActivateLatentJump(PieceKind kind)
    {
        var economy = new Economy();
        var tower = new Turret { Kind = kind };
        var latentPattern = tower.JumpPattern.ToArray();
        Assert.False(economy.HasJump(tower));
        Seed(economy, UpgradeKind.Jump, 71);
        economy.GetProgress(UpgradeKind.Jump).PullCount = 701;

        Assert.True(economy.TrySetJumpPattern(tower, latentPattern, out var error), error);

        Assert.True(economy.HasJump(tower));
        Assert.Equal(71, economy.GetAllocated(tower, UpgradeKind.Jump));
        Assert.Equal(69, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(latentPattern, tower.JumpPattern);
    }

    [Theory]
    [InlineData(PieceKind.Pawn, false)]
    [InlineData(PieceKind.Pawn, true)]
    [InlineData(PieceKind.Rook, false)]
    [InlineData(PieceKind.Rook, true)]
    [InlineData(PieceKind.Bishop, false)]
    [InlineData(PieceKind.Bishop, true)]
    public void BatchBuysCurrentCapToActivateLatentJumpWithoutDrainingJumpBank(PieceKind kind, bool queued)
    {
        var game = new GameSession();
        Assert.True(game.Deploy(kind, new Cell(8, 16, 8)));
        var tower = Assert.Single(game.Turrets);
        var latentPattern = tower.JumpPattern.ToArray();
        Assert.False(game.Economy.HasJump(tower));
        Seed(game.Economy, UpgradeKind.Jump, 71);
        game.Economy.GetProgress(UpgradeKind.Jump).PullCount = 701;
        var template = PatternTemplate(71, latentPattern);
        int[] ids = [tower.Id];

        Assert.Equal(2, game.GetUpgradeBatchCosts(ids, template)[UpgradeKind.Jump]);
        CommitBatch(game, ids, template, queued, 2);

        Assert.True(game.Economy.HasJump(tower));
        Assert.Equal(71, game.Economy.GetAllocated(tower, UpgradeKind.Jump));
        Assert.Equal(69, game.Economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(latentPattern, tower.JumpPattern);
        Assert.False(game.GetUpgradeBatchCosts(ids, template).ContainsKey(UpgradeKind.Jump));
    }

    private static void CommitBatch(GameSession game, int[] ids, UpgradeTemplate template, bool queued, long expectedCost)
    {
        if (!queued)
        {
            Assert.True(game.SpendUpgradeBatchNow(ids, template), game.Message);
            return;
        }

        int jumpBefore = game.Economy.GetProgress(UpgradeKind.Jump).Available;
        Assert.True(game.QueueUpgradeBatch(ids, template), game.Message);
        Assert.Equal(expectedCost, game.QueuedUpgradeBatch!.Cost);
        Assert.Equal(GameSession.UpgradeBatchGraceSeconds, game.QueuedUpgradeBatch.GraceRemaining);
        game.Update(GameSession.UpgradeBatchGraceSeconds - .1f);
        Assert.Equal(jumpBefore, game.Economy.GetProgress(UpgradeKind.Jump).Available);
        game.Update(.2f);
        Assert.Null(game.QueuedUpgradeBatch);
    }

    private static GameSession ThreeKnights()
    {
        var game = new GameSession();
        for (int x = 8; x < 11; x++) Assert.True(game.Deploy(PieceKind.Knight, new Cell(x, 16, 8)));
        return game;
    }

    private static UpgradeTemplate PatternTemplate(int requested, params Cell[] offsets) => new()
    {
        Points = new() { [UpgradeKind.Jump] = requested },
        UseJumpPattern = true,
        JumpPattern = offsets.ToList()
    };

    private static void Seed(Economy economy, UpgradeKind kind, int available)
    {
        economy.GetProgress(kind).PullCount = 91;
        economy.GetProgress(kind).Available = available;
    }
}
