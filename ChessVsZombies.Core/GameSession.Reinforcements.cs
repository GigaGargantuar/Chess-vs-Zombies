using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private readonly List<SavedReturningZombie> _returningZombies = [];

    public int PendingReinforcementCount => _returningZombies.Count;
    public long PendingReinforcementBudget => _returningZombies.Sum(zombie => (long)zombie.OriginalBudgetCost);
    public long PendingBossReinforcementBudget => _returningZombies.Where(zombie => zombie.IsBoss)
        .Sum(zombie => (long)zombie.OriginalBudgetCost);

    private GearTier? BestAvailableArmorTier(int waveIndex)
    {
        GearTier? best = LevelCatalog.HighestAvailableArmorTier(GetSourceLevelNumber(waveIndex));
        if (waveIndex >= 0 && waveIndex < Level.Waves.Count)
            foreach (var group in Level.Waves[waveIndex].Groups)
                if (group.Kind != ZombieKind.Basic && (!best.HasValue || group.Tier > best)) best = group.Tier;
        return best;
    }

    private void DeferZombieToNextWave(Zombie zombie)
    {
        if (zombie.Hp <= 0 || !Zombies.Contains(zombie)) return;
        int targetWave = zombie.WaveIndex + 1;
        int targetLevel = LevelNumber;
        if (targetWave < 0 || targetWave >= Waves.Count) { targetLevel = checked(LevelNumber + LevelSpan); targetWave = 0; }
        var held = _kiloBlocks.GetValueOrDefault(zombie.Id);
        var saved = new SavedReturningZombie
        {
            TargetLevel = targetLevel, TargetWaveIndex = targetWave,
            Id = zombie.Id, Kind = zombie.Kind, Tier = zombie.Tier, Size = zombie.Size, IsBoss = zombie.IsBoss,
            BodyHp = zombie.BodyHp, RetreatReturns = zombie.RetreatReturns,
            OriginalBudgetCost = zombie.OriginalBudgetCost > 0 ? zombie.OriginalBudgetCost : WaveSpawnPlan.BudgetCost(
                new ZombieGroup { Kind = zombie.Kind, Tier = zombie.Tier, Size = zombie.Size, IsBoss = zombie.IsBoss }),
            CarriesBucket = zombie.CarriesBucket, CarriedBlocks = zombie.CarriedBlocks,
            Armor = zombie.ArmorPieces.Select(piece => new SavedZombieArmor
            {
                Slot = piece.Slot, Tier = piece.Tier, Hp = piece.Hp, HeatProtectionRemaining = piece.HeatProtectionRemaining
            }).ToList(),
            Buckets = zombie.BucketLoads.Take(zombie.CarriesBucket ? zombie.BucketCount : 0)
                .Select(load => new SavedZombieBucket { Units = load.Units, Kind = load.Kind }).ToList(),
            HeldBlocks = held?.Select(block => new SavedKiloBlock
            {
                BlockIndex = block.BlockIndex, Material = block.Material, Hp = block.Hp, WasBarrier = block.WasBarrier,
                OffsetX = block.Offset.X, OffsetY = block.Offset.Y, OffsetZ = block.Offset.Z
            }).ToList() ?? []
        };
        _returningZombies.Add(saved);
        if (targetLevel == LevelNumber) AddReinforcementBudget(Waves[targetWave], saved);
        if (zombie.WaveIndex >= 0 && zombie.WaveIndex < Waves.Count)
        {
            var oldWave = Waves[zombie.WaveIndex];
            // Remaining life leaves this wave; it is neither damage nor a kill.
            oldWave.TotalHp = Math.Max(oldWave.DamageTaken, oldWave.TotalHp - zombie.Hp);
            if (_largeStandbyWaves.Count > zombie.WaveIndex && _largeStandbyWaves[zombie.WaveIndex] is { } large)
                large.ExactTotalHp = Math.Max(large.ExactDamageTaken, large.ExactTotalHp - zombie.Hp);
        }
        ClearZombieMovement(zombie);
        _kiloBlocks.Remove(zombie.Id);
        _kiloActionCooldowns.Remove(zombie.Id);
        _megaActionCooldowns.Remove(zombie.Id);
        _megaGuardMemory.Remove(zombie.Id);
        _microStates.Remove(zombie.Id);
        zombie.MicroDashRemaining = 0;
        _hurledAllies.Remove(zombie.Id);
        _fallStartHeights.Remove(zombie.Id);
        _coverWaiting.Remove(zombie.Id);
        _coverGroups.Remove(zombie.Id);
        _coverAdvanceOrigins.Remove(zombie.Id);
        _builderYields.Remove(zombie.Id);
        Zombies.Remove(zombie);
    }

    private static float ReinforcementHp(SavedReturningZombie zombie) => zombie.BodyHp + zombie.Armor.Sum(piece => piece.Hp);

    private void AddReinforcementBudget(WaveState wave, SavedReturningZombie zombie)
    {
        wave.TotalCount = checked(wave.TotalCount + 1);
        wave.TotalHp += ReinforcementHp(zombie);
        if (_largeStandbyWaves.Count > wave.Index && _largeStandbyWaves[wave.Index] is { } large)
            large.ExactTotalHp += ReinforcementHp(zombie);
        wave.HpBudget += zombie.OriginalBudgetCost;
        if (zombie.IsBoss) wave.BossBudget += zombie.OriginalBudgetCost;
        else wave.NormalBudget += zombie.OriginalBudgetCost;
    }

    private bool TrySpawnReinforcement(WaveState wave, out bool pending)
    {
        var saved = _returningZombies.FirstOrDefault(zombie => zombie.TargetLevel == LevelNumber && zombie.TargetWaveIndex == wave.Index);
        pending = saved != null;
        if (saved == null) return false;
        var shape = new Zombie { Size = saved.Size };
        var available = FindSizedSpawnCells(shape).Where(cell => IsEmpty(cell) && HasZombieSupport(cell, shape) &&
            IsBodyClear(shape.StandAt(cell), shape)).ToArray();
        if (available.Length == 0) return false;
        var cell = available[_random.Next(available.Length)];
        var zombie = new Zombie
        {
            Id = saved.Id, WaveIndex = wave.Index, Kind = saved.Kind, Tier = saved.Tier, Size = saved.Size,
            IsBoss = saved.IsBoss, Position = shape.StandAt(cell),
            Attack = LevelCatalog.ZombieAttack(saved.Kind, saved.Tier, saved.Size),
            CarriesBucket = saved.CarriesBucket, CarriedBlocks = saved.CarriedBlocks,
            OriginalBudgetCost = saved.OriginalBudgetCost, RetreatReturns = 0,
            Lane = cell.X == 0 ? 0 : cell.X + shape.GridFootprint == WorldGrid.Size ? 1 : cell.Z == 0 ? 2 : 3
        };
        zombie.RestoreBodyAndArmor(saved.BodyHp, saved.Armor.Select(piece => new ArmorPiece
        {
            Slot = piece.Slot, Tier = piece.Tier, Hp = piece.Hp, HeatProtectionRemaining = piece.HeatProtectionRemaining,
            DurabilityMultiplier = zombie.ArmorDurabilityMultiplier
        }));
        for (int i = 0; i < saved.Buckets.Count; i++)
        {
            zombie.BucketLoads[i].Units = saved.Buckets[i].Units;
            zombie.BucketLoads[i].Kind = saved.Buckets[i].Kind;
        }
        if (saved.HeldBlocks.Count > 0)
            _kiloBlocks[zombie.Id] = saved.HeldBlocks.Select(block => new HeldKiloBlock(block.BlockIndex, block.Material, block.Hp, block.WasBarrier)
            { Offset = new Vector3(block.OffsetX, block.OffsetY, block.OffsetZ) }).ToList();
        zombie.FaceToward(new Vector3(16, zombie.Position.Y, 16) - zombie.Position);
        Zombies.Add(zombie);
        DeathToll.RecordPhysicalSpawn();
        _nextId = Math.Max(_nextId, zombie.Id + 1);
        _returningZombies.Remove(saved);
        wave.Spawned++;
        wave.ReinforcementsSpawned++;
        wave.BudgetSpent += saved.OriginalBudgetCost;
        return true;
    }
}
