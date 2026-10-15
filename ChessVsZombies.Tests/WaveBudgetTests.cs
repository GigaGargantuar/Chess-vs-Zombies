using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class WaveBudgetTests
{
    [Fact]
    public void ArmorConsumesItsActualHpAndBossesUseTheirDiscountedReserve()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition
        {
            HpBudget = 1000,
            Groups = [new() { Kind = ZombieKind.Basic }, new() { Kind = ZombieKind.Armored, Tier = GearTier.Wood }]
        });
        Assert.Equal(970, plan.NormalBudget);
        Assert.Equal(30, plan.BossBudget);
        Assert.Equal(1000, plan.BudgetSpent);
        Assert.Equal(21, plan.Groups.Single(group => !group.IsBoss && group.Kind == ZombieKind.Basic).Count);
        Assert.Equal(19, plan.Groups.Single(group => !group.IsBoss && group.Kind == ZombieKind.Armored).Count);
        var boss = Assert.Single(plan.Groups, group => group.IsBoss);
        Assert.Equal(ZombieSize.Kilo, boss.Size);
        Assert.Equal(3, boss.Count);
        Assert.Equal(1450, plan.TotalHp);
        Assert.Equal(43, plan.SpawnCount);
    }

    [Theory]
    [InlineData(.01f, 1)]
    [InlineData(.03f, 3)]
    [InlineData(.05f, 5)]
    public void ReserveUsesTheConfiguredPercentageWithoutFloatRoundingLosingAPurchase(float fraction, int bosses)
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition
        {
            HpBudget = 1000, BossBudgetFraction = fraction, Groups = [new() { Kind = ZombieKind.Basic }]
        });
        Assert.Equal(bosses * 10, plan.BossBudget);
        Assert.Equal(bosses, plan.Groups.Single(group => group.IsBoss).Count);
        Assert.Equal(1000, plan.BudgetSpent);
        Assert.Equal(1000 + bosses * 30, plan.TotalHp);
    }

    [Fact]
    public void EveryResolvedNormalSpawnPaysItsActualSizeAndGearHp()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition
        {
            HpBudget = 1237,
            Groups =
            [
                new() { Kind = ZombieKind.Basic, Count = 3 },
                new() { Kind = ZombieKind.Basic, Size = ZombieSize.Milli, Count = 2 },
                new() { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Copper, Size = ZombieSize.Milli },
                new() { Kind = ZombieKind.Armored, Tier = GearTier.Stone, Size = ZombieSize.Kilo }
            ]
        });
        long normalSpent = plan.Groups.Where(group => !group.IsBoss).Sum(group =>
            (long)group.Count * LevelCatalog.ZombieMaxHp(group.Kind, group.Tier, group.Size));
        long bossSpent = plan.Groups.Where(group => group.IsBoss).Sum(group => (long)group.Count * 10);
        Assert.InRange(normalSpent, plan.NormalBudget - 4, plan.NormalBudget);
        Assert.InRange(bossSpent, plan.BossBudget - 9, plan.BossBudget);
        Assert.Equal(normalSpent + bossSpent, plan.BudgetSpent);
        Assert.True(plan.BudgetSpent <= plan.HpBudget);
    }

    [Fact]
    public void AnUnaffordableArmorCandidateDoesNotCreateAFreeZombieOrOverspend()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition
        {
            HpBudget = 100,
            Groups = [new() { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Osmium }]
        });
        Assert.Empty(plan.Groups);
        Assert.Equal(0, plan.SpawnCount);
        Assert.Equal(0, plan.TotalHp);
        Assert.Equal(0, plan.BudgetSpent);
    }

    [Fact]
    public void LegacyExactRostersAndLargeCountsDoNotRequireExpandedSpawnLists()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition
        {
            Groups = [new() { Kind = ZombieKind.Basic, Count = int.MaxValue }]
        });
        Assert.Single(plan.Groups);
        Assert.Equal(int.MaxValue, plan.SpawnCount);
        Assert.Equal(10L * int.MaxValue, plan.BudgetSpent);
        Assert.Equal(ZombieKind.Basic, WaveSpawnPlanner.SelectGroup(plan, int.MaxValue - 1).Kind);
    }

    [Fact]
    public void BudgetedWeightsMayExceedTheSpawnCountLimitWithoutExpandingOrOverflowing()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition
        {
            HpBudget = int.MaxValue,
            Groups =
            [
                new() { Kind = ZombieKind.Basic, Count = int.MaxValue },
                new() { Kind = ZombieKind.Armored, Tier = GearTier.Osmium, Count = int.MaxValue }
            ]
        });
        Assert.InRange(plan.Groups.Count, 1, 3);
        Assert.InRange(plan.BudgetSpent, 0, int.MaxValue);
        Assert.InRange(plan.SpawnCount, 1, int.MaxValue);
        Assert.NotNull(WaveSpawnPlanner.SelectGroup(plan, plan.SpawnCount - 1));
    }

    [Fact]
    public void RareBossIsInterleavedWithNormalSpawnsInsteadOfBeingLeftUntilTheEnd()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition
        {
            HpBudget = 1000, BossBudgetFraction = .01f, Groups = [new() { Kind = ZombieKind.Basic }]
        });
        int bossIndex = Enumerable.Range(0, plan.SpawnCount).Single(index => WaveSpawnPlanner.SelectGroup(plan, index).IsBoss);
        Assert.InRange(bossIndex, 1, plan.SpawnCount - 2);
    }

    [Fact]
    public void WoodenGearStartsAtFiveBecomesMoreCommonAtSevenAndDominatesAtNine()
    {
        var catalog = new LevelCatalog();
        for (int level = 1; level < 5; level++)
            Assert.All(WaveSpawnPlanner.Plan(catalog.Get(level).Waves[0]).Groups, group => Assert.Equal(ZombieKind.Basic, group.Kind));
        double Share(int level)
        {
            var groups = WaveSpawnPlanner.Plan(catalog.Get(level).Waves[0]).Groups.Where(group => !group.IsBoss).ToArray();
            Assert.Contains(groups, group => group.Kind == ZombieKind.Basic);
            Assert.All(groups.Where(group => group.Kind != ZombieKind.Basic), group => Assert.Equal(GearTier.Wood, group.Tier));
            return (double)groups.Where(group => group.Kind != ZombieKind.Basic).Sum(group => group.Count) / groups.Sum(group => group.Count);
        }
        Assert.True(Share(5) < Share(7));
        Assert.True(Share(7) < Share(9));
        Assert.True(Share(9) > .5);
    }

    [Theory]
    [InlineData(10, GearTier.Stone)]
    [InlineData(15, GearTier.Copper)]
    [InlineData(20, GearTier.Iron)]
    [InlineData(25, GearTier.Gold)]
    [InlineData(30, GearTier.Diamond)]
    [InlineData(35, GearTier.Osmium)]
    public void StrongestArmorRemainsMostCommonWhileEveryEarlierArmorTierStillAppears(int level, GearTier strongest)
    {
        var groups = WaveSpawnPlanner.Plan(new LevelCatalog().Get(level).Waves[0]).Groups
            .Where(group => !group.IsBoss && group.Kind is ZombieKind.Armored or ZombieKind.ArmedArmored).ToArray();
        int strongestCount = groups.Where(group => group.Tier == strongest).Sum(group => group.Count);
        for (int tier = 0; tier < (int)strongest; tier++)
        {
            int count = groups.Where(group => group.Tier == (GearTier)tier).Sum(group => group.Count);
            Assert.InRange(count, 1, strongestCount - 1);
        }
    }

    [Fact]
    public void CatalogClonesAllBudgetAndSizeAuthoringFields()
    {
        var catalog = new LevelCatalog();
        var authored = new LevelDefinition
        {
            Waves = [new() { HpBudget = 1000, BossBudgetFraction = .05f,
                Groups = [new() { Size = ZombieSize.Milli }, new() { Size = ZombieSize.Kilo, IsBoss = true }] }]
        };
        catalog.Set(authored);
        authored.Waves[0].HpBudget = 1;
        authored.Waves[0].Groups[0].Size = ZombieSize.Standard;
        var wave = catalog.Get(1).Waves[0];
        Assert.Equal(1000, wave.HpBudget);
        Assert.Equal(.05f, wave.BossBudgetFraction);
        Assert.Equal(ZombieSize.Milli, wave.Groups[0].Size);
        Assert.True(wave.Groups[1].IsBoss);
    }

    [Theory]
    [InlineData(0, .03f)]
    [InlineData(-1, .03f)]
    [InlineData(100, 0f)]
    [InlineData(100, .06f)]
    [InlineData(100, float.NaN)]
    public void InvalidBudgetDefinitionsAreRejected(int budget, float reserve)
        => Assert.Throws<InvalidDataException>(() => WaveSpawnPlanner.Plan(new WaveDefinition
            { HpBudget = budget, BossBudgetFraction = reserve, Groups = [new()] }));
}
