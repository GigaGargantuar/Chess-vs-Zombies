using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class Knight3DPatternTests
{
    [Fact]
    public void ANewKnightHasAllTwentyFourLJumpsAcrossThreePlanes()
    {
        var knight = new Turret { Kind = PieceKind.Knight };
        var pattern = knight.JumpPattern;

        Assert.Equal(24, pattern.Count);
        Assert.Equal(24, pattern.Distinct().Count());
        Assert.All(pattern, offset =>
        {
            var axes = new[] { Math.Abs(offset.X), Math.Abs(offset.Y), Math.Abs(offset.Z) }.Order().ToArray();
            Assert.Equal(new[] { 0, 1, 2 }, axes);
        });
        Assert.Equal(8, pattern.Count(offset => offset.Y == 0));
        Assert.Equal(8, pattern.Count(offset => offset.Z == 0));
        Assert.Equal(8, pattern.Count(offset => offset.X == 0));
        var expected = new HashSet<Cell>();
        foreach (int longSign in new[] { -1, 1 })
            foreach (int shortSign in new[] { -1, 1 })
            {
                expected.UnionWith(new[]
                {
                    new Cell(2 * longSign, shortSign, 0), new Cell(shortSign, 2 * longSign, 0),
                    new Cell(2 * longSign, 0, shortSign), new Cell(shortSign, 0, 2 * longSign),
                    new Cell(0, 2 * longSign, shortSign), new Cell(0, shortSign, 2 * longSign)
                });
            }
        Assert.True(expected.SetEquals(pattern));
        var economy = new Economy();
        Assert.Equal(2, JumpRules.FormationRadius(pattern));
        Assert.True(economy.GetAttackPattern(knight).ToHashSet().SetEquals(expected));
    }

    [Theory]
    [InlineData(PieceKind.Pawn)]
    [InlineData(PieceKind.Bishop)]
    [InlineData(PieceKind.Rook)]
    public void OtherPiecesKeepTheOriginalEightOffsetJumpProfile(PieceKind kind)
    {
        var economy = new Economy();
        var piece = new Turret { Kind = kind };
        Assert.Equal(8, piece.JumpPattern.Count);
        Assert.All(piece.JumpPattern, offset => Assert.Equal(0, offset.Y));
        economy.GetProgress(UpgradeKind.Jump).PullCount = 2;
        economy.GetProgress(UpgradeKind.Jump).Available = 2;

        Assert.True(economy.Apply(piece, UpgradeKind.Jump));

        Assert.Equal(2, economy.GetRange(piece, UpgradeKind.Jump));
        Assert.Equal(0, economy.GetProgress(UpgradeKind.Jump).Available);
    }

    [Fact]
    public void KnightFormationRadiusCanExpandWithoutASeparateDestinationCountCost()
    {
        var economy = new Economy();
        var knight = new Turret { Kind = PieceKind.Knight };
        economy.GetProgress(UpgradeKind.Jump).PullCount = 21;
        economy.GetProgress(UpgradeKind.Jump).Available = 3;
        Assert.True(economy.TrySetJumpPattern(knight, JumpRules.DefaultKnightPattern().Append(new Cell(3, 1, 0)), out _));
        Assert.Equal(25, knight.JumpPattern.Count);
        Assert.Equal(1, economy.GetProgress(UpgradeKind.Jump).Available);
    }

    [Theory]
    [InlineData(2, 1, 0)]
    [InlineData(1, 2, 0)]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    public void ADefaultKnightActuallyLobsAtTargetsInEachVerticalPlane(int x, int y, int z)
    {
        var game = new GameSession();
        var cell = new Cell(5, 16, 5);
        Assert.True(game.Deploy(PieceKind.Knight, cell));
        var target = new Zombie { Id = -1, Position = (cell + new Cell(x, y, z)).Center };
        game.Zombies.Add(target);

        Invoke(game, "UpdateTurrets", .01f);

        var shot = Assert.Single(game.Projectiles);
        Assert.Equal(ShotKind.Lob, shot.Kind);
        Assert.Equal(target.Id, shot.TargetId);
        Invoke(game, "UpdateProjectiles", 2f);
        Assert.Equal(9, target.BodyHp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckpointsPreserveEitherTheNewTwentyFourJumpsOrAnExistingCustomEight(bool customEight)
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(5, 16, 5)));
        var knight = Assert.Single(game.Turrets);
        if (customEight)
        {
            knight.JumpPattern.Clear();
            knight.JumpPattern.AddRange(JumpRules.DefaultPattern(PieceKind.Pawn));
        }
        var expected = knight.JumpPattern.ToArray();
        string path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-knight3d-{Guid.NewGuid():N}.json");
        try
        {
            SaveStore.Save(game, path);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);

            Assert.Equal(expected, Assert.Single(restored!.Turrets).JumpPattern);
            Assert.Equal(customEight ? 8 : 24, Assert.Single(restored.Turrets).JumpPattern.Count);
            Assert.Equal(24, restored.UpgradeTemplate.JumpPattern.Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void DefaultTemplatesAndKnightsOwnIndependentPatternLists()
    {
        var template = new UpgradeTemplate();
        var first = new Turret { Kind = PieceKind.Knight };
        var second = new Turret { Kind = PieceKind.Knight };
        first.JumpPattern.Clear();

        Assert.Equal(24, template.JumpPattern.Count);
        Assert.Equal(24, second.JumpPattern.Count);
        Assert.Equal(JumpRules.DefaultKnightPattern(), template.JumpPattern);
    }

    private static object? Invoke(object target, string method, params object?[] arguments) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
