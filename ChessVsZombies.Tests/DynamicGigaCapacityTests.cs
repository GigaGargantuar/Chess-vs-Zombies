using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class DynamicGigaCapacityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFullEncounterRosterCannotAddAnUnchargedGigaBeyondCounterCapacity(bool splitAcrossWaves)
    {
        var levels = new LevelCatalog();
        int firstCount = splitAcrossWaves ? int.MaxValue - 1 : int.MaxValue;
        var level = new LevelDefinition
        {
            Waves = [new WaveDefinition
            {
                EnableGiga = true,
                Groups = [new ZombieGroup { Size = ZombieSize.Micro, Count = firstCount }]
            }]
        };
        if (splitAcrossWaves)
            level.Waves.Add(new WaveDefinition { Groups = [new ZombieGroup { Size = ZombieSize.Micro, Count = 1 }] });
        levels.Set(level);
        var game = new GameSession(123, levels);
        Assert.True(game.StartLevel(), game.Message);
        var wave = game.Waves[0];
        var plan = game.PreparedWaves[0];
        // The Micro roster makes the Giga quote itself fit Int32, isolating
        // the wave-count boundary from the independent cost-storage boundary.
        Assert.NotNull(plan.GigaSpawns);
        Assert.InRange(plan.GigaSpawns.SpawnCost, 523, int.MaxValue);
        Assert.Equal(firstCount, plan.SpawnCount);
        Assert.Equal(firstCount, wave.TotalCount);
        Assert.Equal(int.MaxValue, game.Waves.Sum(candidate => (long)candidate.TotalCount));
        Assert.Equal(int.MaxValue, game.LivingStandbyCount);
        float hp = wave.TotalHp;

        Assert.False((bool)typeof(GameSession).GetMethod("TrySpawnGiga", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(game, [wave])!);

        Assert.Equal(0, game.PendingGigaSpawnCount);
        Assert.Empty(game.Zombies);
        Assert.Equal(0, game.DeathToll.PhysicalSpawns);
        Assert.Equal(0, wave.BudgetSpent);
        Assert.Equal(0, wave.Spawned);
        Assert.Equal(firstCount, wave.TotalCount);
        Assert.Equal(int.MaxValue, game.Waves.Sum(candidate => (long)candidate.TotalCount));
        Assert.Equal(int.MaxValue, game.LivingStandbyCount);
        Assert.Equal(hp, wave.TotalHp);
    }
}
