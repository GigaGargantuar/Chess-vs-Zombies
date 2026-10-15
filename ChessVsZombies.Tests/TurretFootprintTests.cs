using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class TurretFootprintTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void RooksAcquireAnOccupiedKiloLaneThatDoesNotContainItsAnchor(int turretY)
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Rook, new(10, turretY, 15));
        var kilo = AddZombie(game, ZombieSize.Kilo, new(5, 16, 14));

        var (target, shot, distance) = Select(game, turret);
        var aim = Aim(game, turret, kilo, shot);

        Assert.Same(kilo, target);
        Assert.Equal(ShotKind.Pellet, shot);
        Assert.Equal(new Vector3(7, turretY + .5f, 15.5f), aim);
        Assert.Equal(Vector3.DistanceSquared(turret.Cell.Center, aim), distance, 5);
        Fire(game);
        Assert.Equal(aim, Assert.Single(game.Projectiles).Target);
    }

    [Fact]
    public void BishopsAimAlongTheEligibleDiagonalInsteadOfTheKiloCenter()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Bishop, new(10, 20, 15));
        var kilo = AddZombie(game, ZombieSize.Kilo, new(5, 16, 14));

        var (target, shot, _) = Select(game, turret);
        var aim = Aim(game, turret, kilo, shot);

        Assert.Same(kilo, target);
        Assert.Equal(ShotKind.Pellet, shot);
        Assert.Equal(aim.X - turret.Cell.Center.X, aim.Y - turret.Cell.Center.Y, 5);
        Assert.Equal(turret.Cell.Center.Z, aim.Z, 5);
        AssertOnBody(kilo, aim);
        Assert.NotEqual(kilo.Position, aim);
    }

    [Fact]
    public void PawnsStrikeAnAdjacentOccupiedCellEvenWhenTheAnchorIsTwoCellsAway()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Pawn, new(8, 16, 15));
        var kilo = AddZombie(game, ZombieSize.Kilo, new(6, 16, 14));
        float originalHp = kilo.Hp;

        var (target, shot, _) = Select(game, turret);
        var aim = Aim(game, turret, kilo, shot);
        Fire(game);

        Assert.Same(kilo, target);
        Assert.Equal(ShotKind.Melee, shot);
        Assert.Equal(new Vector3(8, 16.5f, 15.5f), aim);
        Assert.Equal(originalHp - 1, kilo.Hp);
        Assert.Equal(aim, Assert.Single(game.Projectiles).Target);
    }

    [Fact]
    public void KnightsCanLobAtTheOccupiedSideOfAKiloOutsideItsAnchorPattern()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Knight, new(10, 16, 15));
        var kilo = AddZombie(game, ZombieSize.Kilo, new(7, 16, 15));

        var (target, shot, _) = Select(game, turret);
        var aim = Aim(game, turret, kilo, shot);
        Fire(game);

        Assert.Same(kilo, target);
        Assert.Equal(ShotKind.Lob, shot);
        AssertOnBody(kilo, aim);
        Assert.Equal(2 * (aim.Z - turret.Cell.Center.Z), turret.Cell.Center.X - aim.X, 5);
        var projectile = Assert.Single(game.Projectiles);
        Assert.Equal(ShotKind.Lob, projectile.Kind);
        Assert.Equal(aim, projectile.Target);
    }

    [Fact]
    public void TheNearestEligibleAimKeepsSelectionAndLaunchInTheSameShotFamily()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Rook, new(10, 16, 15));
        turret.UpgradePoints[UpgradeKind.Jump] = 1;
        var kilo = AddZombie(game, ZombieSize.Kilo, new(7, 16, 14));

        var (target, shot, _) = Select(game, turret);
        Fire(game);

        Assert.Same(kilo, target);
        Assert.Equal(ShotKind.Pellet, shot);
        var projectile = Assert.Single(game.Projectiles);
        Assert.Equal(shot, projectile.Kind);
        Assert.Equal(new Vector3(9, 16.5f, 15.5f), projectile.Target);
    }

    [Fact]
    public void APartiallyOccupiedCellDoesNotGrantALaneThatMissesTheActualBody()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Rook, new(10, 16, 16));
        var kilo = AddZombie(game, ZombieSize.Kilo, new(5, 16, 14));
        kilo.Position += new Vector3(.9f, 0, .1f);
        Assert.True(kilo.Position.Z + kilo.BodyHalfExtents.Z > 16);

        var (target, _, _) = Select(game, turret);
        Fire(game);

        Assert.Null(target);
        Assert.Empty(game.Projectiles);
    }

    [Fact]
    public void MerelyTouchingTheNextGridCellDoesNotGrantItsFiringLane()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Rook, new(10, 16, 16));
        AddZombie(game, ZombieSize.Kilo, new(5, 16, 14));

        var (target, _, _) = Select(game, turret);

        Assert.Null(target);
    }

    [Theory]
    [InlineData(ZombieSize.Standard)]
    [InlineData(ZombieSize.Milli)]
    public void SmallerSizesRetainTheirOriginalAnchorPatternAndCenterAim(ZombieSize size)
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Rook, new(10, 16, 15));
        var zombie = AddZombie(game, size, new(5, 16, 15));

        var (target, shot, distance) = Select(game, turret);
        var aim = Aim(game, turret, zombie, shot);
        Fire(game);

        Assert.Same(zombie, target);
        Assert.Equal(ShotKind.Pellet, shot);
        Assert.Equal(zombie.Position, aim);
        Assert.Equal(Vector3.DistanceSquared(turret.Cell.Center, zombie.Position), distance);
        Assert.Equal(zombie.Position, Assert.Single(game.Projectiles).Target);
    }

    private static Turret AddTurret(GameSession game, PieceKind kind, Cell cell)
    {
        var turret = new Turret { Id = 1, Kind = kind, Cell = cell };
        game.Turrets.Add(turret);
        return turret;
    }

    private static Zombie AddZombie(GameSession game, ZombieSize size, Cell cell)
    {
        var zombie = new Zombie { Id = 100, WaveIndex = -1, Size = size, Attack = 1 };
        zombie.Position = zombie.StandAt(cell);
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static (Zombie? Target, ShotKind Shot, float Distance) Select(GameSession game, Turret turret)
    {
        object?[] arguments = [turret, default(ShotKind), 0f];
        var target = (Zombie?)typeof(GameSession).GetMethod("SelectTurretTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(game, arguments);
        return (target, (ShotKind)arguments[1]!, (float)arguments[2]!);
    }

    private static Vector3 Aim(GameSession game, Turret turret, Zombie zombie, ShotKind shot) => (Vector3)typeof(GameSession)
        .GetMethod("GetTurretAimPoint", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, [turret, zombie, shot])!;

    private static void Fire(GameSession game) => typeof(GameSession)
        .GetMethod("UpdateTurrets", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, [.7f]);

    private static void AssertOnBody(Zombie zombie, Vector3 aim)
    {
        var min = zombie.Position - zombie.BodyHalfExtents;
        var max = zombie.Position + zombie.BodyHalfExtents;
        Assert.InRange(aim.X, min.X - .0001f, max.X + .0001f);
        Assert.InRange(aim.Y, min.Y - .0001f, max.Y + .0001f);
        Assert.InRange(aim.Z, min.Z - .0001f, max.Z + .0001f);
        Assert.True(Math.Abs(aim.X - min.X) < .0001f || Math.Abs(aim.X - max.X) < .0001f ||
            Math.Abs(aim.Y - min.Y) < .0001f || Math.Abs(aim.Y - max.Y) < .0001f ||
            Math.Abs(aim.Z - min.Z) < .0001f || Math.Abs(aim.Z - max.Z) < .0001f);
    }
}
