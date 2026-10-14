using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class ProjectileArmorIntegrationTests
{
    [Fact]
    public void ARookAcquiresAndHitsATargetThroughTwoFriendlyPieces()
    {
        var game = Battle();
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(8, 16, 8)));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(9, 16, 8)));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(10, 16, 8)));
        var shooter = game.Turrets[0];
        foreach (var turret in game.Turrets) turret.Cooldown = 10000;
        var zombie = Begin(game, new Cell(11, 16, 8));
        shooter.Cooldown = 0;
        game.Update(.01f);
        Assert.Equal(ShotKind.Pellet, Assert.Single(game.Projectiles).Kind);
        game.Update(.23f);
        Assert.Equal(9, zombie.BodyHp);
        Assert.All(game.Turrets, turret => Assert.Equal(Balance.TurretHp, turret.Hp));
    }

    [Fact]
    public void AKnightLobPassesThroughAnInterveningTowerStack()
    {
        var game = Battle();
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(8, 16, 8)));
        for (int y = 16; y <= 18; y++) Assert.True(game.Deploy(PieceKind.Pawn, new Cell(9, y, 8)));
        var shooter = game.Turrets[0];
        foreach (var turret in game.Turrets) turret.Cooldown = 10000;
        var zombie = Begin(game, new Cell(10, 16, 9));
        shooter.Cooldown = 0;
        game.Update(.01f);
        Assert.Equal(ShotKind.Lob, Assert.Single(game.Projectiles).Kind);
        game.Update(.49f);
        Assert.Equal(9, zombie.BodyHp);
        Assert.All(game.Turrets, turret => Assert.Equal(Balance.TurretHp, turret.Hp));
    }

    [Theory]
    [InlineData(ShotKind.Pellet)]
    [InlineData(ShotKind.Lob)]
    public void FriendlyPassThroughDoesNotLetProjectilesIgnoreSolidBlocks(ShotKind kind)
    {
        var game = Battle();
        Assert.True(game.Inventory.Add(Material.Stone, 3));
        for (int y = 16; y <= 18; y++) Assert.True(game.BuildBarrier(new Cell(13, y, 14), Material.Stone));
        var zombie = Begin(game, new Cell(14, 16, 14));
        game.Projectiles.Add(new Projectile { Kind = kind, Damage = 3, Origin = new Cell(12, 16, 14).Center,
            Target = zombie.Position, TargetId = zombie.Id, Duration = .2f });
        game.Update(.25f);
        Assert.Equal(10, zombie.BodyHp);
        Assert.Empty(game.Projectiles);
        Assert.All(game.World.Barriers, block => Assert.Equal(500, block.Hp));
    }

    [Theory]
    [InlineData(ShotKind.Pellet)]
    [InlineData(ShotKind.Lob)]
    public void AProjectileCannotEscapeASolidNowOccupyingItsOriginCell(ShotKind shot)
    {
        var game = Battle();
        var origin = new Cell(12, 16, 14);
        Assert.True(game.Inventory.Add(Material.Stone));
        Assert.True(game.BuildBarrier(origin, Material.Stone));
        var zombie = Begin(game, new Cell(14, 16, 14));
        // Model an in-flight shot whose original shooter cell is now solid.
        game.Projectiles.Add(new Projectile { Kind = shot, Damage = 3, Origin = origin.Center,
            Target = zombie.Position, TargetId = zombie.Id, Duration = .2f });
        game.Update(.01f);
        Assert.Empty(game.Projectiles);
        Assert.Equal(10, zombie.BodyHp);
    }

    [Theory]
    [InlineData(ShotKind.Pellet, ArmorSlot.Chestplate)]
    [InlineData(ShotKind.Lob, ArmorSlot.Helmet)]
    public void ActualProjectilesCarryTheirAttackTypeToTheEligibleArmor(ShotKind shot, ArmorSlot expectedArmor)
    {
        var game = Battle(ZombieKind.Armored);
        var zombie = Begin(game, new Cell(14, 16, 15));
        zombie.FaceToward(Vector3.UnitX); // Face the core; this attack arrives from behind.
        game.Projectiles.Add(new Projectile { Kind = shot, Damage = 3, Origin = zombie.Position - Vector3.UnitX * 3,
            Target = zombie.Position, TargetId = zombie.Id, Duration = .2f });
        game.Update(.25f);
        Assert.Equal(10, zombie.BodyHp);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(piece.Slot == expectedArmor ? 7 : 10, piece.Hp));
    }

    [Fact]
    public void ADescendingLobUsesItsImpactAngleInsteadOfTheShootersHorizontalBearing()
    {
        var game = Battle(ZombieKind.Armored);
        // The lob comes from the zombie's current forward bearing but descends
        // more than 30 degrees. Complete the hit before the next movement slice.
        var zombie = Begin(game, new Cell(8, 16, 8));
        zombie.FaceToward(-Vector3.UnitZ);
        var origin = zombie.Position - Vector3.UnitZ * 2;
        game.Projectiles.Add(new Projectile { Kind = ShotKind.Lob, Damage = 3, Origin = origin,
            Target = zombie.Position, TargetId = zombie.Id, Duration = .2f, Age = .15f });
        game.Update(.05f);
        Assert.Equal(10, zombie.ArmorPieces.Single(p => p.Slot == ArmorSlot.Shield).Hp);
        Assert.Equal(7, zombie.ArmorPieces.Single(p => p.Slot == ArmorSlot.Helmet).Hp);
        Assert.Equal(10, zombie.BodyHp);
    }

    private static GameSession Battle(ZombieKind kind = ZombieKind.Basic)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Count = 1, Kind = kind, Tier = GearTier.Wood }] }] });
        return new GameSession(levels: levels);
    }

    private static Zombie Begin(GameSession game, Cell position)
    {
        Assert.True(game.StartLevel());
        game.Update(.01f);
        game.Projectiles.Clear();
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = position.Center;
        zombie.NextCell = null;
        zombie.Cooldown = 10000;
        return zombie;
    }
}
