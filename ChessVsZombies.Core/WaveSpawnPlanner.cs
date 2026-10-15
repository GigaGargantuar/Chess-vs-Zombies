namespace ChessVsZombies.Core;

/// <summary>A resolved roster. Purchase budgets are distinct from actual combat HP.</summary>
public sealed class WaveSpawnPlan
{
    public IReadOnlyList<ZombieGroup> Groups { get; }
    public int SpawnCount { get; }
    public float TotalHp { get; }
    public long HpBudget { get; }
    public long NormalBudget { get; }
    public long BossBudget { get; }
    public long BudgetSpent { get; }

    internal WaveSpawnPlan(List<ZombieGroup> groups, long hpBudget, long normalBudget, long bossBudget)
    {
        Groups = groups.AsReadOnly();
        SpawnCount = checked((int)groups.Sum(group => (long)group.Count));
        TotalHp = groups.Sum(group => (float)group.Count * LevelCatalog.ZombieMaxHp(group.Kind, group.Tier, group.Size));
        HpBudget = hpBudget;
        NormalBudget = normalBudget;
        BossBudget = bossBudget;
        BudgetSpent = groups.Sum(group => (long)group.Count * BudgetCost(group));
    }

    public static int BudgetCost(ZombieGroup group) => group.IsBoss ?
        group.Size == ZombieSize.Mega ? Balance.ZombieHp * 3 / 2 : Balance.ZombieHp :
        LevelCatalog.ZombieMaxHp(group.Kind, group.Tier, group.Size);
}

/// <summary>Resolves HP purchases and interleaves exact quotas without expanding large waves.</summary>
public static class WaveSpawnPlanner
{
    public static WaveSpawnPlan Plan(WaveDefinition wave)
    {
        ArgumentNullException.ThrowIfNull(wave);
        LevelCatalog.Validate(new LevelDefinition { Waves = [wave] });
        if (wave.HpBudget is not { } budget)
        {
            var exact = wave.Groups.Select(Copy).ToList();
            long normal = exact.Where(group => !group.IsBoss).Sum(group => (long)group.Count * WaveSpawnPlan.BudgetCost(group));
            long bosses = exact.Where(group => group.IsBoss).Sum(group => (long)group.Count * WaveSpawnPlan.BudgetCost(group));
            return new(exact, normal + bosses, normal, bosses);
        }

        // Decimal conversion keeps a nominal 1%, 3%, or 5% reserve from losing one HP to float rounding.
        int bossBudget = (int)decimal.Floor(budget * (decimal)wave.BossBudgetFraction);
        int normalBudget = budget - bossBudget;
        var groups = Allocate(wave.Groups.Where(group => !group.IsBoss).ToList(), normalBudget);
        var bossTypes = wave.Groups.Where(group => group.IsBoss).ToList();
        if (bossTypes.Count == 0)
        {
            var gear = wave.Groups.Where(group => !group.IsBoss && group.Kind != ZombieKind.Basic)
                .MaxBy(group => group.Tier);
            bossTypes.Add(new ZombieGroup
            {
                Kind = gear == null ? ZombieKind.Basic : ZombieKind.Armored,
                Tier = gear?.Tier ?? GearTier.Wood, Size = ZombieSize.Kilo, IsBoss = true
            });
            bossTypes.Add(new ZombieGroup
            {
                Kind = gear == null ? ZombieKind.Basic : ZombieKind.Armored,
                Tier = gear?.Tier ?? GearTier.Wood, Size = ZombieSize.Mega, IsBoss = true
            });
        }
        groups.AddRange(Allocate(bossTypes, bossBudget));
        return new(groups, budget, normalBudget, bossBudget);
    }

    private static List<ZombieGroup> Allocate(IReadOnlyList<ZombieGroup> choices, int budget)
    {
        if (choices.Count == 0 || budget == 0) return [];
        long cycleCost = choices.Sum(group => (long)group.Count * WaveSpawnPlan.BudgetCost(group));
        var counts = new int[choices.Count];
        var target = new double[choices.Count];
        long remaining = budget;
        for (int index = 0; index < choices.Count; index++)
        {
            // Count is a composition weight for budgeted definitions, never a free spawn allowance.
            counts[index] = (int)((long)choices[index].Count * budget / cycleCost);
            target[index] = (double)choices[index].Count * budget / cycleCost;
            remaining -= (long)counts[index] * WaveSpawnPlan.BudgetCost(choices[index]);
        }
        while (true)
        {
            int selected = -1;
            double largestDeficit = double.NegativeInfinity;
            for (int index = 0; index < choices.Count; index++)
            {
                if (WaveSpawnPlan.BudgetCost(choices[index]) > remaining) continue;
                double deficit = target[index] - counts[index];
                if (deficit <= largestDeficit) continue;
                selected = index;
                largestDeficit = deficit;
            }
            if (selected < 0) break;
            counts[selected]++;
            remaining -= WaveSpawnPlan.BudgetCost(choices[selected]);
        }
        var result = new List<ZombieGroup>();
        for (int index = 0; index < choices.Count; index++)
            if (counts[index] > 0)
            {
                var group = Copy(choices[index]);
                group.Count = counts[index];
                result.Add(group);
            }
        return result;
    }

    private static ZombieGroup Copy(ZombieGroup group) => new()
    {
        Kind = group.Kind, Tier = group.Tier, Count = group.Count, Size = group.Size, IsBoss = group.IsBoss
    };

    public static ZombieGroup SelectGroup(WaveDefinition wave, int spawnIndex) => SelectGroup(Plan(wave), spawnIndex);

    public static ZombieGroup SelectGroup(WaveSpawnPlan plan, int spawnIndex)
    {
        if (spawnIndex < 0 || spawnIndex >= plan.SpawnCount) throw new ArgumentOutOfRangeException(nameof(spawnIndex));
        long total = plan.SpawnCount;
        long bossCount = plan.Groups.Where(group => group.IsBoss).Sum(group => (long)group.Count);
        // Center rare bosses in the normal spawn stream instead of stacking them at the end.
        long before = (spawnIndex * bossCount + total / 2) / total;
        bool bossSlot = ((spawnIndex + 1L) * bossCount + total / 2) / total > before;
        long ordinal = bossSlot ? before : spawnIndex - before;
        if (bossSlot) return SelectByQuota(plan.Groups.Where(group => group.IsBoss).ToList(), ordinal);

        var normal = plan.Groups.Where(group => !group.IsBoss).ToList();
        long normalCount = total - bossCount;
        static bool Armored(ZombieGroup group) => group.Kind is ZombieKind.Armored or ZombieKind.ArmedArmored;
        long armorCount = normal.Where(Armored).Sum(group => (long)group.Count);
        long armorBefore = ordinal * armorCount / normalCount;
        bool armorSlot = (ordinal + 1L) * armorCount / normalCount > armorBefore;
        long categoryOrdinal = armorSlot ? armorBefore : ordinal - armorBefore;
        return SelectByQuota(normal.Where(group => Armored(group) == armorSlot).ToList(), categoryOrdinal);
    }

    private static ZombieGroup SelectByQuota(List<ZombieGroup> groups, long ordinal)
    {
        long completedRounds = 0;
        while (groups.Count > 0)
        {
            long rounds = groups.Min(group => group.Count) - completedRounds;
            long batch = rounds * groups.Count;
            if (ordinal < batch) return groups[(int)(ordinal % groups.Count)];
            ordinal -= batch;
            completedRounds += rounds;
            groups.RemoveAll(group => group.Count <= completedRounds);
        }
        throw new InvalidOperationException("Wave roster is inconsistent.");
    }
}
