using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private GigaSpawnPlan?[] _gigaSpawnPlans = [];
    private int[] _gigaSpawnsRemaining = [];

    public int PendingGigaSpawnCount => _gigaSpawnsRemaining.Sum();

    private void ClearGigaSpawns()
    {
        _gigaSpawnPlans = [];
        _gigaSpawnsRemaining = [];
    }

    // Ordinary standby indices deliberately exclude these reserved purchases.
    // Charge the runtime pool only when a fitting body actually activates.
    private void InitializeGigaSpawns(IReadOnlyList<WaveSpawnPlan> plans)
    {
        _gigaSpawnPlans = plans.Select(plan => plan.GigaSpawns).ToArray();
        _gigaSpawnsRemaining = plans.Select(plan => plan.GigaCount).ToArray();
        for (int index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            Waves[index].TotalCount = checked(Waves[index].TotalCount + plan.GigaCount);
            Waves[index].TotalHp += plan.GigaTotalHp;
            if (_largeStandbyWaves[index] is { } large && plan.GigaSpawns is { } giga)
                large.ExactTotalHp += giga.ExactInitialHp * giga.Count;
        }
    }

    private bool TrySpawnGiga(WaveState wave)
    {
        if ((uint)wave.Index >= (uint)_gigaSpawnsRemaining.Length || _gigaSpawnsRemaining[wave.Index] == 0 ||
            _gigaSpawnPlans[wave.Index] is not { } plan ||
            Zombies.Any(zombie => zombie.IsGiga && zombie.BodyHp > 0) ||
            wave.HpBudget - wave.BudgetSpent < plan.SpawnCost) return false;
        if (_spawnWorldRevision != World.Revision || _spawnTopologyRevision != _topologyRevision || _spawnLiquidRevision != Liquids.Revision)
            RefreshSpawnCells();
        var shape = new Zombie { Size = ZombieSize.Giga };
        var available = FindSizedSpawnCells(shape).Where(cell => IsEmpty(cell) && HasZombieSupport(cell, shape) &&
            IsBodyClear(shape.StandAt(cell), shape)).ToArray();
        if (available.Length == 0) return false;
        var cell = available[_spawnRandom.Next(available.Length)];
        var zombie = new Zombie
        {
            Id = _nextId++, WaveIndex = wave.Index, Kind = ZombieKind.Basic, Size = ZombieSize.Giga,
            IsBoss = true, Position = shape.StandAt(cell), OriginalBudgetCost = checked((int)plan.SpawnCost),
            Lane = cell.X == 0 ? 0 : cell.X + shape.GridFootprint == WorldGrid.Size ? 1 : cell.Z == 0 ? 2 : 3
        };
        zombie.InitializeGigaCounter(plan.InitialCounter);
        zombie.FaceToward(new Vector3(16, zombie.Position.Y, 16) - zombie.Position);
        Zombies.Add(zombie);
        _gigaSpawnsRemaining[wave.Index]--;
        wave.Spawned++;
        _internalWaveSpawned[wave.Index]++;
        wave.BudgetSpent += plan.SpawnCost;
        DeathToll.RecordPhysicalSpawn();
        DeathToll.RecordProcessedQueueSlots(1, dead: false);
        InvalidateProjectileColliders();
        return true;
    }

    // Public mutable WaveState is also used by isolated replay fixtures to skip
    // the queue. Remove reserved entries before reconciling ordinary indices.
    private int ReconcileSkippedGigaSpawns(int waveIndex, int skipped)
    {
        if ((uint)waveIndex >= (uint)_gigaSpawnsRemaining.Length) return skipped;
        int gigaSkipped = Math.Min(skipped, _gigaSpawnsRemaining[waveIndex]);
        _gigaSpawnsRemaining[waveIndex] -= gigaSkipped;
        return skipped - gigaSkipped;
    }
}
