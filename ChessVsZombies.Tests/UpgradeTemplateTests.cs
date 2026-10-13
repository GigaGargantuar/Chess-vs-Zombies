using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class UpgradeTemplateTests
{
    [Fact]
    public void MultiCategoryTemplateChangesOnlyItsTargetAndPreservesExistingDamage()
    {
        var economy = new Economy();
        var target = new Turret { Kind = PieceKind.Pawn, Hp = 17 };
        var untouched = new Turret { Kind = PieceKind.Pawn };
        Seed(economy, UpgradeKind.HP, 11, 2);
        Seed(economy, UpgradeKind.ATK, 11, 2);
        var template = Template((UpgradeKind.HP, 2), (UpgradeKind.ATK, 1));
        Assert.True(economy.TryApplyTemplate(target, template, out var error), error);
        Assert.Equal(50, economy.GetMaxHp(target));
        Assert.Equal(37, target.Hp);
        Assert.Equal(2, economy.GetAttack(target));
        Assert.Equal(0, economy.GetProgress(UpgradeKind.HP).Available);
        Assert.Equal(1, economy.GetProgress(UpgradeKind.ATK).Available);
        Assert.Equal(30, economy.GetMaxHp(untouched));
        Assert.Equal(1, economy.GetAttack(untouched));
        Assert.Equal(2, template.Points[UpgradeKind.HP]);
    }

    [Fact]
    public void PullingPastMilestonesRaisesCapsWithoutChangingExistingTowerStatsOrReach()
    {
        var economy = new Economy(39);
        var turret = new Turret { Kind = PieceKind.Pawn };
        var kinds = new[] { UpgradeKind.HP, UpgradeKind.ATK, UpgradeKind.Orthogonal };
        foreach (var kind in kinds)
        {
            Seed(economy, kind, 10, 1);
            Assert.True(economy.Apply(turret, kind));
        }
        turret.Hp -= 7;
        for (int i = 0; i < 200 && kinds.Any(kind => economy.GetAllocationCap(kind) < 2); i++)
            economy.Pull([turret]);
        Assert.All(kinds, kind => Assert.True(economy.GetAllocationCap(kind) >= 2));
        Assert.Equal(40, economy.GetMaxHp(turret));
        Assert.Equal(33, turret.Hp);
        Assert.Equal(2, economy.GetAttack(turret));
        Assert.Equal(2, economy.GetRange(turret, UpgradeKind.Orthogonal));
        Assert.All(kinds, kind => Assert.Equal(1, economy.GetAllocated(turret, kind)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidCategoryRejectsTheWholeTemplateWithoutSpendingOtherPoints(bool exceedsCap)
    {
        var economy = new Economy();
        var turret = new Turret { Kind = PieceKind.Pawn, Hp = 17 };
        Seed(economy, UpgradeKind.HP, 11, 2);
        Seed(economy, UpgradeKind.ATK, exceedsCap ? 2 : 11, exceedsCap ? 2 : 0);
        var template = Template((UpgradeKind.HP, 1), (UpgradeKind.ATK, exceedsCap ? 2 : 1));
        Assert.False(economy.TryApplyTemplate(turret, template, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(turret.UpgradePoints);
        Assert.Equal(17, turret.Hp);
        Assert.Equal(2, economy.GetProgress(UpgradeKind.HP).Available);
        Assert.Equal(exceedsCap ? 2 : 0, economy.GetProgress(UpgradeKind.ATK).Available);
    }

    [Fact]
    public void InvalidJumpPatternRollsBackEveryComponentInTheTemplate()
    {
        var economy = new Economy();
        var turret = new Turret { Kind = PieceKind.Pawn };
        Seed(economy, UpgradeKind.HP, 1, 1);
        Seed(economy, UpgradeKind.Jump, 1, 1);
        var template = Template((UpgradeKind.HP, 1), (UpgradeKind.Jump, 1));
        template.UseJumpPattern = true;
        template.JumpPattern.Clear();
        template.JumpPattern.Add(new Cell(3, 0, 0));
        var originalPattern = turret.JumpPattern.ToArray();
        Assert.False(economy.TryApplyTemplate(turret, template, out _));
        Assert.Empty(turret.UpgradePoints);
        Assert.Equal(originalPattern, turret.JumpPattern);
        Assert.Equal(30, turret.Hp);
        Assert.Equal(1, economy.GetProgress(UpgradeKind.HP).Available);
        Assert.Equal(1, economy.GetProgress(UpgradeKind.Jump).Available);
    }

    [Theory]
    [InlineData(PieceKind.Rook, UpgradeKind.Orthogonal, 31, 0, 0)]
    [InlineData(PieceKind.Bishop, UpgradeKind.PlaneDiagonal, 31, 31, 0)]
    public void SlidingPiecesHaveUnlimitedInnateRaysWithoutBuyingRange(PieceKind kind, UpgradeKind family, int x, int y, int z)
    {
        var economy = new Economy();
        var turret = new Turret { Kind = kind };
        Assert.Equal(int.MaxValue, economy.GetRange(turret, family));
        Assert.True(economy.IsDirectionalOffset(turret, new Cell(x, y, z)));
        Assert.Contains(new Cell(x, y, z), economy.GetAttackPattern(turret));
        Seed(economy, family, 1, 1);
        Assert.False(economy.Apply(turret, family));
        Assert.Equal(1, economy.GetProgress(family).Available);
    }

    [Theory]
    [InlineData(PieceKind.Knight, 1, 3)]
    [InlineData(PieceKind.Pawn, 1, 2)]
    [InlineData(PieceKind.Pawn, 2, 3)]
    public void EachAllocatedJumpPointExtendsOnlyThatPiecesReach(PieceKind kind, int points, int range)
    {
        var economy = new Economy();
        var turret = new Turret { Kind = kind };
        Seed(economy, UpgradeKind.Jump, 11, 2);
        Assert.True(economy.TryApplyTemplate(turret, Template((UpgradeKind.Jump, points)), out _));
        Assert.Equal(range, economy.GetRange(turret, UpgradeKind.Jump));
        Assert.Equal(range == 2 ? 8 : 12, economy.GetJumpBudget(turret));
    }

    [Fact]
    public void EditingATemplateDoesNotChangeATowerUntilTheTemplateIsApplied()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        var before = knight.JumpPattern.ToArray();
        var template = new UpgradeTemplate { UseJumpPattern = true };
        template.JumpPattern.Clear();
        template.JumpPattern.Add(new Cell(0, 2, 0));
        Assert.Equal(before, knight.JumpPattern);
        Assert.True(economy.TryApplyTemplate(knight, template, out _));
        Assert.Equal(new Cell(0, 2, 0), Assert.Single(knight.JumpPattern));
    }

    [Fact]
    public void SessionApplicationRequiresAnActualLivingTowerDuringPreparation()
    {
        var game = new GameSession();
        Seed(game.Economy, UpgradeKind.HP, 11, 2);
        var template = Template((UpgradeKind.HP, 1));
        Assert.False(game.ApplyUpgradeTemplate(new Cell(11, 16, 12), template));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 16, 12)));
        Assert.True(game.ApplyUpgradeTemplate(new Cell(11, 16, 12), template));
        Assert.Equal(40, Assert.Single(game.Turrets).Hp);
        Assert.True(game.StartLevel());
        Assert.False(game.ApplyUpgradeTemplate(new Cell(11, 16, 12), template));
        Assert.Equal(1, game.Economy.GetProgress(UpgradeKind.HP).Available);
        Assert.Equal(40, Assert.Single(game.Turrets).Hp);
    }

    private static void Seed(Economy economy, UpgradeKind kind, int pulls, int available)
    {
        economy.GetProgress(kind).PullCount = pulls;
        economy.GetProgress(kind).Available = available;
    }

    private static UpgradeTemplate Template(params (UpgradeKind Kind, int Count)[] points)
    {
        var template = new UpgradeTemplate();
        foreach (var (kind, count) in points) template.Points[kind] = count;
        return template;
    }
}
