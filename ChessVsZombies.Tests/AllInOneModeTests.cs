using System.Reflection;
using System.Text.Json;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class AllInOneModeTests
{
    [Fact]
    public void AllSourceBudgetsBecomeOneWaveUsingTheFinalWaveEquipmentAndCadence()
    {
        var catalog = new LevelCatalog();
        for (int number = 1; number <= 5; number++)
            catalog.Set(new LevelDefinition
            {
                Number = number,
                Waves = Enumerable.Range(0, number % 2 + 1).Select(index => new WaveDefinition
                {
                    SpawnInterval = number + index * .25f,
                    HpBudget = index == 0 ? 1000 + number : null,
                    BossBudgetFraction = .01f * number,
                    Groups = number == 5 && index == 1
                        ? [new() { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Copper, Count = 2 },
                           new() { Kind = ZombieKind.Armored, Tier = GearTier.Copper, Size = ZombieSize.Mega, IsBoss = true }]
                        : [new() { Count = 2 }, new() { Size = ZombieSize.Kilo, IsBoss = true }]
                }).ToList()
            });
        var sources = Enumerable.Range(1, 5).SelectMany(number => catalog.Get(number).Waves).ToArray();
        var sourcePlans = sources.Select(WaveSpawnPlanner.Plan).ToArray();
        var game = new GameSession(levels: catalog);

        Assert.True(game.SetAllInOne(true));

        Assert.True(game.AllInOne);
        Assert.True(game.MergeFiveLevels);
        Assert.Equal(5, game.LevelSpan);
        Assert.Equal(5, game.EndLevelNumber);
        Assert.Equal(5, game.GetSourceLevelNumber(0));
        var definition = Assert.Single(game.Level.Waves);
        Assert.Equal(sources[^1].SpawnInterval / 5, definition.SpawnInterval);
        var plan = Assert.Single(game.PreparedWaves);
        Assert.Equal(sourcePlans.Sum(source => source.HpBudget), plan.HpBudget);
        Assert.Equal(sourcePlans.Sum(source => source.NormalBudget), plan.NormalBudget);
        Assert.Equal(sourcePlans.Sum(source => source.BossBudget), plan.BossBudget);
        Assert.All(plan.Groups.Where(group => !group.IsBoss), group =>
        {
            Assert.Equal(ZombieKind.ArmedArmored, group.Kind);
            Assert.Equal(GearTier.Copper, group.Tier);
        });
        Assert.All(plan.Groups.Where(group => group.IsBoss), group =>
        {
            Assert.Equal(ZombieKind.Armored, group.Kind);
            Assert.Equal(GearTier.Copper, group.Tier);
            Assert.Equal(ZombieSize.Mega, group.Size);
        });
        Assert.True(plan.BudgetSpent < plan.HpBudget);
        string preview = Snapshot(plan);

        Assert.True(game.StartLevel());

        Assert.Equal(preview, Snapshot(Assert.Single(game.PreparedWaves)));
        var running = Assert.Single(game.Waves);
        Assert.True(running.Started);
        Assert.Equal(plan.SpawnCount, running.TotalCount);
        Assert.Equal(plan.TotalHp, running.TotalHp);
        Assert.Equal(plan.HpBudget, running.HpBudget);
        Assert.Equal(plan.NormalBudget, running.NormalBudget);
        Assert.Equal(plan.BossBudget, running.BossBudget);
    }

    [Fact]
    public void GeneratedFinalWaveGearAndRetreatArmorAreAvailableImmediately()
    {
        var game = new GameSession(levels: new LevelCatalog());
        Assert.True(game.SetAllInOne(true));
        var plan = Assert.Single(game.PreparedWaves);
        Assert.Contains(plan.Groups, group => group.Kind is ZombieKind.Armored or ZombieKind.ArmedArmored);
        Assert.All(plan.Groups.Where(group => group.Kind != ZombieKind.Basic), group => Assert.Equal(GearTier.Wood, group.Tier));
        Assert.True(game.StartLevel());
        var zombie = new Zombie { Id = 10001, WaveIndex = 0, Position = new Cell(4, 16, 4).Center };
        typeof(Zombie).GetProperty(nameof(Zombie.RetreatReturns))!.SetValue(zombie, 2);
        game.Zombies.Add(zombie);

        Invoke(game, "UpgradeRetreatArmor", zombie);

        Assert.Equal(5, game.GetSourceLevelNumber(0));
        Assert.Equal(ZombieKind.Armored, zombie.Kind);
        Assert.Equal(4, zombie.ArmorPieces.Count());
        Assert.All(zombie.ArmorPieces, armor => Assert.Equal(GearTier.Wood, armor.Tier));
    }

    [Fact]
    public void TheCombinedWaveSpawnsFiveTimesAsOftenAsItsFinalSourceWave()
    {
        var game = new GameSession(levels: SmallCatalog(count: 10, interval: 1));
        Assert.True(game.SetAllInOne(true));
        Assert.True(game.StartLevel());

        game.Update(.01f);
        Assert.Equal(1, game.Waves[0].Spawned);
        game.Update(.18f);
        Assert.Equal(1, game.Waves[0].Spawned);
        game.Update(.02f);

        Assert.Equal(2, game.Waves[0].Spawned);
        Assert.Equal(50, game.Waves[0].TotalCount);
        Assert.All(game.Zombies, zombie => Assert.Equal(0, zombie.WaveIndex));
    }

    [Fact]
    public void LaneDamageThresholdUsesTheFinalIncludedLevelFromTheFirstSpawn()
    {
        var game = new GameSession(levels: SmallCatalog());
        for (int level = 1; level < 4; level++)
        {
            Assert.True(game.StartLevel());
            FinishBattle(game);
        }
        Assert.True(game.SetAllInOne(true));
        Assert.True(game.StartLevel());
        Spawn(game, game.Waves[0]);
        var zombie = Assert.Single(game.Zombies);
        int lane = zombie.Lane;
        var position = zombie.Position;

        game.DamageZombie(zombie, 4.5f);

        Assert.Equal(4, game.LevelNumber);
        Assert.Equal(8, game.GetSourceLevelNumber(0));
        Assert.NotEqual(lane, zombie.Lane);
        Assert.Equal(position, zombie.Position);
    }

    [Fact]
    public void SelectingEitherExistingModeClearsAllInOneAndDisablingItKeepsFiveLevels()
    {
        var game = new GameSession(levels: SmallCatalog());
        Assert.False(game.AllInOne);
        Assert.True(game.SetAllInOne(true));
        Assert.Equal(75, game.SurvivorRewardPerTower);

        Assert.True(game.SetAllInOne(false));
        Assert.False(game.AllInOne);
        Assert.True(game.MergeFiveLevels);
        Assert.Equal(5, game.PreparedWaves.Count);
        Assert.Equal(25, game.SurvivorRewardPerTower);
        Assert.True(game.SetAllInOne(true));

        Assert.True(game.SetMergeFiveLevels(true));
        Assert.False(game.AllInOne);
        Assert.True(game.MergeFiveLevels);
        Assert.Equal(5, game.PreparedWaves.Count);
        Assert.True(game.SetAllInOne(true));

        Assert.True(game.SetMergeFiveLevels(false));
        Assert.False(game.AllInOne);
        Assert.False(game.MergeFiveLevels);
        Assert.Single(game.PreparedWaves);
        Assert.Equal(2, game.SurvivorRewardPerTower);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RunningBattlesRejectChangesBetweenAllThreeModes(int mode)
    {
        var game = new GameSession(levels: SmallCatalog());
        if (mode == 1) Assert.True(game.SetMergeFiveLevels(true));
        if (mode == 2) Assert.True(game.SetAllInOne(true));
        var level = game.Level;
        Assert.True(game.StartLevel());
        var waves = game.Waves.ToArray();

        Assert.False(game.SetAllInOne(mode != 2));
        Assert.False(game.SetMergeFiveLevels(mode == 0));

        Assert.Equal(mode == 2, game.AllInOne);
        Assert.Equal(mode != 0, game.MergeFiveLevels);
        Assert.Same(level, game.Level);
        Assert.Equal(waves, game.Waves);
    }

    [Fact]
    public void VictoryPaysSeventyFivePerFinalSurvivorOnceAndAdvancesFiveLevels()
    {
        var game = new GameSession(levels: SmallCatalog());
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(3, 16, 3)));
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(4, 16, 4)));
        game.Turrets[2].Hp = 0;
        Assert.True(game.SetAllInOne(true));
        int balance = game.Economy.Currency;
        Assert.True(game.StartLevel());

        FinishBattle(game);

        Assert.Equal(6, game.LevelNumber);
        Assert.Equal(balance + 150, game.Economy.Currency);
        Assert.False(game.AllInOne);
        Assert.False(game.MergeFiveLevels);
        Assert.Equal(2, game.SurvivorRewardPerTower);
        Assert.Single(game.PreparedWaves);
        game.Update(.1f);
        Assert.Equal(balance + 150, game.Economy.Currency);
        Assert.True(game.StartLevel());
        FinishBattle(game);
        Assert.Equal(7, game.LevelNumber);
        Assert.Equal(balance + 154, game.Economy.Currency);
    }

    [Fact]
    public void DefeatAwardsNothingAndClearsBothSelectionsWithoutAdvancing()
    {
        var game = new GameSession(levels: SmallCatalog());
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.SetAllInOne(true));
        int balance = game.Economy.Currency;
        Assert.True(game.StartLevel());
        Spawn(game, game.Waves[0]);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = new Cell(14, 16, 15).Center;
        zombie.Attack = Balance.CoreHp;

        game.Update(.01f);

        Assert.Equal(GamePhase.Defeat, game.Phase);
        Assert.Equal(1, game.LevelNumber);
        Assert.Equal(balance, game.Economy.Currency);
        Assert.False(game.AllInOne);
        Assert.False(game.MergeFiveLevels);
        Assert.Equal(1, game.LevelSpan);
        Assert.Equal(2, game.SurvivorRewardPerTower);
        Assert.False(game.SetAllInOne(true));
    }

    [Fact]
    public void ResetRestoresTheOrdinarySingleLevelPreview()
    {
        var game = new GameSession(levels: SmallCatalog());
        Assert.True(game.SetAllInOne(true));
        Assert.True(game.StartLevel());

        game.Reset();

        Assert.False(game.AllInOne);
        Assert.False(game.MergeFiveLevels);
        Assert.Equal(1, game.LevelNumber);
        Assert.Equal(1, game.GetSourceLevelNumber(0));
        Assert.Equal(2, game.SurvivorRewardPerTower);
        Assert.Single(game.PreparedWaves);
        Assert.Empty(game.Waves);
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    [Fact]
    public void SelectingAllInOneRejectsAnUnpayableRewardWithoutChangingThePreview()
    {
        var game = new GameSession(levels: SmallCatalog());
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        game.Economy.Credit(int.MaxValue - 74 - game.Economy.Currency);
        var level = game.Level;
        var plans = game.PreparedWaves;

        Assert.False(game.SetAllInOne(true));

        Assert.False(game.AllInOne);
        Assert.False(game.MergeFiveLevels);
        Assert.Same(level, game.Level);
        Assert.Same(plans, game.PreparedWaves);
    }

    [Fact]
    public void StartingAllInOneRechecksThatTheEntireRewardFits()
    {
        var game = new GameSession(levels: SmallCatalog());
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.SetAllInOne(true));
        game.Economy.Credit(int.MaxValue - 74 - game.Economy.Currency);
        var level = game.Level;
        var plans = game.PreparedWaves;

        Assert.False(game.StartLevel());

        Assert.True(game.AllInOne);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Empty(game.Waves);
        Assert.Same(level, game.Level);
        Assert.Same(plans, game.PreparedWaves);
    }

    [Fact]
    public void ExactlyPayableRewardReachesTheWalletLimitAndBlocksAnotherSurvivor()
    {
        var game = new GameSession(levels: SmallCatalog());
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.SetAllInOne(true));
        game.Economy.Credit(int.MaxValue - 75 - game.Economy.Currency);
        Assert.True(game.StartLevel());
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(3, 16, 3)));

        FinishBattle(game);

        Assert.Equal(int.MaxValue, game.Economy.Currency);
        Assert.Equal(6, game.LevelNumber);
    }

    [Fact]
    public void DepartingSurvivorReturnsAfterTheEntireFiveLevelRange()
    {
        var game = new GameSession(levels: SmallCatalog());
        Assert.True(game.SetAllInOne(true));
        Assert.True(game.StartLevel());
        var wave = Assert.Single(game.Waves);
        while (wave.Spawned < wave.TotalCount) Spawn(game, wave);
        var survivor = game.Zombies[0];
        int id = survivor.Id;

        Invoke(game, "DeferZombieToNextWave", survivor);
        foreach (var zombie in game.Zombies.ToArray()) game.DamageZombie(zombie, 1_000_000, DamageKind.Chemical);
        game.Update(.001f);

        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(6, game.LevelNumber);
        var saved = Assert.Single(game.ExportState().ReturningZombies!);
        Assert.Equal(6, saved.TargetLevel);
        Assert.Equal(0, saved.TargetWaveIndex);
        Assert.Equal(id, saved.Id);
        Assert.True(game.StartLevel());
        Assert.Equal(2, Assert.Single(game.Waves).TotalCount);
        Spawn(game, game.Waves[0]);
        Assert.Equal(id, Assert.Single(game.Zombies).Id);
        Assert.Equal(0, game.PendingReinforcementCount);
    }

    [Fact]
    public void APreviousBattleSurvivorJoinsTheSingleCombinedWaveWithoutLosingNativeSpawns()
    {
        var game = new GameSession(levels: SmallCatalog());
        Assert.True(game.StartLevel());
        Spawn(game, game.Waves[0]);
        var survivor = Assert.Single(game.Zombies);
        int id = survivor.Id;
        Invoke(game, "DeferZombieToNextWave", survivor);
        game.Update(.001f);
        Assert.Equal(2, game.LevelNumber);
        Assert.True(game.SetAllInOne(true));
        Assert.True(game.StartLevel());
        var wave = Assert.Single(game.Waves);

        Assert.Equal(6, wave.TotalCount);
        while (wave.Spawned < wave.TotalCount) Spawn(game, wave);

        Assert.Equal(6, game.Zombies.Count);
        Assert.Equal(6, game.Zombies.Select(zombie => zombie.Id).Distinct().Count());
        Assert.Single(game.Zombies, zombie => zombie.Id == id);
        Assert.All(game.Zombies, zombie => Assert.Equal(0, zombie.WaveIndex));
        Assert.Equal(60, wave.BudgetSpent);
        Assert.Equal(60, wave.HpBudget);
        Assert.Equal(0, game.PendingReinforcementCount);
    }

    [Fact]
    public void CombinedBudgetsCanExceedAnIntegerWithoutExpandingTheRoster()
    {
        const int count = int.MaxValue / 5;
        var waves = Enumerable.Range(0, 5).Select(_ => new WaveDefinition
            { Groups = [new() { Count = count }] }).ToArray();

        var plan = WaveSpawnPlanner.Combine(waves);

        Assert.Equal(5 * (long)count * Balance.ZombieHp, plan.HpBudget);
        Assert.True(plan.HpBudget > int.MaxValue);
        Assert.Equal(5 * count, plan.SpawnCount);
        Assert.Single(plan.Groups);
        Assert.Equal(plan.HpBudget, plan.BudgetSpent);
        Assert.Equal(ZombieKind.Basic, WaveSpawnPlanner.SelectGroup(plan, plan.SpawnCount - 1).Kind);
    }

    [Fact]
    public void AnUnrepresentableCombinedCountRejectsTheSelectionAtomically()
    {
        var catalog = SmallCatalog(count: int.MaxValue);
        var game = new GameSession(levels: catalog);
        var level = game.Level;
        var plans = game.PreparedWaves;

        Assert.False(game.SetAllInOne(true));

        Assert.False(game.AllInOne);
        Assert.False(game.MergeFiveLevels);
        Assert.Same(level, game.Level);
        Assert.Same(plans, game.PreparedWaves);
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    [Fact]
    public void BossOnlyFinalWaveUsesLatestNormalEquipmentAndKeepsItsExplicitBoss()
    {
        WaveDefinition[] sources =
        [
            new() { Groups = [new() { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Copper }] },
            new() { Groups = [new() { Kind = ZombieKind.Armored, Tier = GearTier.Osmium, Size = ZombieSize.Mega, IsBoss = true }] }
        ];

        var plan = WaveSpawnPlanner.Combine(sources);

        var normal = Assert.Single(plan.Groups, group => !group.IsBoss);
        Assert.Equal(ZombieKind.ArmedArmored, normal.Kind);
        Assert.Equal(GearTier.Copper, normal.Tier);
        var boss = Assert.Single(plan.Groups, group => group.IsBoss);
        Assert.Equal(GearTier.Osmium, boss.Tier);
        Assert.Equal(ZombieSize.Mega, boss.Size);
        Assert.Equal(130, plan.NormalBudget);
        Assert.Equal(15, plan.BossBudget);
    }

    [Fact]
    public void FinalWaveWithoutExplicitBossesDerivesTheirGearFromItsNormalRoster()
    {
        WaveDefinition[] sources =
        [
            new() { HpBudget = 1000, Groups = [new()] },
            new() { Groups = [new() { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Iron }] }
        ];

        var plan = WaveSpawnPlanner.Combine(sources);

        Assert.Equal(30, plan.BossBudget);
        Assert.Equal(2, plan.Groups.Count(group => group.IsBoss));
        Assert.All(plan.Groups.Where(group => group.IsBoss), group =>
        {
            Assert.Equal(ZombieKind.Armored, group.Kind);
            Assert.Equal(GearTier.Iron, group.Tier);
        });
    }

    private static LevelCatalog SmallCatalog(int count = 1, float interval = 1.4f)
    {
        var catalog = new LevelCatalog();
        for (int level = 1; level <= 15; level++)
            catalog.Set(new LevelDefinition
            {
                Number = level,
                Waves = [new() { SpawnInterval = interval, Groups = [new() { Count = count }] }]
            });
        return catalog;
    }

    private static void FinishBattle(GameSession game)
    {
        foreach (var wave in game.Waves)
        {
            Assert.True(wave.Started);
            while (wave.Spawned < wave.TotalCount) Spawn(game, wave);
            foreach (var zombie in game.Zombies.Where(zombie => zombie.WaveIndex == wave.Index).ToArray())
                game.DamageZombie(zombie, 1_000_000, DamageKind.Chemical);
            game.Update(.001f);
        }
        Assert.Equal(GamePhase.Building, game.Phase);
    }

    private static string Snapshot(WaveSpawnPlan plan) => JsonSerializer.Serialize(new
    {
        plan.Groups, plan.SpawnCount, plan.TotalHp, plan.HpBudget,
        plan.NormalBudget, plan.BossBudget, plan.BudgetSpent
    });

    private static void Spawn(GameSession game, WaveState wave) => Assert.True((bool)Invoke(game, "Spawn", wave)!);
    private static object? Invoke(GameSession game, string method, params object?[] arguments) => typeof(GameSession)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, arguments);
}
