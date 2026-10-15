using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class WaveSpawnPlannerTests
{
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
