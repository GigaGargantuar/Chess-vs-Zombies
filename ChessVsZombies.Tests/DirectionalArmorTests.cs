using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class DirectionalArmorTests
{
    [Theory]
    [InlineData(-1, ArmorSlot.Helmet)]
    [InlineData(0, ArmorSlot.Chestplate)]
    [InlineData(1, ArmorSlot.Boots)]
    public void PelletTravelSelectsOneArmorPieceBeforeOverflowingIntoTheBody(float vertical, ArmorSlot expected)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 13, DamageKind.Pierce, ShotKind.Pellet, new Vector3(1, vertical, 0));

        Assert.Equal(7, zombie.BodyHp);
        Assert.DoesNotContain(zombie.ArmorPieces, piece => piece.Slot == expected);
        Assert.Equal(3, zombie.ArmorPieces.Count);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(10, piece.Hp));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void LobStillUsesTheHelmetEvenOnTheRisingPartOfItsArc(float vertical)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 3, DamageKind.Blunt, ShotKind.Lob, new Vector3(1, vertical, 0));

        Assert.Equal(10, zombie.BodyHp);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(piece.Slot == ArmorSlot.Helmet ? 7 : 10, piece.Hp));
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
    [InlineData(.000001f)]
    [InlineData(1f)]
    [InlineData(1000000f)]
    [InlineData(float.MaxValue)]
    public void PelletArmorClassificationIsIndependentOfDirectionMagnitude(float magnitude)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 3, DamageKind.Pierce, ShotKind.Pellet,
            new Vector3(magnitude, -magnitude, 0));

        Assert.Equal(7, zombie.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Helmet).Hp);
        Assert.Equal(10, zombie.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Chestplate).Hp);
        Assert.Equal(10, zombie.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Boots).Hp);
    }

    [Theory]
    [InlineData(-.005f, ArmorSlot.Helmet)]
    [InlineData(.005f, ArmorSlot.Boots)]
    [InlineData(-float.Epsilon, ArmorSlot.Helmet)]
    [InlineData(float.Epsilon, ArmorSlot.Boots)]
    public void NearlyLevelPelletsStillRespectWhetherTheyCameFromAboveOrBelow(float vertical, ArmorSlot expected)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 3, DamageKind.Pierce, ShotKind.Pellet, new Vector3(1, vertical, 0));

        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(piece.Slot == expected ? 7 : 10, piece.Hp));
    }

    [Theory]
    [InlineData(-.2f, ArmorSlot.Helmet)]
    [InlineData(.2f, ArmorSlot.Boots)]
    public void FrontalShieldStillAbsorbsBeforeDirectionallyMatchingArmor(float vertical, ArmorSlot expected)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 13, DamageKind.Pierce, ShotKind.Pellet, new Vector3(0, vertical, -1));

        Assert.DoesNotContain(zombie.ArmorPieces, piece => piece.Slot == ArmorSlot.Shield);
        Assert.Equal(10, zombie.BodyHp);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(piece.Slot == expected ? 7 : 10, piece.Hp));
    }

    [Fact]
    public void MissingBootsDoNotRedirectAnUnderneathPelletIntoAChestplate()
    {
        var zombie = Armored();
        var game = new GameSession();
        game.DamageZombie(zombie, 10, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitY);

        game.DamageZombie(zombie, 3, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitY);

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
