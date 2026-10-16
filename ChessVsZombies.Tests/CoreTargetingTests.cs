using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class CoreTargetingTests
{
    private static readonly Cell RookCell = new(11, 16, 15);
    private static readonly Cell BehindCore = new(20, 16, 15);

    [Fact]
    public void ARookDoesNotWasteShotsOrCooldownOnAZombieBehindTheCore()
    {
        var game = new GameSession();
        var rook = Deploy(game, PieceKind.Rook, RookCell);
        var zombie = AddZombie(game, BehindCore);

        Fire(game);
        Fire(game);

        Assert.Empty(game.Projectiles);
        Assert.Equal(0, rook.Cooldown);
        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);
        Assert.Equal(Balance.CoreHp, game.CoreHp);
    }

    [Fact]
    public void OrdinaryCoverBeforeTheCoreDoesNotMakeTheBlockedTargetAttackable()
    {
        var game = new GameSession();
        var rook = Deploy(game, PieceKind.Rook, RookCell);
        var coverCell = new Cell(13, 16, 15);
        Assert.True(game.Inventory.Add(Material.Dirt));
        Assert.True(game.BuildBarrier(coverCell));
        AddZombie(game, BehindCore);

        Fire(game);

        Assert.Empty(game.Projectiles);
        Assert.Equal(0, rook.Cooldown);
        Assert.Equal(Balance.DirtBlockHp, game.World.GetBarrier(coverCell)!.Hp);
        Assert.Equal(Balance.CoreHp, game.CoreHp);
    }

    [Fact]
    public void AFartherReachableZombieIsSelectedInsteadOfANearerCoreBlockedZombie()
    {
        var game = new GameSession();
        Deploy(game, PieceKind.Rook, RookCell);
        var blocked = AddZombie(game, BehindCore);
        var reachable = AddZombie(game, new Cell(11, 16, 27));
        Assert.True(Vector3.DistanceSquared(RookCell.Center, blocked.Position) <
            Vector3.DistanceSquared(RookCell.Center, reachable.Position));

        Fire(game);

        var projectile = Assert.Single(game.Projectiles);
        Assert.Equal(reachable.Id, projectile.TargetId);
        Assert.Equal(reachable.Position, projectile.Target);
        AdvanceProjectiles(game, projectile.Duration + .01f);
        Assert.Equal(Balance.ZombieHp - 1, reachable.BodyHp);
        Assert.Equal(Balance.ZombieHp, blocked.BodyHp);
    }

    [Fact]
    public void DestructibleCoverWithoutTheCoreStillReceivesTheRooksShot()
    {
        var game = new GameSession();
        var rook = Deploy(game, PieceKind.Rook, RookCell);
        var coverCell = new Cell(13, 16, 15);
        Assert.True(game.Inventory.Add(Material.Dirt));
        Assert.True(game.BuildBarrier(coverCell));
        var zombie = AddZombie(game, new Cell(14, 16, 15));

        Fire(game);

        var projectile = Assert.Single(game.Projectiles);
        Assert.Equal(zombie.Id, projectile.TargetId);
        Assert.Equal(ShotKind.Pellet, projectile.Kind);
        Assert.True(rook.Cooldown > 0);
        AdvanceProjectiles(game, projectile.Duration + .01f);
        Assert.Empty(game.Projectiles);
        Assert.Equal(Balance.DirtBlockHp - 1, game.World.GetBarrier(coverCell)!.Hp);
        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);
        Assert.Equal(Balance.CoreHp, game.CoreHp);
    }

    [Fact]
    public void AKnightCanLobOverTheCoreEvenWhenTheStraightAimChordCrossesIt()
    {
        var game = new GameSession();
        var knight = Deploy(game, PieceKind.Knight, RookCell);
        var offset = new Cell(9, 0, 1);
        ConfigureJump(game, knight, offset);
        var zombie = AddZombie(game, RookCell + offset);
        var chordMiddle = Vector3.Lerp(RookCell.Center, zombie.Position, .5f);
        Assert.True(WorldGrid.IsCore(CellAt(chordMiddle)));

        Fire(game);

        var projectile = Assert.Single(game.Projectiles);
        Assert.Equal(ShotKind.Lob, projectile.Kind);
        Assert.Equal(zombie.Id, projectile.TargetId);
        Assert.True(projectile.GetPositionAt(projectile.Duration * .5f).Y > 17);
        AdvanceProjectiles(game, projectile.Duration + .01f);
        Assert.Equal(Balance.ZombieHp - 1, zombie.BodyHp);
        Assert.Equal(Balance.CoreHp, game.CoreHp);
    }

    [Fact]
    public void AKnightDoesNotLobThroughTheCoreEvenWhenTheStraightAimChordPassesBelowIt()
    {
        var game = new GameSession();
        Assert.True(game.Inventory.TryPurchase(ToolKind.Shovel, game.Economy, out _));
        // A real excavated chamber gives the low shot an unobstructed launch
        // and destination. Its arc rises into the core instead of a dirt wall.
        for (int x = 11; x <= 20; x++)
            for (int y = 12; y <= 15; y++)
                for (int z = 15; z <= 16; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (!WorldGrid.IsCore(cell)) Assert.True(game.Dig(cell));
                }
        var origin = new Cell(11, 12, 15);
        var knight = Deploy(game, PieceKind.Knight, origin);
        var offset = new Cell(9, 0, 1);
        ConfigureJump(game, knight, offset);
        var zombie = AddZombie(game, origin + offset);
        var probe = new Projectile { Origin = origin.Center, Target = zombie.Position, Kind = ShotKind.Lob, Duration = 1 };
        Assert.False(WorldGrid.IsCore(CellAt(Vector3.Lerp(origin.Center, zombie.Position, .5f))));
        Assert.True(WorldGrid.IsCore(CellAt(probe.GetPositionAt(.5f))));

        Fire(game);

        Assert.Empty(game.Projectiles);
        Assert.Equal(0, knight.Cooldown);
        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);
        Assert.Equal(Balance.CoreHp, game.CoreHp);
    }

    [Theory]
    [InlineData(ZombieSize.Kilo)]
    [InlineData(ZombieSize.Mega)]
    public void AGiantStillOffersAnUnblockedJumpAimWhenItsNearestPelletAimCrossesTheCore(ZombieSize size)
    {
        var game = new GameSession();
        var rook = Deploy(game, PieceKind.Rook, RookCell);
        ConfigureJump(game, rook, new Cell(9, 1, 0));
        var zombie = AddZombie(game, new Cell(19, 16, 15), size);

        Fire(game);

        var projectile = Assert.Single(game.Projectiles);
        Assert.Equal(zombie.Id, projectile.TargetId);
        Assert.Equal(ShotKind.Lob, projectile.Kind);
        Assert.Equal(1, rook.Cooldown);
        Assert.True(projectile.Target.Y > RookCell.Center.Y);
        AdvanceProjectiles(game, projectile.Duration + .01f);
        Assert.Equal(Balance.ZombieHp * zombie.HealthMultiplier - 1, zombie.BodyHp);
        Assert.Equal(Balance.CoreHp, game.CoreHp);
    }

    private static Turret Deploy(GameSession game, PieceKind kind, Cell cell)
    {
        Assert.True(game.Deploy(kind, cell), game.Message);
        return Assert.Single(game.Turrets);
    }

    private static Zombie AddZombie(GameSession game, Cell cell, ZombieSize size = ZombieSize.Standard)
    {
        var zombie = new Zombie { Id = 100 + game.Zombies.Count, WaveIndex = -1, Size = size };
        zombie.Position = zombie.StandAt(cell);
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static void ConfigureJump(GameSession game, Turret turret, Cell offset)
    {
        int allocated = offset.Chebyshev;
        var progress = game.Economy.GetProgress(UpgradeKind.Jump);
        progress.PullCount = 1 + (allocated - 1) * Balance.DuplicatesPerRank;
        progress.Available = allocated;
        Assert.True(game.Economy.TryApplyTemplate(turret, new UpgradeTemplate
        {
            Points = new() { [UpgradeKind.Jump] = allocated },
            UseJumpPattern = true,
            JumpPattern = [offset]
        }, out var error), error);
        Assert.True(game.Economy.IsJumpOffset(turret, offset));
    }

    private static Cell CellAt(Vector3 point) => new((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z));

    private static void Fire(GameSession game) => Invoke(game, "UpdateTurrets", .01f);
    private static void AdvanceProjectiles(GameSession game, float seconds) => Invoke(game, "UpdateProjectiles", seconds);
    private static void Invoke(GameSession game, string method, float seconds) => typeof(GameSession)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, [seconds]);
}
