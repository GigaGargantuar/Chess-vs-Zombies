using System.Reflection;
using System.Text.Json;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MergedLevelTests
{
    [Fact]
    public void OrdinaryDefenseDefaultsToOneLevelAndTwoCurrencyPerSurvivor()
    {
        var game = SmallCampaign();

        Assert.False(game.MergeFiveLevels);
        Assert.Equal(1, game.LevelSpan);
        Assert.Equal(1, game.EndLevelNumber);
        Assert.Equal(2, game.SurvivorRewardPerTower);
        Assert.Equal(1, game.GetSourceLevelNumber(0));
        Assert.Single(game.Level.Waves);
        Assert.Single(game.PreparedWaves);
    }

    [Fact]
    public void MergedPreviewConcatenatesEveryAuthoredWaveWithoutChangingItsCompositionOrBudget()
    {
        var catalog = new LevelCatalog();
        for (int number = 1; number <= 5; number++)
            catalog.Set(new LevelDefinition
            {
                Number = number,
                Name = $"Authored {number}",
                Waves = Enumerable.Range(0, number % 2 + 1).Select(index => new WaveDefinition
                {
                    SpawnInterval = .15f * number + .01f * index,
                    HpBudget = index == 1 ? 500 : null,
                    BossBudgetFraction = .01f * number,
                    Groups =
                    [
                        new() { Kind = ZombieKind.Basic, Count = 3 },
                        new() { Kind = ZombieKind.ArmedArmored, Tier = (GearTier)(number - 1),
                            Size = ZombieSize.Milli, Count = 1 },
                        new() { Kind = ZombieKind.Armored, Tier = GearTier.Wood,
                            Size = number % 2 == 0 ? ZombieSize.Mega : ZombieSize.Kilo, IsBoss = true }
                    ]
                }).ToList()
            });
        var expected = Enumerable.Range(1, 5).SelectMany(number => catalog.Get(number).Waves
            .Select(wave => (Number: number, Wave: wave, Plan: WaveSpawnPlanner.Plan(wave)))).ToArray();
        var game = new GameSession(levels: catalog);

        Assert.True(game.SetMergeFiveLevels(true));

        Assert.True(game.MergeFiveLevels);
        Assert.Equal(5, game.LevelSpan);
        Assert.Equal(5, game.EndLevelNumber);
        Assert.Equal(25, game.SurvivorRewardPerTower);
        Assert.Equal(expected.Length, game.Level.Waves.Count);
        Assert.Equal(expected.Length, game.PreparedWaves.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Number, game.GetSourceLevelNumber(index));
            Assert.Equal(JsonSerializer.Serialize(expected[index].Wave), JsonSerializer.Serialize(game.Level.Waves[index]));
            Assert.Equal(PlanSnapshot(expected[index].Plan), PlanSnapshot(game.PreparedWaves[index]));
        }
        Assert.True(game.StartLevel());
        for (int index = 0; index < expected.Length; index++)
        {
            var wave = game.Waves[index];
            var plan = expected[index].Plan;
            Assert.Equal(plan.SpawnCount, wave.TotalCount);
            Assert.Equal(plan.TotalHp, wave.TotalHp);
            Assert.Equal(plan.HpBudget, wave.HpBudget);
            Assert.Equal(plan.NormalBudget, wave.NormalBudget);
            Assert.Equal(plan.BossBudget, wave.BossBudget);
            Assert.Equal(index == 0, wave.Started);
        }
    }

    [Fact]
    public void AuthoredOverridesAndGeneratedLevelsBothKeepTheirOriginalPreparedRosters()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition
        {
            Number = 3, Name = "Custom third level",
            Waves = [new() { SpawnInterval = .27f, Groups = [new() { Kind = ZombieKind.Armed, Tier = GearTier.Diamond, Count = 2 }] }]
        });
        var expected = Enumerable.Range(1, 5).SelectMany(number => catalog.Get(number).Waves
            .Select(wave => (Source: number, Wave: wave, Plan: WaveSpawnPlanner.Plan(wave)))).ToArray();
        var game = new GameSession(levels: catalog);

        Assert.True(game.SetMergeFiveLevels(true));

        Assert.Equal(expected.Length, game.Level.Waves.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Source, game.GetSourceLevelNumber(index));
            Assert.Equal(JsonSerializer.Serialize(expected[index].Wave), JsonSerializer.Serialize(game.Level.Waves[index]));
            Assert.Equal(PlanSnapshot(expected[index].Plan), PlanSnapshot(game.PreparedWaves[index]));
        }
    }

    [Fact]
    public void DisablingMergeReplacesThePreviewWithOnlyTheCurrentLevel()
    {
        var game = SmallCampaign();
        string ordinary = PlanSnapshot(Assert.Single(game.PreparedWaves));
        Assert.True(game.SetMergeFiveLevels(true));
        Assert.Equal(5, game.PreparedWaves.Count);

        Assert.True(game.SetMergeFiveLevels(false));

        Assert.False(game.MergeFiveLevels);
        Assert.Equal(1, game.LevelSpan);
        Assert.Equal(1, game.EndLevelNumber);
        Assert.Equal(2, game.SurvivorRewardPerTower);
        Assert.Equal(ordinary, PlanSnapshot(Assert.Single(game.PreparedWaves)));
        Assert.Single(game.Level.Waves);
    }

    [Fact]
    public void TheNextSourceLevelStartsAtHalfDamageWithoutAnIntermediateRewardOrBuildingPhase()
    {
        var game = SmallCampaign(zombiesPerWave: 4, spawnInterval: 1);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.SetMergeFiveLevels(true));
        int balance = game.Economy.Currency;
        Assert.True(game.StartLevel());
        game.Update(.001f);
        var first = Assert.Single(game.Zombies);
        game.DamageZombie(first, 10);
        game.Update(.001f);
        Assert.False(game.Waves[1].Started);
        Assert.Equal(10, game.Waves[0].DamageTaken);
        Spawn(game, game.Waves[0]);
        var second = Assert.Single(game.Zombies);
        game.DamageZombie(second, 9);
        game.Update(.001f);
        Assert.False(game.Waves[1].Started);

        game.DamageZombie(second, 1);
        game.Update(.001f);

        Assert.Equal(20, game.Waves[0].DamageTaken);
        Assert.True(game.Waves[1].Started);
        Assert.False(game.Waves[2].Started);
        Assert.Equal(2, game.Waves[0].Spawned);
        Assert.Equal(4, game.Waves[0].TotalCount);
        Assert.Contains(game.Zombies, zombie => zombie.WaveIndex == 1);
        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.Equal(1, game.LevelNumber);
        Assert.Equal(balance, game.Economy.Currency);
    }

    [Fact]
    public void MergedVictoryPaysTwentyFivePerFinalSurvivorExactlyOnceAndAdvancesFiveLevels()
    {
        var game = SmallCampaign();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(3, 16, 3)));
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(4, 16, 4)));
        game.Turrets[2].Hp = 0;
        Assert.True(game.SetMergeFiveLevels(true));
        int balance = game.Economy.Currency;
        Assert.True(game.StartLevel());
        for (int index = 0; index < 4; index++)
        {
            FinishWave(game, index);
            Assert.Equal(GamePhase.Combat, game.Phase);
            Assert.Equal(1, game.LevelNumber);
            Assert.Equal(balance, game.Economy.Currency);
        }

        FinishWave(game, 4);

        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(6, game.LevelNumber);
        Assert.Equal(balance + 50, game.Economy.Currency);
        Assert.False(game.MergeFiveLevels);
        Assert.Equal(1, game.LevelSpan);
        Assert.Equal(6, game.EndLevelNumber);
        Assert.Equal(2, game.SurvivorRewardPerTower);
        Assert.Single(game.PreparedWaves);
        Assert.Equal(6, game.GetSourceLevelNumber(0));
        game.Update(.1f);
        Assert.Equal(balance + 50, game.Economy.Currency);
    }

    [Fact]
    public void TheBattleAfterAMergedVictoryDefaultsToOneLevelUntilMergeIsChosenAgain()
    {
        var game = SmallCampaign();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.SetMergeFiveLevels(true));
        int balance = game.Economy.Currency;
        Assert.True(game.StartLevel());
        FinishBattle(game);
        Assert.Equal(6, game.LevelNumber);
        Assert.False(game.MergeFiveLevels);
        Assert.Equal(balance + 25, game.Economy.Currency);

        Assert.True(game.StartLevel());
        Assert.Single(game.Waves);
        FinishBattle(game);

        Assert.Equal(7, game.LevelNumber);
        Assert.Equal(balance + 27, game.Economy.Currency);
        Assert.False(game.MergeFiveLevels);
        Assert.True(game.SetMergeFiveLevels(true));
        Assert.Equal(11, game.EndLevelNumber);
        Assert.Equal(5, game.PreparedWaves.Count);
        Assert.Equal(25, game.SurvivorRewardPerTower);
        Assert.True(game.StartLevel());
        FinishBattle(game);
        Assert.Equal(12, game.LevelNumber);
        Assert.Equal(balance + 52, game.Economy.Currency);
        Assert.False(game.MergeFiveLevels);
        Assert.Single(game.PreparedWaves);
    }

    [Fact]
    public void OrdinaryVictoryStillAdvancesOneLevelAndPaysTwoPerSurvivor()
    {
        var game = SmallCampaign();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        int balance = game.Economy.Currency;
        Assert.True(game.StartLevel());

        FinishWave(game, 0);

        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(2, game.LevelNumber);
        Assert.Equal(balance + 2, game.Economy.Currency);
        Assert.False(game.MergeFiveLevels);
        Assert.Single(game.PreparedWaves);
    }

    [Fact]
    public void ADefeatDuringMergedCombatAwardsNothingAndDoesNotAdvanceTheCampaign()
    {
        var game = SmallCampaign();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.SetMergeFiveLevels(true));
        int balance = game.Economy.Currency;
        Assert.True(game.StartLevel());
        Spawn(game, game.Waves[0]);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = new Cell(14, 16, 15).Center;
        zombie.Attack = Balance.CoreHp;

        game.Update(.01f);

        Assert.Equal(GamePhase.Defeat, game.Phase);
        Assert.Equal(0, game.CoreHp);
        Assert.Equal(1, game.LevelNumber);
        Assert.Equal(balance, game.Economy.Currency);
        Assert.False(game.MergeFiveLevels);
        Assert.Equal(1, game.LevelSpan);
        Assert.Equal(2, game.SurvivorRewardPerTower);
        Assert.False(game.SetMergeFiveLevels(false));
        game.Update(.1f);
        Assert.Equal(balance, game.Economy.Currency);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheMergeOptionCannotChangeTheRunningBattle(bool merged)
    {
        var game = SmallCampaign();
        Assert.True(game.SetMergeFiveLevels(merged));
        Assert.True(game.StartLevel());
        string roster = JsonSerializer.Serialize(game.Level);
        var waves = game.Waves.ToArray();

        Assert.False(game.SetMergeFiveLevels(!merged));

        Assert.Equal(merged, game.MergeFiveLevels);
        Assert.Equal(roster, JsonSerializer.Serialize(game.Level));
        Assert.Equal(waves, game.Waves);
        Assert.Equal(GamePhase.Combat, game.Phase);
    }

    [Fact]
    public void ResetReturnsTheNewCampaignToOrdinarySingleLevels()
    {
        var game = SmallCampaign();
        Assert.True(game.SetMergeFiveLevels(true));
        Assert.True(game.StartLevel());

        game.Reset();

        Assert.False(game.MergeFiveLevels);
        Assert.Equal(1, game.LevelNumber);
        Assert.Equal(1, game.LevelSpan);
        Assert.Equal(1, game.EndLevelNumber);
        Assert.Equal(2, game.SurvivorRewardPerTower);
        Assert.Single(game.PreparedWaves);
        Assert.Empty(game.Waves);
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    [Fact]
    public void StartingMergedCombatRejectsAnUnpayableRewardBeforeMutatingTheBattle()
    {
        var game = SmallCampaign();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.SetMergeFiveLevels(true));
        game.Economy.Credit(int.MaxValue - 24 - game.Economy.Currency);
        var level = game.Level;
        var plans = game.PreparedWaves;

        Assert.False(game.StartLevel());

        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Empty(game.Waves);
        Assert.Equal(1, game.LevelNumber);
        Assert.Equal(int.MaxValue - 24, game.Economy.Currency);
        Assert.Same(level, game.Level);
        Assert.Same(plans, game.PreparedWaves);
        Assert.True(game.MergeFiveLevels);
    }

    [Fact]
    public void AnExactlyPayableMergedRewardReachesTheWalletLimitWithoutOverflow()
    {
        var game = SmallCampaign();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.SetMergeFiveLevels(true));
        game.Economy.Credit(int.MaxValue - 25 - game.Economy.Currency);
        Assert.True(game.StartLevel());

        FinishBattle(game);

        Assert.Equal(int.MaxValue, game.Economy.Currency);
        Assert.Equal(6, game.LevelNumber);
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    [Fact]
    public void SelectingMergeRejectsAnUnpayableRewardWithoutChangingTheOrdinaryPreview()
    {
        var game = SmallCampaign();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        game.Economy.Credit(int.MaxValue - 2 - game.Economy.Currency);
        var level = game.Level;
        var plans = game.PreparedWaves;

        Assert.False(game.SetMergeFiveLevels(true));

        Assert.False(game.MergeFiveLevels);
        Assert.Same(level, game.Level);
        Assert.Same(plans, game.PreparedWaves);
        Assert.Equal(1, game.LevelSpan);
        Assert.Equal(int.MaxValue - 2, game.Economy.Currency);
        Assert.True(game.StartLevel());
    }

    [Fact]
    public void CombatCannotBuyAnotherSurvivorWhenItsMergedPayoutWouldOverflowTheWallet()
    {
        var game = SmallCampaign();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.SetMergeFiveLevels(true));
        game.Economy.Credit(int.MaxValue - 25 - game.Economy.Currency);
        Assert.True(game.StartLevel());

        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(3, 16, 3)));

        Assert.Single(game.Turrets);
        Assert.Equal(int.MaxValue - 25, game.Economy.Currency);
        Assert.Equal(GamePhase.Combat, game.Phase);
    }

    [Fact]
    public void CombatCannotRefundARookWhenItsRefundWouldOverflowTheReservedOrdinaryReward()
    {
        var game = SmallCampaign();
        var cell = new Cell(2, 16, 2);
        Assert.True(game.Deploy(PieceKind.Rook, cell));
        game.Economy.Credit(int.MaxValue - 2 - game.Economy.Currency);
        Assert.True(game.StartLevel());

        Assert.False(game.Remove(cell));

        Assert.Equal(cell, Assert.Single(game.Turrets).Cell);
        Assert.Equal(int.MaxValue - 2, game.Economy.Currency);
    }

    [Fact]
    public void ADepartingZombieFromAnEarlierSourceLevelReturnsInTheNextMergedWave()
    {
        var game = SmallCampaign();
        Assert.True(game.SetMergeFiveLevels(true));
        Assert.True(game.StartLevel());
        Spawn(game, game.Waves[0]);
        var zombie = Assert.Single(game.Zombies);
        int id = zombie.Id;

        Invoke(game, "DeferZombieToNextWave", zombie);

        Assert.Equal(1, game.LevelNumber);
        Assert.Equal(1, game.PendingReinforcementCount);
        Assert.Equal(2, game.Waves[1].TotalCount);
        Spawn(game, game.Waves[1]);
        var returned = Assert.Single(game.Zombies);
        Assert.Equal(id, returned.Id);
        Assert.Equal(1, returned.WaveIndex);
        Assert.Equal(2, game.GetSourceLevelNumber(returned.WaveIndex));
        Assert.Equal(0, game.PendingReinforcementCount);
    }

    [Fact]
    public void TheFinalMergedWaveDefersALivingZombieIntoTheFirstWaveFiveLevelsLater()
    {
        var game = SmallCampaign();
        Assert.True(game.SetMergeFiveLevels(true));
        Assert.True(game.StartLevel());
        for (int index = 0; index < 4; index++) FinishWave(game, index);
        var final = game.Waves[4];
        if (final.Spawned == 0) Spawn(game, final);
        var zombie = Assert.Single(game.Zombies);
        int survivorId = zombie.Id;

        Invoke(game, "DeferZombieToNextWave", zombie);
        game.Update(.001f);

        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(6, game.LevelNumber);
        Assert.Equal(1, game.PendingReinforcementCount);
        var returning = Assert.Single(game.ExportState().ReturningZombies!);
        Assert.Equal(6, returning.TargetLevel);
        Assert.Equal(0, returning.TargetWaveIndex);
        Assert.True(game.StartLevel());
        Assert.Equal(2, game.Waves[0].TotalCount);
        Spawn(game, game.Waves[0]);
        var restored = Assert.Single(game.Zombies);
        Assert.Equal(survivorId, restored.Id);
        Assert.Equal(0, restored.WaveIndex);
        Assert.Equal(0, game.PendingReinforcementCount);
    }

    [Fact]
    public void RetreatArmorUsesTheZombiesOwnSourceLevelWithoutLeakingLaterUnlocks()
    {
        var game = SmallCampaign();
        Assert.True(game.SetMergeFiveLevels(true));
        Assert.True(game.StartLevel());
        var early = new Zombie { Id = 10001, WaveIndex = 0, Kind = ZombieKind.Basic, Position = new Cell(4, 16, 4).Center };
        var late = new Zombie { Id = 10002, WaveIndex = 4, Kind = ZombieKind.Basic, Position = new Cell(5, 16, 5).Center };
        Set(early, nameof(Zombie.RetreatReturns), 2);
        Set(late, nameof(Zombie.RetreatReturns), 2);
        game.Zombies.AddRange([early, late]);

        Invoke(game, "UpgradeRetreatArmor", early);
        Invoke(game, "UpgradeRetreatArmor", late);

        Assert.Equal(ZombieKind.Basic, early.Kind);
        Assert.Empty(early.ArmorPieces);
        Assert.Equal(ZombieKind.Armored, late.Kind);
        Assert.Equal(4, late.ArmorPieces.Count());
        Assert.All(late.ArmorPieces, armor => Assert.Equal(GearTier.Wood, armor.Tier));
    }

    [Fact]
    public void LaneDamageThresholdUsesEachSourceLevelWhenAMergeCrossesAProgressionBoundary()
    {
        var game = SmallCampaign();
        for (int number = 1; number < 4; number++)
        {
            Assert.True(game.StartLevel());
            FinishBattle(game);
        }
        Assert.Equal(4, game.LevelNumber);
        Assert.True(game.SetMergeFiveLevels(true));
        Assert.True(game.StartLevel());
        Spawn(game, game.Waves[0]);
        Spawn(game, game.Waves[2]);
        var early = Assert.Single(game.Zombies, zombie => zombie.WaveIndex == 0);
        var late = Assert.Single(game.Zombies, zombie => zombie.WaveIndex == 2);
        int earlyLane = early.Lane, lateLane = late.Lane;
        var earlyPosition = early.Position;
        var latePosition = late.Position;

        game.DamageZombie(early, 4.5f);
        game.DamageZombie(late, 4.5f);

        Assert.Equal(4, game.GetSourceLevelNumber(0));
        Assert.Equal(6, game.GetSourceLevelNumber(2));
        Assert.Equal(earlyLane, early.Lane);
        Assert.NotEqual(lateLane, late.Lane);
        Assert.Equal(earlyPosition, early.Position);
        Assert.Equal(latePosition, late.Position);
    }

    private static GameSession SmallCampaign(int zombiesPerWave = 1, float spawnInterval = 1.4f)
    {
        var catalog = new LevelCatalog();
        for (int number = 1; number <= 15; number++)
            catalog.Set(new LevelDefinition
            {
                Number = number, Name = $"Small {number}",
                Waves = [new() { SpawnInterval = spawnInterval, Groups = [new() { Count = zombiesPerWave }] }]
            });
        return new GameSession(levels: catalog);
    }

    private static string PlanSnapshot(WaveSpawnPlan plan) => JsonSerializer.Serialize(new
    {
        plan.Groups, plan.SpawnCount, plan.TotalHp, plan.HpBudget,
        plan.NormalBudget, plan.BossBudget, plan.BudgetSpent
    });

    private static void FinishWave(GameSession game, int index)
    {
        var wave = game.Waves[index];
        Assert.True(wave.Started);
        while (wave.Spawned < wave.TotalCount) Spawn(game, wave);
        foreach (var zombie in game.Zombies.Where(zombie => zombie.WaveIndex == index).ToArray())
            game.DamageZombie(zombie, 1_000_000, DamageKind.Chemical);
        game.Update(.001f);
    }

    private static void FinishBattle(GameSession game)
    {
        int waves = game.Waves.Count;
        for (int index = 0; index < waves; index++) FinishWave(game, index);
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    private static void Spawn(GameSession game, WaveState wave) => Assert.True((bool)Invoke(game, "Spawn", wave)!);
    private static object? Invoke(object target, string method, params object?[] arguments) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static void Set(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);
}
