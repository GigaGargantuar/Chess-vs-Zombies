using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class ArmorHitTests
{
    [Theory]
    [InlineData(ShotKind.Pellet, DamageKind.Pierce, ArmorSlot.Helmet)]
    [InlineData(ShotKind.Lob, DamageKind.Blunt, ArmorSlot.Chestplate)]
    public void FrontHitUsesShieldThenMatchingArmorAndBypassesTheOtherPiece(ShotKind shot, DamageKind kind, ArmorSlot survivor)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 25, kind, shot, -Vector3.UnitZ);

        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(15, zombie.Hp);
        Assert.Equal(survivor, Assert.Single(zombie.ArmorPieces).Slot);
        Assert.Equal(10, Assert.Single(zombie.ArmorPieces).Hp);
    }

    [Theory]
    [InlineData(ShotKind.Pellet, DamageKind.Pierce, ArmorSlot.Chestplate)]
    [InlineData(ShotKind.Lob, DamageKind.Blunt, ArmorSlot.Helmet)]
    public void RearHitUsesOnlyTheMatchingArmorAndThenBody(ShotKind shot, DamageKind kind, ArmorSlot removed)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 15, kind, shot, Vector3.UnitZ);

        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(25, zombie.Hp);
        Assert.Equal(2, zombie.ArmorPieces.Count);
        Assert.DoesNotContain(zombie.ArmorPieces, piece => piece.Slot == removed);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(10, piece.Hp));
    }

    [Fact]
    public void RepeatedPelletsCannotUseTheHelmetAfterTheChestplateFallsOff()
    {
        var zombie = Armored();
        var game = new GameSession();
        game.DamageZombie(zombie, 10, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitZ);

        game.DamageZombie(zombie, 3, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitZ);

        Assert.Equal(7, zombie.BodyHp);
        Assert.Equal(new[] { ArmorSlot.Shield, ArmorSlot.Helmet }, zombie.ArmorPieces.Select(piece => piece.Slot));
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(10, piece.Hp));
    }

    [Fact]
    public void FrontalMeleeOverflowsShieldStraightIntoBody()
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 15, DamageKind.Blunt, ShotKind.Melee, -Vector3.UnitZ);

        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(new[] { ArmorSlot.Chestplate, ArmorSlot.Helmet }, zombie.ArmorPieces.Select(piece => piece.Slot));
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(10, piece.Hp));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(45, 0, true)]
    [InlineData(-45, 0, true)]
    [InlineData(0, 30, true)]
    [InlineData(0, -30, true)]
    [InlineData(45, 30, true)]
    [InlineData(-45, -30, true)]
    [InlineData(45.1, 0, false)]
    [InlineData(-45.1, 0, false)]
    [InlineData(0, 30.1, false)]
    [InlineData(0, -30.1, false)]
    [InlineData(180, 0, false)]
    [InlineData(0, 90, false)]
    public void ShieldCoverageUsesIndependentInclusiveHorizontalAndVerticalWindows(double yaw, double elevation, bool blocked)
    {
        var zombie = Armored();
        var radiansYaw = yaw * Math.PI / 180;
        var radiansElevation = elevation * Math.PI / 180;
        var sourceDirection = new Vector3(
            (float)(Math.Sin(radiansYaw) * Math.Cos(radiansElevation)),
            (float)Math.Sin(radiansElevation),
            (float)(Math.Cos(radiansYaw) * Math.Cos(radiansElevation)));

        new GameSession().DamageZombie(zombie, 3, DamageKind.Blunt, ShotKind.Melee, -sourceDirection);

        Assert.Equal(blocked ? 10 : 7, zombie.BodyHp);
        Assert.Equal(blocked ? 7 : 10, ArmorHp(zombie, ArmorSlot.Shield));
        Assert.Equal(10, ArmorHp(zombie, ArmorSlot.Chestplate));
        Assert.Equal(10, ArmorHp(zombie, ArmorSlot.Helmet));
    }

    [Theory]
    [InlineData(.000001f)]
    [InlineData(1000000f)]
    [InlineData(float.MaxValue)]
    public void ShieldCoverageDoesNotDependOnTravelDirectionMagnitude(float magnitude)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 3, DamageKind.Blunt, ShotKind.Melee, -Vector3.UnitZ * magnitude);

        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(7, ArmorHp(zombie, ArmorSlot.Shield));
    }

    [Fact]
    public void TurningRotatesShieldCoverageWithTheHorizontalBodyFacing()
    {
        var zombie = Armored();
        var game = new GameSession();
        zombie.FaceToward(new Vector3(4, 20, 0));
        Assert.Equal(Vector3.UnitX, zombie.Facing);

        game.DamageZombie(zombie, 3, DamageKind.Blunt, ShotKind.Melee, -Vector3.UnitX);
        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(7, ArmorHp(zombie, ArmorSlot.Shield));

        game.DamageZombie(zombie, 3, DamageKind.Blunt, ShotKind.Melee, -Vector3.UnitZ);
        Assert.Equal(7, zombie.BodyHp);
        Assert.Equal(7, ArmorHp(zombie, ArmorSlot.Shield));
    }

    [Fact]
    public void FacingNormalizesOnlyTheHorizontalDirectionAndIgnoresInvalidOrVerticalDirections()
    {
        var zombie = Armored();
        Assert.Equal(Vector3.UnitZ, zombie.Facing);
        zombie.FaceToward(new Vector3(6, 100, 8));
        Assert.Equal(new Vector3(.6f, 0, .8f), zombie.Facing);
        zombie.FaceToward(Vector3.Zero);
        zombie.FaceToward(Vector3.UnitY);
        zombie.FaceToward(new Vector3(float.NaN, 0, 0));
        zombie.FaceToward(new Vector3(0, float.PositiveInfinity, 1));
        Assert.Equal(new Vector3(.6f, 0, .8f), zombie.Facing);

        zombie.FaceToward(new Vector3(float.MaxValue, 0, float.MaxValue));
        Assert.Equal(0, zombie.Facing.Y);
        Assert.InRange(zombie.Facing.Length(), .99999f, 1.00001f);
        Assert.InRange(zombie.Facing.X, .70710f, .70711f);
        Assert.Equal(zombie.Facing.X, zombie.Facing.Z);
    }

    [Fact]
    public void MissingShotDefaultsPierceToPelletAndBluntToMeleeWithoutGrantingShieldCoverage()
    {
        var pierced = Armored();
        var struck = Armored();
        var game = new GameSession();

        game.DamageZombie(pierced, 3, DamageKind.Pierce);
        game.DamageZombie(struck, 3, DamageKind.Blunt);

        Assert.Equal(10, pierced.BodyHp);
        Assert.Equal(7, ArmorHp(pierced, ArmorSlot.Chestplate));
        Assert.Equal(10, ArmorHp(pierced, ArmorSlot.Shield));
        Assert.Equal(7, struck.BodyHp);
        Assert.All(struck.ArmorPieces, piece => Assert.Equal(10, piece.Hp));
    }

    [Fact]
    public void UndirectedZeroVerticalAndInvalidDirectionsBypassTheShield()
    {
        Vector3?[] directions = [null, Vector3.Zero, Vector3.UnitY, new(float.NaN, 0, -1), new(0, 0, float.NegativeInfinity)];
        foreach (var direction in directions)
        {
            var zombie = Armored();
            new GameSession().DamageZombie(zombie, 3, DamageKind.Blunt, ShotKind.Melee, direction);
            Assert.Equal(7, zombie.BodyHp);
            Assert.All(zombie.ArmorPieces, piece => Assert.Equal(10, piece.Hp));
        }
    }

    [Theory]
    [InlineData(DamageKind.Chemical)]
    [InlineData(DamageKind.Thermal)]
    public void UnresistedExposureBypassesArmorEvenWithFrontalPhysicalDeliveryMetadata(DamageKind kind)
    {
        var zombie = Armored();

        new GameSession().DamageZombie(zombie, 3, kind, ShotKind.Pellet, -Vector3.UnitZ);

        Assert.Equal(7, zombie.BodyHp);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(10, piece.Hp));
    }

    [Fact]
    public void AssigningTotalHpKeepsSequentialCompatibilityIndependentOfTypedHitCoverage()
    {
        var zombie = new Zombie { Kind = ZombieKind.Armored, Tier = GearTier.Wood, Hp = 29 };
        Assert.Equal(29, zombie.Hp);
        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(new[] { ArmorSlot.Chestplate, ArmorSlot.Helmet }, zombie.ArmorPieces.Select(piece => piece.Slot));
        Assert.Equal(9, ArmorHp(zombie, ArmorSlot.Chestplate));
        Assert.Equal(10, ArmorHp(zombie, ArmorSlot.Helmet));

        zombie.Hp = 40;
        Assert.Equal(40, zombie.Hp);
        Assert.Equal(3, zombie.ArmorPieces.Count);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(10, piece.Hp));
        zombie.Hp = 0;
        Assert.Equal(0, zombie.Hp);
        Assert.Equal(0, zombie.BodyHp);
        Assert.Empty(zombie.ArmorPieces);
    }

    [Theory]
    [InlineData(-1, 16)]
    [InlineData(0, 16)]
    [InlineData(1, 0)]
    [InlineData(2, -16)]
    [InlineData(3, -16)]
    public void LobTravelDirectionUsesTheArcDerivativeAtClampedAge(float age, float vertical)
    {
        var projectile = new Projectile { Origin = Vector3.Zero, Target = new Vector3(10, 0, 0), Kind = ShotKind.Lob, Duration = 2 };

        Assert.Equal(new Vector3(10, vertical, 0), projectile.GetTravelDirection(age));
    }

    [Fact]
    public void PelletTravelDirectionStaysOnItsStraightLine()
    {
        var projectile = new Projectile { Origin = new Vector3(3, 8, 2), Target = new Vector3(-2, 4, 9), Kind = ShotKind.Pellet, Duration = 1 };

        Assert.Equal(new Vector3(-5, -4, 7), projectile.GetTravelDirection(0));
        Assert.Equal(new Vector3(-5, -4, 7), projectile.GetTravelDirection(1));
    }

    private static Zombie Armored() => new() { Kind = ZombieKind.Armored, Tier = GearTier.Wood, WaveIndex = -1 };
    private static float ArmorHp(Zombie zombie, ArmorSlot slot) => Assert.Single(zombie.ArmorPieces, piece => piece.Slot == slot).Hp;
}
