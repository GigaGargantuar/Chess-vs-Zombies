namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private bool CanAffordStandbySpawn(StandbySpawn slot, WaveState wave) =>
        !IsGigaSpawnEnabled(wave) || WaveSpawnPlan.BudgetCost(slot.Group) <= wave.HpBudget - wave.BudgetSpent;

    // Retiring a purchase never heals another queued actor or reports a kill.
    // The original slot remains identifiable until its normal queue completion.
    private bool CancelUnaffordableStandby(StandbySpawn slot, WaveState wave)
    {
        if (slot.Consumed || CanAffordStandbySpawn(slot, wave)) return false;
        if (slot.BodyHp > 0)
        {
            float survivingHp = slot.Hp;
            wave.TotalHp = Math.Max(wave.DamageTaken, wave.TotalHp - survivingHp);
            _livingStandbyCount--;
            if (_largeStandbyWaves[wave.Index] is { } large)
            {
                large.ExactTotalHp = Math.Max(large.ExactDamageTaken, large.ExactTotalHp - survivingHp);
                large.LivingCount--;
            }
        }
        // Keep its surviving HP intact so completion classifies this as a
        // cancelled living purchase rather than a scheduled queue death.
        CompleteStandbySlot(slot, wave);
        return true;
    }

    private bool RetireUnfundedStandby(WaveState wave)
    {
        if (!IsGigaSpawnEnabled(wave) || (uint)wave.Index >= (uint)_wavePlans.Count) return false;
        var plan = _wavePlans[wave.Index];
        long remaining = wave.HpBudget - wave.BudgetSpent;
        // The common, well-funded case needs no scan of a large wave's fronts.
        if (plan.Groups.Count == 0 || remaining >= plan.Groups.Max(WaveSpawnPlan.BudgetCost)) return false;
        if (_largeStandbyWaves[wave.Index] is not { } large)
        {
            int? cheapest = null;
            foreach (var slot in _standbyWaves[wave.Index])
                if (!slot.Consumed && slot.BodyHp > 0)
                {
                    int cost = WaveSpawnPlan.BudgetCost(slot.Group);
                    cheapest = cheapest.HasValue ? Math.Min(cheapest.Value, cost) : cost;
                }
            if (!cheapest.HasValue || remaining >= cheapest.Value) return false;
            bool retired = false;
            foreach (var slot in _standbyWaves[wave.Index])
                if (!slot.Consumed) retired |= CancelUnaffordableStandby(slot, wave);
            return retired;
        }
        if (large.Abandoned) return false;
        int? cheapestLarge = null;
        foreach (var front in large.Fronts.Values)
            if (!front.Consumed && front.BodyHp > 0)
            {
                int cost = WaveSpawnPlan.BudgetCost(front.Group);
                cheapestLarge = cheapestLarge.HasValue ? Math.Min(cheapestLarge.Value, cost) : cost;
            }
        foreach (var lane in large.Lanes)
            if (lane.Positions.Length == 0 && lane.Group.Count > 0 &&
                !large.IsCompleted(lane.SlotForOrdinal(lane.Group.Count - 1)))
            {
                int cost = WaveSpawnPlan.BudgetCost(lane.Group);
                cheapestLarge = cheapestLarge.HasValue ? Math.Min(cheapestLarge.Value, cost) : cost;
            }
        // If only dead scheduled entries remain, ordinary queue completion can
        // process them without inventing a living purchase to cancel.
        if (!cheapestLarge.HasValue || remaining >= cheapestLarge.Value) return false;

        int unprocessed = plan.SpawnCount - large.CountCompletedBelow(plan.SpawnCount);
        int living = large.LivingCount;
        double activeHp = Zombies.Where(zombie => zombie.WaveIndex == wave.Index && zombie.BodyHp > 0)
            .Sum(zombie => (double)zombie.Hp);
        double returningHp = _returningZombies.Where(zombie => zombie.TargetLevel == LevelNumber && zombie.TargetWaveIndex == wave.Index)
            .Sum(zombie => (double)ReinforcementHp(zombie));
        double survivingQueueHp = Math.Max(0, large.ExactTotalHp - large.ExactDamageTaken - activeHp - returningHp);
        wave.TotalHp = Math.Max(wave.DamageTaken, (float)(wave.TotalHp - survivingQueueHp));
        large.ExactTotalHp = Math.Max(large.ExactDamageTaken, large.ExactTotalHp - survivingQueueHp);
        large.Abandoned = true;
        large.Completed.AddPrefix(plan.SpawnCount);
        large.DeadDuePrefix = plan.SpawnCount;
        large.LivingCount = 0;
        _livingStandbyCount -= living;
        _scheduledNormalSlots[wave.Index] = plan.SpawnCount;
        wave.Spawned = checked(wave.Spawned + unprocessed);
        _internalWaveSpawned[wave.Index] = checked(_internalWaveSpawned[wave.Index] + unprocessed);
        foreach (var slot in large.Fronts.Values.ToArray())
        {
            slot.Consumed = true;
            AdvanceStandby(slot);
        }
        large.DueWork.Clear();
        large.ActiveStacks.Clear();
        _waitingStandby[wave.Index].Clear();
        if (living > 0) DeathToll.RecordProcessedQueueSlots(living, dead: false);
        int alreadyDead = unprocessed - living;
        if (alreadyDead > 0) DeathToll.RecordProcessedQueueSlots(alreadyDead, dead: true);
        InvalidateProjectileColliders();
        return unprocessed > 0;
    }
}
