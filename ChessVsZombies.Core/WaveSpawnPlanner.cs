namespace ChessVsZombies.Core;

/// <summary>A resolved roster. Purchase budgets are distinct from actual combat HP.</summary>
public sealed class WaveSpawnPlan
{
    public IReadOnlyList<ZombieGroup> Groups { get; }
    public int SpawnCount { get; }
    public float TotalHp { get; }
    public double ExactTotalHp { get; }
    public long HpBudget { get; }
    public long NormalBudget { get; }
    public long BossBudget { get; }
    public long BudgetSpent { get; }

    internal WaveSpawnPlan(List<ZombieGroup> groups, long hpBudget, long normalBudget, long bossBudget)
    {
        Groups = groups.AsReadOnly();
        SpawnCount = checked((int)groups.Sum(group => (long)group.Count));
        TotalHp = groups.Sum(group => (float)group.Count * LevelCatalog.ZombieMaxHp(group.Kind, group.Tier, group.Size));
        ExactTotalHp = groups.Sum(group => (double)group.Count * LevelCatalog.ZombieMaxHp(group.Kind, group.Tier, group.Size));
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

    /// <summary>Spend every included wave's budgets with the final wave's equipment available immediately.</summary>
    public static WaveSpawnPlan Combine(IReadOnlyList<WaveDefinition> waves)
    {
        if (waves.Count == 0) throw new ArgumentException("At least one wave is required.", nameof(waves));
        var plans = waves.Select(Plan).ToArray();
        long normalBudget = plans.Sum(plan => plan.NormalBudget);
        long bossBudget = plans.Sum(plan => plan.BossBudget);
        // A valid authored final wave may contain only bosses. Retain the latest
        // normal composition in that case, alongside the final wave's boss gear.
        var normalTypes = waves.LastOrDefault(wave => wave.Groups.Any(group => !group.IsBoss))?
            .Groups.Where(group => !group.IsBoss).ToList() ?? [];
        var bossTypes = waves[^1].Groups.Where(group => group.IsBoss).ToList();
        if (bossTypes.Count == 0)
        {
            var gear = normalTypes.Where(group => group.Kind != ZombieKind.Basic).MaxBy(group => group.Tier);
            foreach (var size in new[] { ZombieSize.Kilo, ZombieSize.Mega })
                bossTypes.Add(new ZombieGroup
                {
                    Kind = gear == null ? ZombieKind.Basic : ZombieKind.Armored,
                    Tier = gear?.Tier ?? GearTier.Wood, Size = size, IsBoss = true
                });
        }
        var groups = Allocate(normalTypes, normalBudget);
        groups.AddRange(Allocate(bossTypes, bossBudget));
        return new(groups, checked(normalBudget + bossBudget), normalBudget, bossBudget);
    }

    private static List<ZombieGroup> Allocate(IReadOnlyList<ZombieGroup> choices, long budget)
    {
        if (choices.Count == 0 || budget == 0) return [];
        long cycleCost = choices.Sum(group => (long)group.Count * WaveSpawnPlan.BudgetCost(group));
        var counts = new int[choices.Count];
        var target = new double[choices.Count];
        long remaining = budget;
        for (int index = 0; index < choices.Count; index++)
        {
            // Count is a composition weight for budgeted definitions, never a free spawn allowance.
            counts[index] = checked((int)((decimal)choices[index].Count * budget / cycleCost));
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
            counts[selected] = checked(counts[selected] + 1);
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

    // Inverse of SelectGroup. A spatial lane can advance directly to its next
    // roster member without walking the intervening slots of a large wave.
    public static int SlotForGroupOrdinal(WaveSpawnPlan plan, int groupIndex, int groupOrdinal) =>
        new GroupOrdinalIndex(plan, groupIndex).SlotForOrdinal(groupOrdinal);

    internal sealed class GroupOrdinalIndex
    {
        private readonly int _count;
        private readonly int[] _categoryCounts;
        private readonly int _categoryIndex;
        private readonly long _total;
        private readonly long _bossCount;
        private readonly long _normalCount;
        private readonly long _armorCount;
        private readonly bool _boss;
        private readonly bool _armored;

        public GroupOrdinalIndex(WaveSpawnPlan plan, int groupIndex)
        {
            if ((uint)groupIndex >= (uint)plan.Groups.Count) throw new ArgumentOutOfRangeException(nameof(groupIndex));
            var target = plan.Groups[groupIndex];
            _count = target.Count;
            _total = plan.SpawnCount;
            _boss = target.IsBoss;
            _armored = IsArmored(target);
            var counts = new List<int>();
            for (int index = 0; index < plan.Groups.Count; index++)
            {
                var group = plan.Groups[index];
                if (group.IsBoss) _bossCount += group.Count;
                else if (IsArmored(group)) _armorCount += group.Count;
                if (group.IsBoss != _boss || !_boss && IsArmored(group) != _armored) continue;
                if (index == groupIndex) _categoryIndex = counts.Count;
                counts.Add(group.Count);
            }
            _categoryCounts = counts.ToArray();
            _normalCount = _total - _bossCount;
        }

        public int SlotForOrdinal(int groupOrdinal)
        {
            if ((uint)groupOrdinal >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(groupOrdinal));
            if (_categoryCounts.Length == 1 && _total == _count) return groupOrdinal;
            long categoryOrdinal = 0;
            for (int index = 0; index < _categoryCounts.Length; index++)
            {
                categoryOrdinal += Math.Min(groupOrdinal, _categoryCounts[index]);
                if (index < _categoryIndex && _categoryCounts[index] > groupOrdinal) categoryOrdinal++;
            }
            if (_boss)
                return checked((int)(CeilingDivide((categoryOrdinal + 1) * _total - _total / 2, _bossCount) - 1));

            long normalOrdinal = _armored
                ? CeilingDivide((categoryOrdinal + 1) * _normalCount, _armorCount) - 1
                : categoryOrdinal * _normalCount / (_normalCount - _armorCount);
            if (_bossCount == 0) return checked((int)normalOrdinal);
            long low = 1, high = _total;
            while (low < high)
            {
                long middle = low + (high - low) / 2;
                long normals = middle - (middle * _bossCount + _total / 2) / _total;
                if (normals > normalOrdinal) high = middle;
                else low = middle + 1;
            }
            return checked((int)(low - 1));
        }

        private static long CeilingDivide(long numerator, long denominator) =>
            (numerator + denominator - 1) / denominator;
    }

    private static bool IsArmored(ZombieGroup group) => group.Kind is ZombieKind.Armored or ZombieKind.ArmedArmored;

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
