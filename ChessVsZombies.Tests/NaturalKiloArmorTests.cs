using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class NaturalKiloArmorTests
{
    [Theory]
    [InlineData(1, null)]
    [InlineData(4, null)]
    [InlineData(5, GearTier.Wood)]
    [InlineData(9, GearTier.Wood)]
    [InlineData(10, GearTier.Stone)]
    [InlineData(15, GearTier.Copper)]
    [InlineData(20, GearTier.Iron)]
    [InlineData(25, GearTier.Gold)]
    [InlineData(30, GearTier.Diamond)]
    [InlineData(35, GearTier.Osmium)]
    [InlineData(100, GearTier.Osmium)]
    public void GeneratedBossesWearTheStrongestUnlockedArmorWithoutChangingTheirReserveCost(int level, GearTier? tier)
    {
        var definition = Assert.Single(new LevelCatalog().Get(level).Waves);
        var authoredBoss = Assert.Single(definition.Groups, group => group.IsBoss && group.Size == ZombieSize.Kilo);
        Assert.Equal(tier, LevelCatalog.HighestAvailableArmorTier(level));
        Assert.Equal(tier.HasValue ? ZombieKind.Armored : ZombieKind.Basic, authoredBoss.Kind);
        Assert.Equal(tier ?? GearTier.Wood, authoredBoss.Tier);
        Assert.Equal(ZombieSize.Kilo, authoredBoss.Size);
        Assert.Equal(10, WaveSpawnPlan.BudgetCost(authoredBoss));

        var plan = WaveSpawnPlanner.Plan(definition);
        Assert.InRange(plan.BudgetSpent, 0, plan.HpBudget);
        Assert.Equal(plan.Groups.Sum(group => (float)group.Count *
            LevelCatalog.ZombieMaxHp(group.Kind, group.Tier, group.Size)), plan.TotalHp);
        Assert.All(plan.Groups.Where(group => group.IsBoss && group.Size == ZombieSize.Kilo), group =>
        {
            Assert.Equal(authoredBoss.Kind, group.Kind);
            Assert.Equal(authoredBoss.Tier, group.Tier);
        });
    }

    [Theory]
    [InlineData(7, GearTier.Wood)]
    [InlineData(20, GearTier.Iron)]
    [InlineData(35, GearTier.Osmium)]
    public void NaturalSpawnCreatesFourRealScaledArmorPieces(int level, GearTier tier)
    {
        var catalog = new LevelCatalog();
        var game = new GameSession(levels: catalog);
        var state = game.ExportState();
        state.LevelNumber = level;
        typeof(GameSession).GetMethod("RestoreCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game, [state]);
        Assert.True(game.StartLevel());
        var plan = WaveSpawnPlanner.Plan(Assert.Single(catalog.Get(level).Waves));
        // Begin at the natural roster's first boss slot so this assertion exercises
        // the real spawn path without simulating an unrelated full armored wave.
        int bossIndex = Enumerable.Range(0, plan.SpawnCount)
            .First(index => WaveSpawnPlanner.SelectGroup(plan, index) is { IsBoss: true, Size: ZombieSize.Kilo });
        game.Waves[0].Spawned = bossIndex;

        game.Update(.001f);

        var kilo = Assert.Single(game.Zombies);
        Assert.True(kilo.IsBoss);
        Assert.Equal(ZombieSize.Kilo, kilo.Size);
        Assert.Equal(ZombieKind.Armored, kilo.Kind);
        Assert.Equal(tier, kilo.Tier);
        Assert.Equal(4, kilo.ArmorPieces.Count);
        Assert.All(kilo.ArmorPieces, armor =>
        {
            Assert.Equal(tier, armor.Tier);
            Assert.Equal(4 * Balance.GearHp(tier), armor.Hp);
        });
        Assert.Equal(40, kilo.BodyHp);
        Assert.Equal(4 * (10 + 4 * Balance.GearHp(tier)), kilo.Hp);
        Assert.Equal(10, game.Waves[0].BudgetSpent);
        Assert.Equal(plan.TotalHp, game.Waves[0].TotalHp);
    }

    [Fact]
    public void BudgetedWaveWithoutAuthoredBossUsesItsStrongestAvailableGear()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition
        {
            HpBudget = 1000,
            Groups =
            [
                new() { Kind = ZombieKind.Basic, Count = 4 },
                new() { Kind = ZombieKind.Armored, Tier = GearTier.Wood },
                new() { Kind = ZombieKind.Armed, Tier = GearTier.Iron }
            ]
        });
        var boss = Assert.Single(plan.Groups, group => group.IsBoss && group.Size == ZombieSize.Kilo);
        Assert.Equal(ZombieKind.Armored, boss.Kind);
        Assert.Equal(GearTier.Iron, boss.Tier);
        Assert.Equal(1, boss.Count);
        Assert.Equal(10, WaveSpawnPlan.BudgetCost(boss));
        Assert.Equal(840, LevelCatalog.ZombieMaxHp(boss.Kind, boss.Tier, boss.Size));
        var mega = Assert.Single(plan.Groups, group => group.IsBoss && group.Size == ZombieSize.Mega);
        Assert.Equal(GearTier.Iron, mega.Tier);
        Assert.Equal(15, WaveSpawnPlan.BudgetCost(mega));
        Assert.InRange(plan.BudgetSpent, 0, 1000);
        Assert.Equal(plan.Groups.Sum(group => (float)group.Count *
            LevelCatalog.ZombieMaxHp(group.Kind, group.Tier, group.Size)), plan.TotalHp);
    }

    [Fact]
    public void ExplicitBossAuthoringKeepsItsChosenKindAndTier()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition
        {
            HpBudget = 1000,
            Groups =
            [
                new() { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Osmium },
                new() { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Wood, Size = ZombieSize.Kilo, IsBoss = true }
            ]
        });
        var boss = Assert.Single(plan.Groups, group => group.IsBoss);
        Assert.Equal(ZombieKind.ArmedArmored, boss.Kind);
        Assert.Equal(GearTier.Wood, boss.Tier);
        Assert.Equal(10, WaveSpawnPlan.BudgetCost(boss));
    }
}
