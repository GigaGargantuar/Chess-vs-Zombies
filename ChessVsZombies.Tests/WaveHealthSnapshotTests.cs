using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class WaveHealthSnapshotTests
{
    [Fact]
    public void GigaDamageAndDeathMoveThePaidCombatSegmentWithoutMovingTheUnspentWallet()
    {
        var game = Combat(10000);
        var wave = Assert.Single(game.Waves);
        var waiting = game.GetWaveHealth(0);
        Assert.True(wave.TotalHp > 0);
        Assert.Equal(0, waiting.CombatHp);
        Assert.Equal(0, waiting.CombatMaxHp);
        Assert.Equal(10000, waiting.SpawnHp);
        Assert.Equal(10000, waiting.BarMaximum);

        game.Update(.001f);
        var giga = Assert.Single(game.Zombies, zombie => zombie.IsGiga && zombie.Hp > 0);
        var full = game.GetWaveHealth(0);
        Assert.Equal((double)giga.Hp, full.CombatHp);
        Assert.Equal((double)giga.MaxHp, full.CombatMaxHp);
        Assert.True(wave.TotalHp > full.CombatHp);
        Assert.Equal(4970, full.SpawnHp);
        Assert.Equal(10000, full.SpawnHpBudget);
        Assert.Equal(10000, full.BarMaximum);

        game.DamageZombie(giga, giga.Giga!.Resistance);
        Assert.Equal(full, game.GetWaveHealth(0));
        game.DamageZombie(giga, giga.Hp * .25f);
        var damaged = game.GetWaveHealth(0);
        Assert.Equal((double)giga.Hp, damaged.CombatHp);
        Assert.Equal(.75, damaged.CombatHp / full.CombatHp, 6);
        Assert.True(damaged.CombatHp / damaged.BarMaximum < full.CombatHp / full.BarMaximum);
        Assert.Equal(full.CombatMaxHp, damaged.CombatMaxHp);
        Assert.Equal(full.SpawnHp, damaged.SpawnHp);
        Assert.Equal(full.BarMaximum, damaged.BarMaximum);
        Assert.Equal(full.SpawnHp / full.BarMaximum, damaged.SpawnHp / damaged.BarMaximum);

        game.DamageZombie(giga, giga.Hp + 100);
        var killed = game.GetWaveHealth(0);
        Assert.Equal(0, killed.CombatHp);
        Assert.Equal(0, killed.CombatMaxHp);
        Assert.Equal(full.SpawnHp, killed.SpawnHp);
        Assert.Equal(full.BarMaximum, killed.BarMaximum);
        game.Update(.001f); // Remove the dead body before the next spawn cadence.
        Assert.DoesNotContain(giga, game.Zombies);
        Assert.Equal(killed, game.GetWaveHealth(0));

        int nextCounter = GigaRules.SelectInitialCounter(killed.SpawnHp);
        long nextCost = GigaRules.SpawnCost(nextCounter);
        Assert.True((bool)Invoke(game, "TrySpawnGiga", wave)!);
        var replacement = Assert.Single(game.Zombies, zombie => zombie.IsGiga && zombie.Hp > 0);
        var replaced = game.GetWaveHealth(0);
        Assert.Equal(nextCounter, replacement.Giga!.InitialCounter);
        Assert.True(nextCounter < giga.Giga.InitialCounter);
        Assert.Equal((double)replacement.Hp, replaced.CombatHp);
        Assert.Equal(killed.SpawnHp - nextCost, replaced.SpawnHp);
        Assert.Equal(full.BarMaximum, replaced.BarMaximum);
    }

    [Fact]
    public void HighLevelAllInOneShowsThePaidGigaAndPreservesSmallDamageAfterBillionHpKills()
    {
        var game = new GameSession(123, new LevelCatalog());
        var checkpoint = game.ExportState();
        checkpoint.LevelNumber = 1048576;
        checkpoint.MergeFiveLevels = true;
        checkpoint.AllInOne = true;
        Invoke(game, "RestoreCheckpoint", checkpoint);
        Assert.True(game.StartLevel(), game.Message);
        Assert.True(game.AllInOne);
        var wave = Assert.Single(game.Waves);
        Assert.True(wave.TotalHp > wave.HpBudget * 10d);
        Assert.Equal(0, game.GetWaveHealth(0).CombatHp);
        Assert.True((bool)Invoke(game, "TrySpawnGiga", wave)!);
        var giga = Assert.Single(game.Zombies);
        var full = game.GetWaveHealth(0);
        double premium = Math.Max(0, (double)giga.MaxHp - giga.OriginalBudgetCost);
        Assert.Equal(wave.HpBudget + premium, full.BarMaximum);
        Assert.Equal((double)giga.Hp, full.CombatHp);
        Assert.InRange(full.CombatHp / full.BarMaximum, .49, .51);
        Assert.Equal(wave.HpBudget - giga.OriginalBudgetCost, full.SpawnHp);

        game.DamageZombie(giga, giga.Hp * .25f);
        var damaged = game.GetWaveHealth(0);
        Assert.Equal(.75, damaged.CombatHp / full.CombatHp, 6);
        Assert.Equal(full.SpawnHp, damaged.SpawnHp);
        Assert.Equal(full.BarMaximum, damaged.BarMaximum);
        game.DamageZombie(giga, giga.Hp + 10000);
        Assert.Equal(0, game.GetWaveHealth(0).CombatHp);
        Assert.Equal(full.BarMaximum, game.GetWaveHealth(0).BarMaximum);
        Assert.True(wave.DamageTaken > 1_000_000_000);
        Assert.True(game.Zombies.Remove(giga));

        // A small, living actor can still lose one HP after the legacy aggregate
        // damage float has grown too large to represent that increment.
        var ordinary = new Zombie { Id = 9000000, WaveIndex = 0 };
        ordinary.Position = ordinary.StandAt(new Cell(10, 16, 10));
        game.Zombies.Add(ordinary);
        var beforeSmallHit = game.GetWaveHealth(0);
        game.DamageZombie(ordinary, 1, DamageKind.Chemical);
        var afterSmallHit = game.GetWaveHealth(0);
        Assert.Equal(1, beforeSmallHit.CombatHp - afterSmallHit.CombatHp);
        Assert.Equal((double)ordinary.Hp, afterSmallHit.CombatHp);
        Assert.Equal(beforeSmallHit.CombatMaxHp, afterSmallHit.CombatMaxHp);
        Assert.Equal(beforeSmallHit.BarMaximum, afterSmallHit.BarMaximum);
        Assert.Equal(beforeSmallHit.SpawnHp, afterSmallHit.SpawnHp);
    }

    [Fact]
    public void GigaGrowthAddsItsOwnedHealthPremiumWithoutSpendingAndTheScaleStaysAfterDeath()
    {
        var game = Combat(10000);
        var wave = Assert.Single(game.Waves);
        Assert.True((bool)Invoke(game, "TrySpawnGiga", wave)!);
        var giga = Assert.Single(game.Zombies);
        int initial = giga.Giga!.InitialCounter;
        giga.RestoreGigaState(initial, initial, 1, 1, 0);
        var before = game.GetWaveHealth(0);

        game.DamageZombie(giga, 1);

        var grown = game.GetWaveHealth(0);
        double premium = Math.Max(0, (double)giga.MaxHp - giga.OriginalBudgetCost);
        Assert.Equal(initial + 1, giga.Giga!.HpCounter);
        Assert.Equal((double)giga.Hp, grown.CombatHp);
        Assert.Equal((double)giga.MaxHp, grown.CombatMaxHp);
        Assert.Equal(wave.HpBudget + premium, grown.BarMaximum);
        Assert.True(grown.BarMaximum > before.BarMaximum);
        Assert.Equal(before.SpawnHp, grown.SpawnHp);
        game.DamageZombie(giga, giga.Hp + 100);
        var killed = game.GetWaveHealth(0);
        Assert.Equal(0, killed.CombatHp);
        Assert.Equal(grown.BarMaximum, killed.BarMaximum);
        Assert.Equal(grown.SpawnHp, killed.SpawnHp);
        Assert.True(game.Zombies.Remove(giga));
        Assert.Equal(killed, game.GetWaveHealth(0));
    }

    [Fact]
    public void DiscountedKiloAddsItsRealArmorPremiumAndDoesNotRefillTheScaleWhenKilled()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition
        {
            Waves = [new WaveDefinition
            {
                Groups = [new ZombieGroup { Kind = ZombieKind.Armored, Tier = GearTier.Iron, Size = ZombieSize.Kilo, IsBoss = true }]
            }]
        });
        var game = new GameSession(123, catalog);
        Assert.True(game.StartLevel());
        var wave = Assert.Single(game.Waves);
        Assert.True((bool)Invoke(game, "Spawn", wave)!);
        var kilo = Assert.Single(game.Zombies);
        var full = game.GetWaveHealth(0);
        Assert.Equal(10, kilo.OriginalBudgetCost);
        Assert.Equal(4, kilo.ArmorPieces.Count);
        Assert.Equal((double)kilo.Hp, full.CombatHp);
        Assert.Equal((double)kilo.MaxHp, full.CombatMaxHp);
        Assert.Equal(0, full.SpawnHp);
        Assert.Equal((double)kilo.MaxHp, full.BarMaximum);

        game.DamageZombie(kilo, kilo.BodyHp, DamageKind.Chemical);

        var killed = game.GetWaveHealth(0);
        Assert.Equal(0, killed.CombatHp);
        Assert.Equal(0, killed.CombatMaxHp);
        Assert.Equal(full.BarMaximum, killed.BarMaximum);
        Assert.Equal(0, killed.SpawnHp);
        Assert.True(game.Zombies.Remove(kilo));
        Assert.Equal(killed, game.GetWaveHealth(0));
    }

    private static GameSession Combat(int budget)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition
        {
            Waves = [new WaveDefinition { HpBudget = budget, EnableGiga = true, Groups = [new ZombieGroup { Kind = ZombieKind.Basic }] }]
        });
        var game = new GameSession(123, catalog);
        Assert.True(game.StartLevel());
        return game;
    }

    private static object? Invoke(object owner, string name, params object?[] arguments) => owner.GetType()
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, arguments);
}
