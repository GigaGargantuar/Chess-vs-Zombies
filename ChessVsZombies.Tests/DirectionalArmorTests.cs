using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class DirectionalArmorTests
{
    [Theory]
    [InlineData(ShotKind.Pellet, -1, ArmorSlot.Helmet)]
    [InlineData(ShotKind.Pellet, 0, ArmorSlot.Chestplate)]
    [InlineData(ShotKind.Pellet, 1, ArmorSlot.Boots)]
    [InlineData(ShotKind.Lob, -1, ArmorSlot.Helmet)]
    [InlineData(ShotKind.Lob, 0, ArmorSlot.Chestplate)]
    [InlineData(ShotKind.Lob, 1, ArmorSlot.Boots)]
    public void ProjectileTravelSelectsOneArmorPieceBeforeOverflowingIntoTheBody(ShotKind shot, float vertical, ArmorSlot expected)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 13, shot == ShotKind.Pellet ? DamageKind.Pierce : DamageKind.Blunt,
            shot, new Vector3(1, vertical, 0));

        Assert.Equal(7, zombie.BodyHp);
        Assert.DoesNotContain(zombie.ArmorPieces, piece => piece.Slot == expected);
        Assert.Equal(3, zombie.ArmorPieces.Count);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(10, piece.Hp));
    }

    [Theory]
    [InlineData(.1f, .2f, ArmorSlot.Boots)]
    [InlineData(.6f, .8f, ArmorSlot.Helmet)]
    public void ARealLobUsesItsTravelDirectionWhereItHitsAnotherBody(float startAge, float contactAge, ArmorSlot expected)
    {
        var game = new GameSession();
        var zombie = Armored();
        var projectile = new Projectile
        {
            Origin = new Vector3(10.5f, 20.5f, 10.5f), Target = new Vector3(18.5f, 20.5f, 10.5f),
            Kind = ShotKind.Lob, Damage = 3, Duration = 1, Age = startAge, TargetId = 123
        };
        zombie.Position = projectile.GetPositionAt(contactAge);
        game.Zombies.Add(zombie);
        game.Projectiles.Add(projectile);

        typeof(GameSession).GetMethod("UpdateProjectiles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game, [contactAge - startAge]);

        Assert.Empty(game.Projectiles);
        Assert.Equal(10, zombie.BodyHp);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(piece.Slot == expected ? 7 : 10, piece.Hp));
    }

    [Theory]
    [InlineData(-1, ArmorSlot.Helmet)]
    [InlineData(1, ArmorSlot.Boots)]
    public void ARealPelletUsesItsImpactTravelDirection(float vertical, ArmorSlot expected)
    {
        var game = new GameSession();
        var zombie = Armored();
        zombie.Position = new Vector3(10.5f, 20.5f, 10.5f);
        game.Zombies.Add(zombie);
        game.Projectiles.Add(new Projectile
        {
            Origin = zombie.Position - new Vector3(3, vertical * 3, 0),
            Target = zombie.Position, Kind = ShotKind.Pellet, Damage = 3, Duration = .1f
        });

        typeof(GameSession).GetMethod("UpdateProjectiles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game, [.1f]);

        Assert.Empty(game.Projectiles);
        Assert.Equal(10, zombie.BodyHp);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(piece.Slot == expected ? 7 : 10, piece.Hp));
    }

    [Theory]
    [InlineData(ShotKind.Pellet, .000001f)]
    [InlineData(ShotKind.Pellet, 1f)]
    [InlineData(ShotKind.Pellet, 1000000f)]
    [InlineData(ShotKind.Pellet, float.MaxValue)]
    [InlineData(ShotKind.Lob, .000001f)]
    [InlineData(ShotKind.Lob, 1f)]
    [InlineData(ShotKind.Lob, 1000000f)]
    [InlineData(ShotKind.Lob, float.MaxValue)]
    public void ProjectileArmorClassificationIsIndependentOfDirectionMagnitude(ShotKind shot, float magnitude)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 3, shot == ShotKind.Pellet ? DamageKind.Pierce : DamageKind.Blunt, shot,
            new Vector3(magnitude, -magnitude, 0));

        Assert.Equal(7, zombie.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Helmet).Hp);
        Assert.Equal(10, zombie.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Chestplate).Hp);
        Assert.Equal(10, zombie.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Boots).Hp);
    }

    [Theory]
    [InlineData(ShotKind.Pellet, -.005f, ArmorSlot.Helmet)]
    [InlineData(ShotKind.Pellet, .005f, ArmorSlot.Boots)]
    [InlineData(ShotKind.Pellet, -float.Epsilon, ArmorSlot.Helmet)]
    [InlineData(ShotKind.Pellet, float.Epsilon, ArmorSlot.Boots)]
    [InlineData(ShotKind.Lob, -.005f, ArmorSlot.Helmet)]
    [InlineData(ShotKind.Lob, .005f, ArmorSlot.Boots)]
    [InlineData(ShotKind.Lob, -float.Epsilon, ArmorSlot.Helmet)]
    [InlineData(ShotKind.Lob, float.Epsilon, ArmorSlot.Boots)]
    public void NearlyLevelProjectilesStillRespectWhetherTheyCameFromAboveOrBelow(ShotKind shot, float vertical, ArmorSlot expected)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 3, shot == ShotKind.Pellet ? DamageKind.Pierce : DamageKind.Blunt,
            shot, new Vector3(1, vertical, 0));

        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(piece.Slot == expected ? 7 : 10, piece.Hp));
    }

    [Theory]
    [InlineData(ShotKind.Pellet, -.2f, ArmorSlot.Helmet)]
    [InlineData(ShotKind.Pellet, .2f, ArmorSlot.Boots)]
    [InlineData(ShotKind.Lob, -.2f, ArmorSlot.Helmet)]
    [InlineData(ShotKind.Lob, .2f, ArmorSlot.Boots)]
    public void FrontalShieldStillAbsorbsBeforeDirectionallyMatchingArmor(ShotKind shot, float vertical, ArmorSlot expected)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 13, shot == ShotKind.Pellet ? DamageKind.Pierce : DamageKind.Blunt,
            shot, new Vector3(0, vertical, -1));

        Assert.DoesNotContain(zombie.ArmorPieces, piece => piece.Slot == ArmorSlot.Shield);
        Assert.Equal(10, zombie.BodyHp);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(piece.Slot == expected ? 7 : 10, piece.Hp));
    }

    [Theory]
    [InlineData(ShotKind.Pellet)]
    [InlineData(ShotKind.Lob)]
    public void MissingBootsDoNotRedirectAnUnderneathProjectileIntoAChestplate(ShotKind shot)
    {
        var zombie = Armored();
        var game = new GameSession();
        var kind = shot == ShotKind.Pellet ? DamageKind.Pierce : DamageKind.Blunt;
        game.DamageZombie(zombie, 10, kind, shot, Vector3.UnitY);

        game.DamageZombie(zombie, 3, kind, shot, Vector3.UnitY);

        Assert.Equal(7, zombie.BodyHp);
        Assert.Equal(10, zombie.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Chestplate).Hp);
        Assert.Equal(10, zombie.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Helmet).Hp);
    }

    [Fact]
    public void OsmiumBootsAddTheirOwnDurabilityAndHeatProtection()
    {
        var zombie = new Zombie { Kind = ZombieKind.Armored, Tier = GearTier.Osmium };

        Assert.Equal(Balance.ZombieHp + 4 * Balance.GearHp(GearTier.Osmium), zombie.MaxHp);
        Assert.Equal(zombie.MaxHp, zombie.Hp);
        Assert.Equal(4, zombie.ArmorPieces.Count);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(210, piece.Hp));
        Assert.Equal(40, zombie.ArmorPieces.Sum(piece => piece.HeatProtectionRemaining));
    }

    private static Zombie Armored() => new() { Id = 99, WaveIndex = -1, Kind = ZombieKind.Armored, Tier = GearTier.Wood };
}
