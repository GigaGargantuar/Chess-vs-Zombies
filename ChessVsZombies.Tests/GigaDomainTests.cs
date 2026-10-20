using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GigaDomainTests
{
    [Fact]
    public void InitializationUsesHalfTheOriginalPoolAndTheNearestEligibleIntegerCounter()
    {
        Assert.Equal(10, GigaRules.SelectInitialCounter(1));
        Assert.Equal(10, GigaRules.SelectInitialCounter(0));
        for (int counter = 10; counter < 30; counter++)
        {
            Assert.Equal(counter, GigaRules.SelectInitialCounter(2 * GigaRules.MaximumHp(counter)));
            double midpointWave = GigaRules.MaximumHp(counter) + GigaRules.MaximumHp(counter + 1);
            Assert.Equal(counter, GigaRules.SelectInitialCounter(midpointWave));
            Assert.Equal(counter + 1, GigaRules.SelectInitialCounter(midpointWave + .01));
        }

        var zombie = new Zombie { Size = ZombieSize.Giga };
        zombie.InitializeGiga(20_000);
        Assert.Equal(GigaRules.SelectInitialCounter(20_000), zombie.Giga!.InitialCounter);
        Assert.Equal(zombie.Giga.HpCounter, zombie.Giga.ResCounter);
        Assert.Equal(1, zombie.Giga.AttackCounter);
        Assert.Equal(zombie.MaxHp, zombie.Hp);
        Assert.Equal((long)Math.Ceiling(GigaRules.MaximumHp(zombie.Giga.InitialCounter)), zombie.Giga.SpawnCost);
        Assert.Equal(523, GigaRules.SpawnCost(10));
    }

    [Theory]
    [InlineData(ZombieKind.Basic)]
    [InlineData(ZombieKind.ArmedArmored)]
    public void GigaAlwaysHasACubeBodyAndNoEquipmentRegardlessOfLegacyKind(ZombieKind kind)
    {
        var zombie = new Zombie { Kind = kind, Size = ZombieSize.Giga, Tier = GearTier.Osmium, CarriesBucket = true };
        Assert.Equal(5, (int)ZombieSize.Giga);
        Assert.True(zombie.IsGiga);
        Assert.True(zombie.IsGiant);
        Assert.Equal(4, zombie.GridFootprint);
        Assert.Equal(4, zombie.BodyScale);
        Assert.Equal(new Vector3(2), zombie.BodyHalfExtents);
        Assert.Equal(2, zombie.StandingHeight);
        Assert.Empty(zombie.ArmorPieces);
        Assert.False(zombie.HasWeapon);
        Assert.False(zombie.CarriesBucket);
        Assert.Equal(0, zombie.BucketCount);
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Equal(new Zombie().MovementSpeed * .2f, zombie.MovementSpeed, 6);
        var cell = new Cell(5, 16, 8);
        zombie.Position = zombie.StandAt(cell);
        Assert.Equal(cell, zombie.Cell);
        Assert.Equal(new Vector3(cell.X, cell.Y, cell.Z), zombie.Position - zombie.BodyHalfExtents);
        Assert.Equal(zombie.BodyHp, zombie.Hp);
    }

    [Fact]
    public void ThresholdEqualityBlocksTheWholeHitButStillWearsDownResistance()
    {
        var zombie = Giga();
        float hp = zombie.Hp;
        float resistance = zombie.Giga!.Resistance;
        Assert.Equal((float)Enumerable.Range(1, 10).Sum(k => Math.Log(k, Math.PI)), resistance);

        Hit(zombie, resistance);

        Assert.Equal(hp, zombie.Hp);
        Assert.Equal(9, zombie.Giga.ResCounter);
        Assert.Equal(10, zombie.Giga.HpCounter);
    }

    [Fact]
    public void DamageAboveResistancePenetratesInFullWithoutGrowingHpAtHigherResistance()
    {
        var zombie = Giga();
        float hp = zombie.Hp;
        float damage = zombie.Giga!.Resistance + 1;

        Hit(zombie, damage);

        Assert.Equal(hp - damage, zombie.Hp);
        Assert.Equal(10, zombie.Giga.HpCounter);
        Assert.Equal(9, zombie.Giga.ResCounter);
    }

    [Fact]
    public void GrowthUsesThePreHitResistanceAndPreservesTheSurvivingPostDamageHpFraction()
    {
        var zombie = Giga(resCounter: 2);
        float originalMaximum = zombie.MaxHp;
        Hit(zombie, 1);
        Assert.Equal(1, zombie.Giga!.ResCounter);
        Assert.Equal(10, zombie.Giga.HpCounter);
        Assert.Equal(originalMaximum - 1, zombie.Hp);

        float afterDamage = zombie.Hp - 5;
        Hit(zombie, 5);

        Assert.Equal(11, zombie.Giga.HpCounter);
        Assert.Equal(1, zombie.Giga.ResCounter);
        Assert.Equal((float)((double)afterDamage * zombie.MaxHp / originalMaximum), zombie.Hp);
        Assert.Equal(afterDamage / originalMaximum, zombie.Hp / zombie.MaxHp, 6);
    }

    [Fact]
    public void LethalDamageCannotBeRescuedByMaximumHpGrowth()
    {
        var zombie = Giga(resCounter: 1);

        Hit(zombie, zombie.Hp);

        Assert.Equal(0, zombie.Hp);
        Assert.Equal(10, zombie.Giga!.HpCounter);
        Assert.Equal(1, zombie.Giga.ResCounter);
    }

    [Fact]
    public void EveryHitIncludingBlockedAndZeroDamageResetsTheWholeRecoveryTimer()
    {
        var zombie = Giga();
        zombie.Giga!.AdvanceRecovery(.75f);
        Hit(zombie, 0);
        Assert.Equal(9, zombie.Giga.ResCounter);
        Assert.Equal(0, zombie.Giga.RecoverySeconds);
        zombie.Giga.AdvanceRecovery(.75f);
        Assert.Equal(9, zombie.Giga.ResCounter);
        Hit(zombie, 1); // RES still blocks this, but resets the next full second.
        Assert.Equal(8, zombie.Giga.ResCounter);
        zombie.Giga.AdvanceRecovery(.5f);
        Assert.Equal(8, zombie.Giga.ResCounter);
        zombie.Giga.AdvanceRecovery(.5f);
        Assert.Equal(9, zombie.Giga.ResCounter);
        Assert.Equal(0, zombie.Giga.RecoverySeconds);
        Assert.Equal(zombie.MaxHp, zombie.Hp);
    }

    [Fact]
    public void RecoveryHasNoInitialCounterCapAndDoesNotChangeHpOrAttack()
    {
        var zombie = Giga();
        float maximum = zombie.MaxHp;

        zombie.Giga!.AdvanceRecovery(3.25f);

        Assert.Equal(13, zombie.Giga.ResCounter);
        Assert.Equal(.25, zombie.Giga.RecoverySeconds);
        Assert.Equal((float)Enumerable.Range(1, 13).Sum(k => Math.Log(k, Math.PI)), zombie.Giga.Resistance);
        Assert.Equal(10, zombie.Giga.HpCounter);
        Assert.Equal(1, zombie.Giga.AttackCounter);
        Assert.Equal(maximum, zombie.MaxHp);
        Assert.Equal(maximum, zombie.Hp);
    }

    [Fact]
    public void AttackProgressionUsesPhiAndItsLogarithmicBlastRadiusAndResetsAfterLockedTargetKill()
    {
        var zombie = Giga();
        for (int counter = 1; counter <= 8; counter++)
        {
            Assert.Equal(counter, zombie.Giga!.AttackCounter);
            Assert.Equal((float)Math.Pow(GigaRules.GoldenRatio, counter), zombie.AttackDamage);
            Assert.Equal(MathF.Log10(zombie.AttackDamage), zombie.Giga.BlastRadius, 6);
            zombie.Giga.RecordSuccessfulAttack(false);
        }
        zombie.Giga!.RecordSuccessfulAttack(true);
        Assert.Equal(1, zombie.Giga.AttackCounter);
        Assert.Equal(10, zombie.Giga.HpCounter);
        Assert.Equal(10, zombie.Giga.ResCounter);
    }

    [Fact]
    public void RestoredCountersRemainIndependentAndAdministrativeHpAssignmentDoesNotCountAsAHit()
    {
        var zombie = Giga();
        zombie.RestoreGigaState(10, 12, 14, 7, .7);
        zombie.Hp = zombie.MaxHp / 2;
        Assert.Equal(12, zombie.Giga!.HpCounter);
        Assert.Equal(14, zombie.Giga.ResCounter);
        Assert.Equal(7, zombie.Giga.AttackCounter);
        Assert.Equal(.7, zombie.Giga.RecoverySeconds);
        zombie.Hp = zombie.MaxHp;
        Assert.Equal(zombie.MaxHp, zombie.Hp);
        Assert.Empty(zombie.ArmorPieces);
    }

    private static Zombie Giga(int resCounter = 10)
    {
        var zombie = new Zombie { Size = ZombieSize.Giga, Kind = ZombieKind.Armored };
        if (resCounter != 10) zombie.RestoreGigaState(10, 10, resCounter, 1, 0);
        return zombie;
    }

    private static void Hit(Zombie zombie, float damage) => typeof(Zombie)
        .GetMethod("ApplyDamage", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(zombie, [damage, DamageKind.Pierce, (ShotKind?)ShotKind.Pellet, null]);
}
