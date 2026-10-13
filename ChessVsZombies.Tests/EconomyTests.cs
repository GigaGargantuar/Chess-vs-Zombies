using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class EconomyTests
{
    [Fact]
    public void PurchasesAndPullsCannotOverdrawTheSharedWallet()
    {
        var economy = new Economy(7);
        Assert.Equal(1000, economy.Currency);
        Assert.False(economy.TrySpend(-1));
        Assert.True(economy.TrySpend(995));
        economy.Pull();
        Assert.Equal(0, economy.Currency);
        Assert.False(economy.TrySpend(1));
        var totalPulls = economy.Progress.Values.Sum(progress => progress.PullCount);
        economy.Pull();
        Assert.Equal(totalPulls, economy.Progress.Values.Sum(progress => progress.PullCount));
        Assert.Equal(0, economy.Currency);
        Assert.Throws<ArgumentOutOfRangeException>(() => economy.Credit(-1));
    }

    [Theory]
    [InlineData(PieceKind.Pawn, 1)]
    [InlineData(PieceKind.Knight, 2)]
    [InlineData(PieceKind.Bishop, 4)]
    [InlineData(PieceKind.Rook, 4)]
    public void DeploymentPricesFollowTheSpecifiedPiecePrices(PieceKind kind, int price)
        => Assert.Equal(price, Balance.PieceCost(kind));

    [Fact]
    public void FirstDropUnlocksRankOneThenEachTenDuplicatesAddsOneRank()
    {
        var progress = new UpgradeProgress();
        foreach (var (pulls, rank) in new[] { (1, 1), (10, 1), (11, 2), (20, 2), (21, 3) })
        {
            progress.PullCount = pulls;
            Assert.Equal(rank, progress.Rank);
            Assert.Equal(pulls - 1, progress.Duplicates);
        }
    }

    [Fact]
    public void PullsProduceExactlyOneOfTheSixComponentsAtTheSharedCost()
    {
        var economy = new Economy(171);
        for (int i = 0; i < 200; i++) economy.Pull();

        Assert.Equal(0, economy.Currency);
        Assert.Equal(6, economy.Progress.Count);
        Assert.Equal(200, economy.Progress.Values.Sum(progress => progress.PullCount));
        Assert.Equal(200, economy.Progress.Values.Sum(progress => progress.Available));
        Assert.All(Enum.GetValues<UpgradeKind>(), kind => Assert.True(economy.GetProgress(kind).PullCount > 0));
    }

    [Fact]
    public void EachAllocationConsumesOnePointAndThePerTowerCapLimitsRepeatedAllocation()
    {
        var economy = new Economy();
        var turret = new Turret { Kind = PieceKind.Pawn };
        var progress = economy.GetProgress(UpgradeKind.ATK);
        progress.PullCount = 11;
        progress.Available = 3;

        economy.Apply(turret, UpgradeKind.ATK);
        Assert.Equal(1, economy.GetAllocated(turret, UpgradeKind.ATK));
        Assert.Equal(2, progress.Available);
        Assert.Equal(2, economy.GetAttack(turret));
        Assert.True(economy.Apply(turret, UpgradeKind.ATK));
        Assert.Equal(1, progress.Available);
        Assert.Equal(3, economy.GetAttack(turret));
        Assert.False(economy.Apply(turret, UpgradeKind.ATK));
        Assert.Equal(1, progress.Available);
    }

    [Fact]
    public void HpComponentIncreasesCurrentAndMaximumHealthWithoutErasingDamage()
    {
        var economy = new Economy();
        var turret = new Turret { Kind = PieceKind.Pawn, Hp = 17 };
        var progress = economy.GetProgress(UpgradeKind.HP);
        progress.PullCount = 11;
        progress.Available = 2;

        economy.Apply(turret, UpgradeKind.HP);
        economy.Apply(turret, UpgradeKind.HP);

        Assert.Equal(50, economy.GetMaxHp(turret));
        Assert.Equal(37, turret.Hp);
        Assert.Equal(0, progress.Available);
    }

    [Theory]
    [InlineData(UpgradeKind.Orthogonal, 12)]
    [InlineData(UpgradeKind.PlaneDiagonal, 24)]
    [InlineData(UpgradeKind.SpaceDiagonal, 16)]
    public void DirectionalComponentsReachEveryRayOfTheirThreeDimensionalFamily(UpgradeKind kind, int familyCount)
    {
        var economy = new Economy();
        var turret = new Turret { Kind = PieceKind.Knight };
        var progress = economy.GetProgress(kind);
        progress.PullCount = 11;
        progress.Available = 2;
        economy.Apply(turret, kind);
        economy.Apply(turret, kind);

        var pattern = economy.GetAttackPattern(turret).ToHashSet();
        var family = pattern.Where(offset => economy.IsDirectionalOffset(turret, offset) && BelongsToFamily(offset, kind)).ToArray();
        Assert.Equal(familyCount, family.Length);
        Assert.Contains(family, offset => offset.Y == 2);
        Assert.Contains(family, offset => offset.Y == -2);
        Assert.DoesNotContain(new Cell(0, 0, 0), pattern);
        Assert.DoesNotContain(family, offset => offset.Chebyshev > 2);
    }

    [Fact]
    public void CustomJumpPatternsRespectReachPointsAndNonContinuousOffsets()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        Assert.True(economy.HasJump(knight));
        Assert.Equal(8, economy.GetJumpBudget(knight));
        Assert.True(economy.TrySetJumpPattern(knight, [new Cell(2, 0, 1), new Cell(0, 2, -1)], out _));
        var accepted = knight.JumpPattern.ToArray();
        Assert.False(economy.TrySetJumpPattern(knight, [new Cell(1, 0, 0)], out _));
        Assert.False(economy.TrySetJumpPattern(knight, [new Cell(3, 0, 0)], out _));
        Assert.False(economy.TrySetJumpPattern(knight, [], out _));
        Assert.False(economy.TrySetJumpPattern(knight,
            Enumerable.Range(-2, 5).SelectMany(x => new[] { new Cell(x, 2, 0), new Cell(x, -2, 0) }), out _));
        Assert.Equal(accepted, knight.JumpPattern);

        var progress = economy.GetProgress(UpgradeKind.Jump);
        progress.PullCount = 11;
        progress.Available = 1;
        economy.Apply(knight, UpgradeKind.Jump);
        Assert.Equal(12, economy.GetJumpBudget(knight));
        Assert.True(economy.TrySetJumpPattern(knight, [new Cell(3, 1, 0)], out _));
        Assert.Contains(new Cell(3, 1, 0), economy.GetAttackPattern(knight));
    }

    [Fact]
    public void NonJumpPiecesNeedAJumpComponentBeforeTheyCanUseAnEditedPattern()
    {
        var economy = new Economy();
        var turret = new Turret { Kind = PieceKind.Pawn };
        Assert.False(economy.TrySetJumpPattern(turret, [new Cell(2, 0, 0)], out _));
        economy.GetProgress(UpgradeKind.Jump).PullCount = 1;
        economy.GetProgress(UpgradeKind.Jump).Available = 1;
        economy.Apply(turret, UpgradeKind.Jump);
        Assert.True(economy.TrySetJumpPattern(turret, [new Cell(2, 0, 0)], out _));
    }

    [Fact]
    public void MixedUpgradesKeepJumpAndDirectionalAttacksDistinct()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        economy.GetProgress(UpgradeKind.Orthogonal).PullCount = 1;
        economy.GetProgress(UpgradeKind.Orthogonal).Available = 1;
        economy.Apply(knight, UpgradeKind.Orthogonal);

        Assert.True(economy.IsDirectionalOffset(knight, new Cell(1, 0, 0)));
        Assert.False(economy.IsJumpOffset(knight, new Cell(1, 0, 0)));
        Assert.True(economy.IsJumpOffset(knight, new Cell(2, 0, 1)));
        Assert.False(economy.IsDirectionalOffset(knight, new Cell(2, 0, 1)));
    }

    private static bool BelongsToFamily(Cell cell, UpgradeKind kind)
    {
        int nonZero = (cell.X != 0 ? 1 : 0) + (cell.Y != 0 ? 1 : 0) + (cell.Z != 0 ? 1 : 0);
        int expected = kind switch { UpgradeKind.Orthogonal => 1, UpgradeKind.PlaneDiagonal => 2, _ => 3 };
        return nonZero == expected;
    }
}
