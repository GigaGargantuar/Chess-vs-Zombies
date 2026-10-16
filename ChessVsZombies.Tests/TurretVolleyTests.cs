using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class TurretVolleyTests
{
    private static readonly Cell Origin = new(8, 16, 8);

    [Fact]
    public void ARookFiresAtEveryOccupiedDirectionalDestinationInOneVolley()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Rook, Origin);
        var targets = new[]
        {
            AddZombie(game, Origin + new Cell(-4, 0, 0)),
            AddZombie(game, Origin + new Cell(4, 0, 0)),
            AddZombie(game, Origin + new Cell(0, 0, 4)),
            AddZombie(game, Origin + new Cell(0, 4, 0))
        };
        AddZombie(game, Origin + new Cell(3, 0, 1));

        Fire(game);

        Assert.Equal(targets.Select(target => target.Id).Order(), game.Projectiles.Select(shot => shot.TargetId).Order());
        Assert.All(game.Projectiles, shot => Assert.Equal(ShotKind.Pellet, shot.Kind));
        Assert.Equal(.7f, turret.Cooldown);
        Fire(game, .1f);
        Assert.Equal(4, game.Projectiles.Count);
    }

    [Fact]
    public void SeparateOccupiedCellsAlongTheSameInfiniteRayEachLaunchAPhysicalShot()
    {
        var game = new GameSession();
        AddTurret(game, PieceKind.Rook, Origin);
        var farther = AddZombie(game, Origin + new Cell(5, 0, 0));
        var nearer = AddZombie(game, Origin + new Cell(3, 0, 0));

        Fire(game);

        Assert.Equal(new[] { nearer.Id, farther.Id }, game.Projectiles.Select(shot => shot.TargetId));
        AdvanceShots(game);
        // Each projectile still strikes the first body on its actual trajectory.
        Assert.Equal(Balance.ZombieHp - 2, nearer.BodyHp);
        Assert.Equal(Balance.ZombieHp, farther.BodyHp);
    }

    [Fact]
    public void SeparateOccupiedCellsAlongAnUpgradedFiniteRayEachLaunchAShot()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Pawn, Origin);
        turret.UpgradePoints[UpgradeKind.Orthogonal] = 2;
        var near = AddZombie(game, Origin + new Cell(2, 0, 0));
        var far = AddZombie(game, Origin + new Cell(3, 0, 0));
        AddZombie(game, Origin + new Cell(4, 0, 0));

        Fire(game);

        Assert.Equal(new[] { near.Id, far.Id }, game.Projectiles.Select(shot => shot.TargetId));
        Assert.All(game.Projectiles, shot => Assert.Equal(ShotKind.Pellet, shot.Kind));
    }

    [Fact]
    public void APawnStrikesAllUnobstructedAdjacentDestinationsOnTheSameCooldown()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Pawn, Origin);
        var targets = new[]
        {
            AddZombie(game, Origin + new Cell(-1, 0, 0)),
            AddZombie(game, Origin + new Cell(1, 0, 0)),
            AddZombie(game, Origin + new Cell(0, 0, 1))
        };

        Fire(game);

        Assert.All(targets, target => Assert.Equal(Balance.ZombieHp - 1, target.BodyHp));
        Assert.Equal(3, game.Projectiles.Count);
        Assert.All(game.Projectiles, shot => Assert.Equal(ShotKind.Melee, shot.Kind));
        Assert.Equal(.7f, turret.Cooldown);
    }

    [Fact]
    public void AKnightLobsAtAllItsOccupiedDestinationsAcrossThreePlanes()
    {
        var game = new GameSession();
        var origin = new Cell(8, 20, 8);
        var turret = AddTurret(game, PieceKind.Knight, origin);
        var offsets = new[] { new Cell(-2, 1, 0), new Cell(2, 0, 1), new Cell(0, 2, -1), new Cell(0, -1, 2) };
        var targets = offsets.Select(offset => AddZombie(game, origin + offset)).ToArray();

        Fire(game);

        Assert.Equal(targets.Select(target => target.Id).Order(), game.Projectiles.Select(shot => shot.TargetId).Order());
        Assert.All(game.Projectiles, shot => Assert.Equal(ShotKind.Lob, shot.Kind));
        Assert.Equal(1, turret.Cooldown);
    }

    [Fact]
    public void AnOverlappingJumpAndDirectionalDestinationContributesOnlyOneLob()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Rook, Origin);
        turret.UpgradePoints[UpgradeKind.Jump] = 1;
        turret.JumpPattern.Clear();
        turret.JumpPattern.Add(new Cell(2, 0, 0));
        var target = AddZombie(game, Origin + new Cell(2, 0, 0));

        Fire(game);

        var shot = Assert.Single(game.Projectiles);
        Assert.Equal(target.Id, shot.TargetId);
        Assert.Equal(ShotKind.Lob, shot.Kind);
        Assert.Equal(1, turret.Cooldown);
    }

    [Fact]
    public void BodiesSharingADestinationProduceOneShotWithTheExistingNearestTieOrder()
    {
        var game = new GameSession();
        AddTurret(game, PieceKind.Rook, Origin);
        AddZombie(game, Origin + new Cell(3, 0, 0));
        var near = AddZombie(game, Origin + new Cell(3, 0, 0));
        near.Position -= Vector3.UnitX * .1f;
        var tied = AddZombie(game, Origin + new Cell(3, 0, 0));
        tied.Position = near.Position;

        Fire(game);

        Assert.Equal(near.Id, Assert.Single(game.Projectiles).TargetId);
    }

    [Fact]
    public void MixedMeleeAndLobDestinationsShareTheLongerVolleyCooldown()
    {
        var game = new GameSession();
        var turret = AddTurret(game, PieceKind.Pawn, Origin);
        turret.UpgradePoints[UpgradeKind.Jump] = 1;
        var adjacent = AddZombie(game, Origin + new Cell(1, 0, 0));
        var distant = AddZombie(game, Origin + new Cell(2, 0, 1));

        Fire(game);

        Assert.Equal(Balance.ZombieHp - 1, adjacent.BodyHp);
        Assert.Equal(Balance.ZombieHp, distant.BodyHp);
        Assert.Equal(new[] { ShotKind.Melee, ShotKind.Lob }, game.Projectiles.Select(shot => shot.Kind));
        Assert.Equal(1, turret.Cooldown);
        Fire(game, .71f);
        Assert.Equal(2, game.Projectiles.Count);
        Fire(game, .30f);
        Assert.Equal(4, game.Projectiles.Count);
        Assert.Equal(Balance.ZombieHp - 2, adjacent.BodyHp);
        Assert.Equal(1, turret.Cooldown);
    }

    [Fact]
    public void VolleyShotsWearOrdinaryCoverWhileCoreBlockedDestinationsAreExcluded()
    {
        var game = new GameSession();
        var origin = new Cell(11, 16, 15);
        AddTurret(game, PieceKind.Rook, origin);
        var blockedByCore = AddZombie(game, new Cell(20, 16, 15));
        var near = AddZombie(game, new Cell(11, 16, 23));
        var far = AddZombie(game, new Cell(11, 16, 25));
        var coverCell = new Cell(11, 16, 20);
        Assert.True(game.Inventory.Add(Material.Dirt));
        Assert.True(game.BuildBarrier(coverCell));

        Fire(game);

        Assert.Equal(new[] { near.Id, far.Id }, game.Projectiles.Select(shot => shot.TargetId));
        AdvanceShots(game);
        Assert.Equal(Balance.DirtBlockHp - 2, game.World.GetBarrier(coverCell)!.Hp);
        Assert.All(new[] { blockedByCore, near, far }, target => Assert.Equal(Balance.ZombieHp, target.BodyHp));
        Assert.Equal(Balance.CoreHp, game.CoreHp);
    }

    [Fact]
    public void AnOccludedMeleeDestinationDoesNotPreventTheOtherAdjacentStrikes()
    {
        var game = new GameSession();
        AddTurret(game, PieceKind.Pawn, Origin);
        var open = AddZombie(game, Origin + new Cell(-1, 0, 0));
        var blocked = AddZombie(game, Origin + new Cell(1, 0, 0));
        Assert.True(game.Inventory.Add(Material.Dirt));
        Assert.True(game.BuildBarrier(blocked.Cell));

        Fire(game);

        Assert.Equal(open.Id, Assert.Single(game.Projectiles).TargetId);
        Assert.Equal(Balance.ZombieHp - 1, open.BodyHp);
        Assert.Equal(Balance.ZombieHp, blocked.BodyHp);
    }

    private static Turret AddTurret(GameSession game, PieceKind kind, Cell cell)
    {
        var turret = new Turret { Id = 1, Kind = kind, Cell = cell };
        game.Turrets.Add(turret);
        return turret;
    }

    private static Zombie AddZombie(GameSession game, Cell cell)
    {
        var zombie = new Zombie { Id = 100 + game.Zombies.Count, WaveIndex = -1, Position = cell.Center };
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static void Fire(GameSession game, float seconds = .01f) => Invoke(game, "UpdateTurrets", seconds);
    private static void AdvanceShots(GameSession game) => Invoke(game, "UpdateProjectiles", 4);
    private static void Invoke(GameSession game, string method, float seconds) => typeof(GameSession)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, [seconds]);
}
