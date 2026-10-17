using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class WaveSpawnPlannerTests
{
    [Fact]
    public void GroupOrdinalInverseMatchesEveryInterleavedSlot()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition { Groups =
        [
            new() { Kind = ZombieKind.Basic, Count = 17 },
            new() { Kind = ZombieKind.Armed, Count = 7, Size = ZombieSize.Milli },
            new() { Kind = ZombieKind.Armored, Count = 5, Tier = GearTier.Iron },
            new() { Kind = ZombieKind.ArmedArmored, Count = 3, Tier = GearTier.Wood },
            new() { Kind = ZombieKind.Basic, Count = 2, Size = ZombieSize.Kilo, IsBoss = true },
            new() { Kind = ZombieKind.Armored, Count = 1, Size = ZombieSize.Mega, IsBoss = true }
        ] });
        for (int group = 0; group < plan.Groups.Count; group++)
            for (int ordinal = 0; ordinal < plan.Groups[group].Count; ordinal++)
            {
                int slot = WaveSpawnPlanner.SlotForGroupOrdinal(plan, group, ordinal);
                Assert.Same(plan.Groups[group], WaveSpawnPlanner.SelectGroup(plan, slot));
                Assert.Equal(ordinal, Enumerable.Range(0, slot).Count(index =>
                    ReferenceEquals(WaveSpawnPlanner.SelectGroup(plan, index), plan.Groups[group])));
            }
    }

    [Fact]
    public void GroupOrdinalInverseHandlesAnEighteenMillionSlotWaveWithoutExpansion()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition { Groups =
        [
            new() { Count = 17_999_990 },
            new() { Kind = ZombieKind.Armored, Tier = GearTier.Iron, Count = 8 },
            new() { Kind = ZombieKind.Basic, Size = ZombieSize.Kilo, IsBoss = true, Count = 2 }
        ] });
        Assert.Equal(18_000_000, plan.SpawnCount);
        foreach (int group in Enumerable.Range(0, plan.Groups.Count))
            foreach (int ordinal in new[] { 0, plan.Groups[group].Count / 2, plan.Groups[group].Count - 1 }.Distinct())
            {
                int slot = WaveSpawnPlanner.SlotForGroupOrdinal(plan, group, ordinal);
                Assert.Same(plan.Groups[group], WaveSpawnPlanner.SelectGroup(plan, slot));
            }
    }

    [Fact]
    public void ActualWaveSpawnsArmorAlongsideFodderWithoutChangingItsHpBudget()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition
        {
            Number = 1, Name = "Mixed column",
            Waves = [new() { SpawnInterval = .1f, Groups = [new() { Kind = ZombieKind.Basic, Count = 8 }, new() { Kind = ZombieKind.Armored, Tier = GearTier.Iron, Count = 2 }] }]
        });
        var game = new GameSession(levels: catalog);
        Assert.True(game.StartLevel());
        game.Update(1.01f);
        var order = game.Zombies.OrderBy(zombie => zombie.Id).ToArray();
        Assert.Equal(10, order.Length);
        Assert.Equal(ZombieKind.Armored, order[4].Kind);
        Assert.Equal(ZombieKind.Armored, order[9].Kind);
        Assert.Equal(8, order.Count(zombie => zombie.Kind == ZombieKind.Basic));
        Assert.Equal(500, game.Waves[0].TotalHp);
        Assert.Equal(0, game.Waves[0].DamageTaken);
    }

    [Fact]
    public void GeneratedWavesInterleaveTheirResolvedArmorAndBossQuotas()
    {
        var catalog = new LevelCatalog();
        for (int level = 3; level <= 100; level++)
        {
            var plan = WaveSpawnPlanner.Plan(Assert.Single(catalog.Get(level).Waves));
            var spawned = new Dictionary<(ZombieKind, GearTier, ZombieSize, bool), int>();
            for (int index = 0; index < plan.SpawnCount; index++)
            {
                var group = WaveSpawnPlanner.SelectGroup(plan, index);
                var key = (group.Kind, group.Tier, group.Size, group.IsBoss);
                spawned[key] = spawned.GetValueOrDefault(key) + 1;
            }
            foreach (var group in plan.Groups)
                Assert.Equal(group.Count, spawned[(group.Kind, group.Tier, group.Size, group.IsBoss)]);
            Assert.InRange(plan.BudgetSpent, 0, plan.HpBudget);
        }
    }

    [Theory]
    [InlineData(16, 4)]
    [InlineData(2, 7)]
    [InlineData(0, 5)]
    [InlineData(8, 0)]
    public void AuthoredCountsAndHpBudgetRemainExact(int basic, int armored)
    {
        var wave = new WaveDefinition();
        if (basic > 0) wave.Groups.Add(new() { Kind = ZombieKind.Basic, Count = basic });
        if (armored > 0) wave.Groups.Add(new() { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Osmium, Count = armored });
        var order = Enumerable.Range(0, basic + armored).Select(i => WaveSpawnPlanner.SelectGroup(wave, i)).ToArray();
        Assert.Equal(basic, order.Count(group => group.Kind == ZombieKind.Basic));
        Assert.Equal(armored, order.Count(group => group.Kind == ZombieKind.ArmedArmored));
        Assert.Equal(basic * 10 + armored * 850, order.Sum(group => LevelCatalog.ZombieMaxHp(group.Kind, group.Tier)));
        if (basic == 16) Assert.Equal(ZombieKind.ArmedArmored, order[4].Kind);
    }
}
