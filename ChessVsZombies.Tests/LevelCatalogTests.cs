using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class LevelCatalogTests
{
    [Theory]
    [InlineData(GearTier.Wood, 50, 3)]
    [InlineData(GearTier.Stone, 90, 4)]
    [InlineData(GearTier.Copper, 130, 6)]
    [InlineData(GearTier.Iron, 210, 9)]
    [InlineData(GearTier.Gold, 330, 14)]
    [InlineData(GearTier.Diamond, 530, 22)]
    [InlineData(GearTier.Osmium, 850, 35)]
    public void FourArmorItemsStackAndWeaponsAddToBaseAttack(GearTier tier, int armoredHp, int armedAttack)
    {
        Assert.Equal(10, LevelCatalog.ZombieMaxHp(ZombieKind.Basic, tier));
        Assert.Equal(10, LevelCatalog.ZombieMaxHp(ZombieKind.Armed, tier));
        Assert.Equal(armoredHp, LevelCatalog.ZombieMaxHp(ZombieKind.Armored, tier));
        Assert.Equal(armoredHp, LevelCatalog.ZombieMaxHp(ZombieKind.ArmedArmored, tier));
        Assert.Equal(1, LevelCatalog.ZombieAttack(ZombieKind.Basic, tier));
        Assert.Equal(1, LevelCatalog.ZombieAttack(ZombieKind.Armored, tier));
        Assert.Equal(armedAttack, LevelCatalog.ZombieAttack(ZombieKind.Armed, tier));
        Assert.Equal(armedAttack, LevelCatalog.ZombieAttack(ZombieKind.ArmedArmored, tier));
    }

    [Fact]
    public void GeneratedLevelsHaveOneWaveAndEventuallyIncludeEveryKindAndTier()
    {
        var levels = new LevelCatalog();
        var seenKinds = new HashSet<ZombieKind>();
        var seenTiers = new HashSet<GearTier>();
        for (int number = 1; number <= 40; number++)
        {
            var level = levels.Get(number);
            Assert.Equal(number, level.Number);
            var wave = Assert.Single(level.Waves);
            Assert.NotEmpty(wave.Groups);
            Assert.All(wave.Groups, group => Assert.True(group.Count > 0));
            seenKinds.UnionWith(wave.Groups.Select(group => group.Kind));
            seenTiers.UnionWith(wave.Groups.Select(group => group.Tier));
        }
        Assert.Equal(Enum.GetValues<ZombieKind>().Length, seenKinds.Count);
        Assert.Equal(Enum.GetValues<GearTier>().Length, seenTiers.Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(40)]
    public void GeneratedRostersIncludeMicrosWithoutAddingFreeSpawns(int level)
    {
        var wave = Assert.Single(new LevelCatalog().Get(level).Waves);
        var normal = wave.Groups.Where(group => !group.IsBoss).ToArray();
        Assert.Equal(6 + level * 2, normal.Sum(group => group.Count));
        Assert.Contains(normal, group => group.Size == ZombieSize.Micro);
        var plan = WaveSpawnPlanner.Plan(wave);
        Assert.Contains(plan.Groups, group => group.Size == ZombieSize.Micro && !group.IsBoss);
        Assert.InRange(plan.BudgetSpent, 0, plan.HpBudget);
        Assert.All(normal.Where(group => group.Size == ZombieSize.Micro), group =>
            Assert.Equal(2 + (group.Kind is ZombieKind.Armored or ZombieKind.ArmedArmored ? Balance.GearHp(group.Tier) : 0),
                WaveSpawnPlan.BudgetCost(group)));
    }

    [Theory]
    [InlineData(GearTier.Wood)]
    [InlineData(GearTier.Stone)]
    [InlineData(GearTier.Copper)]
    [InlineData(GearTier.Iron)]
    [InlineData(GearTier.Gold)]
    [InlineData(GearTier.Diamond)]
    [InlineData(GearTier.Osmium)]
    public void MicroArmorUsesOneQuarterDurabilityIndependentlyOfItsTwoBodyHp(GearTier tier)
    {
        foreach (var kind in Enum.GetValues<ZombieKind>())
        {
            var zombie = new Zombie { Kind = kind, Tier = tier, Size = ZombieSize.Micro };
            Assert.Equal(2, zombie.BodyHp);
            Assert.All(zombie.ArmorPieces, piece => Assert.Equal(Balance.GearHp(tier) * .25f, piece.Hp));
            Assert.Equal(zombie.Hp, LevelCatalog.ZombieMaxHp(kind, tier, ZombieSize.Micro));
        }
    }

    [Fact]
    public void AuthoredMultiwaveLevelsRemainStableWhenAuthoringOrReturnedObjectsChange()
    {
        var catalog = new LevelCatalog();
        var level = new LevelDefinition
        {
            Number = 1,
            Name = "Two fronts",
            Waves =
            [
                new() { Groups = [new() { Kind = ZombieKind.Basic, Count = 3 }] },
                new() { Groups = [new() { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Iron, Count = 2 }] }
            ]
        };
        catalog.Set(level);
        level.Waves.Clear();
        var loaded = catalog.Get(1);
        Assert.Equal(2, loaded.Waves.Count);
        loaded.Waves[0].Groups[0].Count = 100;
        Assert.Equal(3, catalog.Get(1).Waves[0].Groups[0].Count);
    }

    [Fact]
    public void JsonLevelErrorsAreReportedAndValidNeighborsStillLoad()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ChessVsZombiesTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "valid.json"), """
                { "number": 42, "name": "Custom defense", "waves": [
                  { "spawnInterval": 0.5, "groups": [{ "kind": "Armed", "tier": "Copper", "count": 7 }] }
                ] }
                """);
            File.WriteAllText(Path.Combine(directory, "invalid.json"), """
                { "number": 43, "name": "Invalid defense", "waves": [] }
                """);
            var catalog = new LevelCatalog(directory);
            Assert.Single(catalog.LoadErrors);
            Assert.Contains("invalid.json", catalog.LoadErrors[0]);
            var level = catalog.Get(42);
            Assert.Equal("Custom defense", level.Name);
            var group = Assert.Single(Assert.Single(level.Waves).Groups);
            Assert.Equal(ZombieKind.Armed, group.Kind);
            Assert.Equal(GearTier.Copper, group.Tier);
            Assert.Equal(7, group.Count);
        }
        finally
        {
            File.Delete(Path.Combine(directory, "valid.json"));
            File.Delete(Path.Combine(directory, "invalid.json"));
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(1, .5f)]
    [InlineData(5, .5f)]
    [InlineData(6, .45f)]
    [InlineData(10, .45f)]
    [InlineData(11, .4f)]
    [InlineData(46, .05f)]
    [InlineData(51, 0f)]
    public void LaneThresholdShrinksAfterEachFiveCompletedLevels(int level, float expected)
        => Assert.Equal(expected, Balance.LaneThreshold(level), precision: 5);
}
