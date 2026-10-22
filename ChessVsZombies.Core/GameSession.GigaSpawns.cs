using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private bool[] _gigaWavesEnabled = [];

    // Eligibility is evaluated again at the normal spawn cadence. An active
    // Giga blocks activation, while its wave can still fund a later purchase.
    public int PendingGigaSpawnCount => Waves.Count(HasPendingGigaPurchase);

    private void ClearGigaSpawns()
    {
        _gigaWavesEnabled = [];
    }

    // Gigas are new purchases from the same wallet as ordinary arrivals. Their
    // initial preview quote reserves neither future purchases nor combat HP.
    private void InitializeGigaSpawns(IReadOnlyList<WaveSpawnPlan> plans)
    {
        _gigaWavesEnabled = plans.Select(plan => plan.GigaEnabled).ToArray();
    }

    private bool IsGigaSpawnEnabled(WaveState wave) =>
        (uint)wave.Index < (uint)_gigaWavesEnabled.Length && _gigaWavesEnabled[wave.Index];

    private bool HasPendingGigaPurchase(WaveState wave) => TryQuoteGiga(wave, out _, out _);

    private bool TryQuoteGiga(WaveState wave, out int counter, out long cost)
    {
        counter = 0;
        cost = 0;
        if (!IsGigaSpawnEnabled(wave) || wave.TotalCount >= int.MaxValue ||
            Waves.Sum(candidate => (long)candidate.TotalCount) >= int.MaxValue) return false;
        long remaining = wave.HpBudget - wave.BudgetSpent;
        if (remaining < GigaRules.SpawnCost(GigaRules.MinimumInitialCounter)) return false;
        counter = GigaRules.SelectInitialCounter(remaining);
        cost = GigaRules.SpawnCost(counter);
        return cost <= remaining && cost <= int.MaxValue;
    }

    private bool TrySpawnGiga(WaveState wave)
    {
        if (Zombies.Any(zombie => zombie.IsGiga && zombie.BodyHp > 0) ||
            !TryQuoteGiga(wave, out int counter, out long cost)) return false;
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
            IsBoss = true, Position = shape.StandAt(cell), OriginalBudgetCost = checked((int)cost),
            Lane = cell.X == 0 ? 0 : cell.X + shape.GridFootprint == WorldGrid.Size ? 1 : cell.Z == 0 ? 2 : 3
        };
        zombie.InitializeGigaCounter(counter);
        zombie.FaceToward(new Vector3(16, zombie.Position.Y, 16) - zombie.Position);
        Zombies.Add(zombie);
        wave.TotalCount = checked(wave.TotalCount + 1);
        wave.Spawned++;
        _internalWaveSpawned[wave.Index]++;
        wave.TotalHp += zombie.Hp;
        if (_largeStandbyWaves[wave.Index] is { } large) large.ExactTotalHp += zombie.Hp;
        wave.BudgetSpent += cost;
        RecordCombatHealthPurchase(zombie, wave);
        DeathToll.RecordPhysicalSpawn();
        DeathToll.RecordProcessedQueueSlots(1, dead: false);
        InvalidateProjectileColliders();
        return true;
    }

    // A replay fixture that explicitly skips the whole wave must also suppress
    // later dynamic purchases. Gigas have no reserved ordinary queue indices.
    private int ReconcileSkippedGigaSpawns(int waveIndex, int skipped)
    {
        if ((uint)waveIndex < (uint)_gigaWavesEnabled.Length &&
            Waves[waveIndex].Spawned >= Waves[waveIndex].TotalCount)
            _gigaWavesEnabled[waveIndex] = false;
        return skipped;
    }
}
