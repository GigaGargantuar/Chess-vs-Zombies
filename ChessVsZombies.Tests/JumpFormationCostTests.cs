using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class JumpFormationCostTests
{
    [Fact]
    public void TwoPointsBuyCurrentJumpCapRegardlessOfFormationRadiusOrDestinationCount()
    {
        var sparse = new Economy();
        var dense = new Economy();
        var first = new Turret { Kind = PieceKind.Knight };
        var second = new Turret { Kind = PieceKind.Knight };
        Seed(sparse, 91, 2);
        Seed(dense, 91, 2);
        var many = Enumerable.Range(-2, 5).SelectMany(x => Enumerable.Range(-2, 5)
            .SelectMany(y => Enumerable.Range(-2, 5).Select(z => new Cell(x, y, z))))
            .Where(cell => cell.Chebyshev == 2).Take(35).ToArray();

        Assert.True(sparse.TrySetJumpPattern(first, [new Cell(2, 0, 0)], out var sparseError), sparseError);
        Assert.True(dense.TrySetJumpPattern(second, many, out var denseError), denseError);
        Assert.Equal(2, JumpRules.FormationRadius(many));
        Assert.Equal(10, sparse.GetAllocated(first, UpgradeKind.Jump));
        Assert.Equal(10, dense.GetAllocated(second, UpgradeKind.Jump));
        Assert.Equal(0, sparse.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(0, dense.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(35, second.JumpPattern.Count);
    }

    [Fact]
    public void PurchasedReachMakesEditsFreeAndTwoPointsCatchUpToARaisedCap()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        Seed(economy, 21, 5);
        Assert.True(economy.TrySetJumpPattern(knight, [new Cell(2, 0, 0)], out _));
        Assert.Equal(3, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(3, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.True(economy.TrySetJumpPattern(knight, [new Cell(3, 1, 0), new Cell(2, 0, 0)], out _));
        Assert.Equal(3, economy.GetProgress(UpgradeKind.Jump).Available);

        economy.GetProgress(UpgradeKind.Jump).PullCount = 191;
        Assert.Equal(3, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.True(economy.TrySetJumpPattern(knight, [new Cell(20, 1, 0)], out _));
        Assert.Equal(20, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(1, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.True(economy.TrySetJumpPattern(knight, [new Cell(2, 0, 1)], out _));
        Assert.True(economy.TrySetJumpPattern(knight, [new Cell(2, 0, 1)], out _));
        Assert.Equal(20, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(1, economy.GetProgress(UpgradeKind.Jump).Available);
    }

    [Fact]
    public void InsufficientMatchingPointsOrCollectionCapLeaveThePatternAndBalanceUntouched()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        var before = knight.JumpPattern.ToArray();
        Seed(economy, 11, 1);
        Assert.False(economy.TrySetJumpPattern(knight, [new Cell(2, 0, 0)], out _));
        Assert.Equal(before, knight.JumpPattern);
        Assert.Equal(1, economy.GetProgress(UpgradeKind.Jump).Available);
        Seed(economy, 11, 3);
        Assert.False(economy.TrySetJumpPattern(knight, [new Cell(3, 0, 0)], out _));
        Assert.Equal(before, knight.JumpPattern);
        Assert.Equal(3, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(0, economy.GetAllocated(knight, UpgradeKind.Jump));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(int.MinValue, 0, 0)]
    public void InvalidDestinationsNeverDeductJumpPoints(int x, int y, int z)
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        Seed(economy, 11, 2);
        Assert.False(economy.TrySetJumpPattern(knight, [new Cell(x, y, z)], out _));
        Assert.Equal(2, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(0, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.False(economy.TrySetJumpPattern(knight, [new Cell(2, 0, 0), new Cell(2, 0, 0)], out _));
        Assert.Equal(2, economy.GetProgress(UpgradeKind.Jump).Available);
    }

    [Fact]
    public void ExistingSavedFormationLoadsAndIdenticalReapplicationDoesNotCharge()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(8, 16, 8)));
        var knight = Assert.Single(game.Turrets);
        knight.JumpPattern.Clear();
        knight.JumpPattern.Add(new Cell(2, 0, 0));
        string path = Path.Combine(Path.GetTempPath(), $"cvz-legacy-formation-{Guid.NewGuid():N}.json");
        try
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            var existing = Assert.Single(restored!.Turrets);
            Assert.Equal(new Cell(2, 0, 0), Assert.Single(existing.JumpPattern));
            Assert.True(restored.Economy.TrySetJumpPattern(existing, [new Cell(2, 0, 0)], out _));
            Assert.Equal(0, restored.Economy.GetAllocated(existing, UpgradeKind.Jump));
            Assert.False(restored.Economy.TrySetJumpPattern(existing, [new Cell(-2, 0, 0)], out _));
            Assert.Equal(new Cell(2, 0, 0), Assert.Single(existing.JumpPattern));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void BatchBoughtJumpLevelsCoverTheFormationWithoutAnotherCharge()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(8, 16, 8)));
        var knight = Assert.Single(game.Turrets);
        Seed(game.Economy, 11, 2);
        Assert.True(game.SpendUpgradeBatchNow([knight.Id], new UpgradeTemplate
        {
            Points = new() { [UpgradeKind.Jump] = 2 }
        }));
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.True(game.Economy.TrySetJumpPattern(knight, [new Cell(2, 1, 0)], out _));
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(2, game.Economy.GetAllocated(knight, UpgradeKind.Jump));
    }

    [Fact]
    public void TemplateJumpRequestAndFormationShareOneCostInsteadOfChargingTwice()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        Seed(economy, 11, 2);
        var template = new UpgradeTemplate
        {
            Points = new() { [UpgradeKind.Jump] = 2 },
            UseJumpPattern = true,
            JumpPattern = [new Cell(2, 1, 0), new Cell(-2, 0, 1)]
        };

        Assert.True(economy.TryApplyTemplate(knight, template, out var error), error);
        Assert.Equal(2, economy.GetAllocated(knight, UpgradeKind.Jump));
        Assert.Equal(0, economy.GetProgress(UpgradeKind.Jump).Available);
        Assert.Equal(2, knight.JumpPattern.Count);
    }

    private static void Seed(Economy economy, int pulls, int available)
    {
        var progress = economy.GetProgress(UpgradeKind.Jump);
        progress.PullCount = pulls;
        progress.Available = available;
    }
}
