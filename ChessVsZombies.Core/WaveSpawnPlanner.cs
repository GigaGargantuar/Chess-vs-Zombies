namespace ChessVsZombies.Core;

/// <summary>Interleaves armor throughout the exact authored roster without expanding large waves.</summary>
public static class WaveSpawnPlanner
{
    public static ZombieGroup SelectGroup(WaveDefinition wave, int spawnIndex)
    {
        long total = wave.Groups.Sum(group => (long)group.Count);
        if (spawnIndex < 0 || spawnIndex >= total) throw new ArgumentOutOfRangeException(nameof(spawnIndex));
        static bool Armored(ZombieGroup group) => group.Kind is ZombieKind.Armored or ZombieKind.ArmedArmored;
        long armorCount = wave.Groups.Where(Armored).Sum(group => (long)group.Count);
        long before = spawnIndex * armorCount / total;
        bool armorSlot = (spawnIndex + 1L) * armorCount / total > before;
        long ordinal = armorSlot ? before : spawnIndex - before;
        var groups = wave.Groups.Where(group => Armored(group) == armorSlot).ToList();
        // Rotate through the kinds in each category, skipping a group when its quota is exhausted.
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
