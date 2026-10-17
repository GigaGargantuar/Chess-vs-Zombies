using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    // A chunk is allocated only after a slot in it dies or completes ahead of
    // the schedule. Even a fully marked 18-million-slot wave needs under 3 MB
    // of bit data, rather than millions of individual records.
    private sealed class SparseSlotBits
    {
        private readonly Dictionary<int, ulong[]> _chunks = [];
        public int Prefix { get; private set; }
        public bool Contains(int slot) => slot < Prefix || _chunks.TryGetValue(slot >> 12, out var words) &&
            (words[(slot >> 6) & 63] & (1UL << (slot & 63))) != 0;
        public void Add(int slot)
        {
            if (slot < Prefix) return;
            int chunk = slot >> 12;
            if (!_chunks.TryGetValue(chunk, out var words)) _chunks.Add(chunk, words = new ulong[64]);
            words[(slot >> 6) & 63] |= 1UL << (slot & 63);
        }
        public void AddPrefix(int exclusiveEnd) => Prefix = Math.Max(Prefix, exclusiveEnd);

        public int CountBelow(int exclusiveEnd)
        {
            long count = Math.Min(Prefix, exclusiveEnd);
            foreach (var (chunk, words) in _chunks)
                for (int index = 0; index < words.Length; index++)
                {
                    long start = ((long)chunk << 12) + (index << 6);
                    if (start >= exclusiveEnd || start + 64 <= Prefix) continue;
                    ulong mask = BitMask(start, Prefix, exclusiveEnd);
                    count += BitOperations.PopCount(words[index] & mask);
                }
            return checked((int)count);
        }

        public int CountUncompletedBelow(int exclusiveEnd, SparseSlotBits completed)
        {
            long count = 0;
            foreach (var (chunk, words) in _chunks)
                for (int index = 0; index < words.Length; index++)
                {
                    long start = ((long)chunk << 12) + (index << 6);
                    if (start >= exclusiveEnd || start + 64 <= completed.Prefix) continue;
                    ulong mask = BitMask(start, completed.Prefix, exclusiveEnd);
                    ulong completedWord = completed._chunks.TryGetValue(chunk, out var other) ? other[index] : 0;
                    count += BitOperations.PopCount(words[index] & ~completedWord & mask);
                }
            return checked((int)count);
        }

        private static ulong BitMask(long start, int lower, int upper)
        {
            int left = (int)Math.Clamp(lower - start, 0, 64);
            int right = (int)Math.Clamp(upper - start, 0, 64);
            ulong aboveLeft = left == 64 ? 0 : ulong.MaxValue << left;
            ulong belowRight = right == 64 ? ulong.MaxValue : (1UL << right) - 1;
            return aboveLeft & belowRight;
        }
    }

    private sealed class LargeGroupLane(WaveSpawnPlan plan, ZombieGroup group, Cell[] positions, int waveIndex, int groupIndex, int seed)
    {
        public ZombieGroup Group { get; } = group;
        public Cell[] Positions { get; private set; } = positions;
        public int WaveIndex { get; } = waveIndex;
        public int GroupIndex { get; } = groupIndex;
        public Zombie Shape { get; } = new() { Size = group.Size };
        private readonly WaveSpawnPlanner.GroupOrdinalIndex _slotIndex = new(plan, groupIndex);
        public int SlotForOrdinal(int ordinal) => _slotIndex.SlotForOrdinal(ordinal);
        private readonly int _seed = seed;
        private sealed record ShuffledBlock(long Number, int[] Order, int[] Inverse);
        private ShuffledBlock? _recent;
        private ShuffledBlock? _previous;
        public void SetPositions(Cell[] positions)
        {
            if (Positions.Length != 0 || positions.Length == 0) throw new InvalidOperationException();
            Positions = positions;
            _recent = _previous = null;
        }

        // The seed, group, wave and block determine a complete Fisher-Yates
        // shuffle. Every valid cell appears exactly once per block. Two small
        // cached shuffles bound both memory and successor lookup work.
        private ShuffledBlock Permutation(long block)
        {
            if (_recent?.Number == block) return _recent;
            if (_previous?.Number == block) return _previous;
            int count = Positions.Length;
            ulong key = unchecked((ulong)(uint)_seed) ^ ((ulong)(uint)WaveIndex << 32) ^
                ((ulong)(uint)GroupIndex * 0x9E3779B97F4A7C15UL) ^
                (unchecked((ulong)block) * 0xD6E8FEB86659FD93UL);
            var order = Enumerable.Range(0, count).ToArray();
            for (int index = count - 1; index > 0; index--)
            {
                ulong range = (ulong)(index + 1);
                ulong threshold = unchecked(0UL - range) % range;
                do key = Mix64(key); while (key < threshold);
                int selected = (int)(key % range);
                (order[index], order[selected]) = (order[selected], order[index]);
            }
            var inverse = new int[count];
            for (int index = 0; index < count; index++) inverse[order[index]] = index;
            _previous = _recent;
            return _recent = new ShuffledBlock(block, order, inverse);
        }

        public int PositionIndex(int ordinal)
        {
            int count = Positions.Length;
            return Permutation(ordinal / count).Order[ordinal % count];
        }

        public int? FirstOrdinalAtPosition(int positionIndex, int afterOrdinal)
        {
            int count = Positions.Length;
            long block = afterOrdinal < 0 ? 0 : (long)afterOrdinal / count;
            for (int attempt = 0; attempt < 2; attempt++, block++)
            {
                int within = Permutation(block).Inverse[positionIndex];
                long ordinal = block * count + within;
                if (ordinal > afterOrdinal && ordinal < Group.Count) return (int)ordinal;
            }
            return null;
        }

        private static ulong Mix64(ulong value)
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }

    }

    private sealed class LargeStandbyWave(WaveSpawnPlan plan)
    {
        public WaveSpawnPlan Plan { get; } = plan;
        public List<LargeGroupLane> Lanes { get; } = [];
        public Dictionary<int, StandbySpawn> Fronts { get; } = [];
        public HashSet<StandbyStack> ActiveStacks { get; } = [];
        public PriorityQueue<StandbySpawn, int> DueWork { get; } = new();
        public SparseSlotBits Dead { get; } = new();
        public SparseSlotBits Completed { get; } = new();
        public int DeadDuePrefix { get; set; }
        public int LivingCount { get; set; } = plan.SpawnCount;
        public double ExactTotalHp { get; set; } = plan.ExactTotalHp;
        public double ExactDamageTaken { get; set; }
        public bool Abandoned { get; set; }
        public long UnplacedWorldRevision { get; set; } = -1;
        public long UnplacedTopologyRevision { get; set; } = -1;
        public long UnplacedLiquidRevision { get; set; } = -1;
        public bool IsCompleted(int slot) => Completed.Contains(slot) ||
            slot < DeadDuePrefix && Dead.Contains(slot);
        public int CountCompletedBelow(int exclusiveEnd) => Completed.CountBelow(exclusiveEnd) +
            Dead.CountUncompletedBelow(Math.Min(exclusiveEnd, DeadDuePrefix), Completed);
    }

    private LargeStandbyWave PrepareLargeStandbyWave(WaveSpawnPlan plan, int waveIndex,
        Dictionary<ZombieSize, Cell[]> candidates, Dictionary<ZombieSize, Zombie> shapes)
    {
        var large = new LargeStandbyWave(plan);
        large.ExactTotalHp += _returningZombies.Where(zombie => zombie.TargetLevel == LevelNumber &&
            zombie.TargetWaveIndex == waveIndex).Sum(zombie => (double)ReinforcementHp(zombie));
        for (int groupIndex = 0; groupIndex < plan.Groups.Count; groupIndex++)
        {
            var group = plan.Groups[groupIndex];
            if (!shapes.TryGetValue(group.Size, out var shape))
                shapes.Add(group.Size, shape = new Zombie { Size = group.Size });
            if (!candidates.TryGetValue(group.Size, out var positions))
            {
                positions = FindSizedSpawnCells(shape).Where(cell => IsEmpty(cell) &&
                    HasZombieSupport(cell, shape) && IsBodyClear(shape.StandAt(cell), shape)).ToArray();
                if (positions.Length != 0) candidates.Add(group.Size, positions);
            }
            var lane = new LargeGroupLane(plan, group, positions, waveIndex, groupIndex, _seed);
            large.Lanes.Add(lane);
            for (int positionIndex = 0; positionIndex < positions.Length; positionIndex++)
                if (NextAvailableLargeOrdinal(large, lane, positionIndex, -1) is { } ordinal)
                    IndexLargeStandby(large, lane, ordinal, positionIndex, shape);
        }
        return large;
    }

    private void IndexLargeStandby(LargeStandbyWave large, LargeGroupLane lane, int ordinal,
        int positionIndex, Zombie? shape = null)
    {
        shape ??= lane.Shape;
        int slotIndex = lane.SlotForOrdinal(ordinal);
        var slot = new StandbySpawn(lane.Group, lane.Positions[positionIndex], lane.WaveIndex, slotIndex)
        {
            GroupIndex = lane.GroupIndex, GroupOrdinal = ordinal, PositionIndex = positionIndex,
            Due = slotIndex < _scheduledNormalSlots[lane.WaveIndex]
        };
        if (shape.IsGiant) slot.CoverHp = Balance.BlockHp(lane.Group.Kind == ZombieKind.Basic
            ? Material.Dirt : new Zombie { Kind = lane.Group.Kind, Tier = lane.Group.Tier }.CarriedBlockMaterial);
        large.Fronts.Add(slotIndex, slot);
        var position = shape.StandAt(slot.Cell!.Value);
        IndexStandby(slot, position, shape);
        large.ActiveStacks.Add(_standbyStacks[position]);
    }

    private void AdvanceLargeStandby(LargeStandbyWave large, StandbySpawn slot)
    {
        large.Fronts.Remove(slot.Slot);
        if (large.Abandoned) return;
        var lane = large.Lanes[slot.GroupIndex];
        if (NextAvailableLargeOrdinal(large, lane, slot.PositionIndex, slot.GroupOrdinal) is { } next)
            IndexLargeStandby(large, lane, next, slot.PositionIndex);
    }

    private static int? NextAvailableLargeOrdinal(LargeStandbyWave large, LargeGroupLane lane,
        int positionIndex, int afterOrdinal)
    {
        int after = afterOrdinal;
        if (large.Completed.Prefix > 0)
        {
            int low = after + 1, high = lane.Group.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (lane.SlotForOrdinal(middle) < large.Completed.Prefix)
                    low = middle + 1;
                else high = middle;
            }
            after = low - 1;
        }
        while (lane.FirstOrdinalAtPosition(positionIndex, after) is { } next)
        {
            int nextSlot = lane.SlotForOrdinal(next);
            if (!large.IsCompleted(nextSlot) && !large.Dead.Contains(nextSlot)) return next;
            after = next;
        }
        return null;
    }

    private void MaterializeUnplacedLargeLanes(LargeStandbyWave large)
    {
        if (large.Abandoned || !large.Lanes.Any(lane => lane.Positions.Length == 0) ||
            large.UnplacedWorldRevision == World.Revision &&
            large.UnplacedTopologyRevision == _topologyRevision &&
            large.UnplacedLiquidRevision == Liquids.Revision) return;
        if (_spawnWorldRevision != World.Revision || _spawnTopologyRevision != _topologyRevision ||
            _spawnLiquidRevision != Liquids.Revision) RefreshSpawnCells();
        large.UnplacedWorldRevision = World.Revision;
        large.UnplacedTopologyRevision = _topologyRevision;
        large.UnplacedLiquidRevision = Liquids.Revision;
        bool added = false;
        foreach (var lane in large.Lanes.Where(lane => lane.Positions.Length == 0))
        {
            var shape = lane.Shape;
            var positions = FindSizedSpawnCells(shape).Where(cell => IsEmpty(cell) &&
                HasZombieSupport(cell, shape) && IsBodyClear(shape.StandAt(cell), shape)).ToArray();
            if (positions.Length == 0) continue;
            lane.SetPositions(positions);
            for (int positionIndex = 0; positionIndex < positions.Length; positionIndex++)
                if (NextAvailableLargeOrdinal(large, lane, positionIndex, -1) is { } ordinal)
                {
                    IndexLargeStandby(large, lane, ordinal, positionIndex, shape);
                    added = true;
                }
        }
        if (added) InvalidateProjectileColliders();
    }

    private bool ScheduleNextLargeStandby(LargeStandbyWave large, WaveState wave)
    {
        AdvanceScheduledCursor(wave.Index);
        int index = _scheduledNormalSlots[wave.Index];
        if (index >= large.Plan.SpawnCount) return false;
        _scheduledNormalSlots[wave.Index] = index + 1;
        if (large.Dead.Contains(index))
        {
            if (!large.Completed.Contains(index))
            {
                large.Completed.Add(index);
                wave.Spawned++;
                _internalWaveSpawned[wave.Index]++;
            }
        }
        else if (large.Fronts.TryGetValue(index, out var front))
        {
            front.Due = true;
            TryActivateStandby(front, wave);
        }
        large.DeadDuePrefix = Math.Max(large.DeadDuePrefix, index + 1);
        return true;
    }

    private bool ScheduleLargeDueBatch(LargeStandbyWave large, WaveState wave, float interval)
    {
        AdvanceScheduledCursor(wave.Index);
        int start = _scheduledNormalSlots[wave.Index];
        if (start >= large.Plan.SpawnCount) return false;
        int dueCount = (int)Math.Min(large.Plan.SpawnCount - (long)start,
            Math.Floor(-(double)wave.SpawnTimer / interval) + 1);
        if (dueCount < 64) return false;
        int end = start + dueCount;
        int deadDue = large.Dead.CountUncompletedBelow(end, large.Completed) -
            large.Dead.CountUncompletedBelow(start, large.Completed);
        _scheduledNormalSlots[wave.Index] = end;
        large.DeadDuePrefix = end;
        foreach (var front in large.Fronts.Values)
            if (front.Slot < end) front.Due = true;
        wave.Spawned += deadDue;
        _internalWaveSpawned[wave.Index] += deadDue;
        wave.SpawnTimer = (float)((double)wave.SpawnTimer + (double)dueCount * interval);
        RetryLargeStandby(large, wave);
        return true;
    }

    private void RetryLargeStandby(LargeStandbyWave large, WaveState wave)
    {
        MaterializeUnplacedLargeLanes(large);
        var due = large.DueWork;
        due.Clear();
        foreach (var stack in large.ActiveStacks)
            if (stack.Front is { } front && front.WaveIndex == wave.Index &&
                front.Slot < _scheduledNormalSlots[wave.Index])
                due.Enqueue(front, front.Slot);
        while (due.TryDequeue(out var front, out _))
        {
            var position = large.Lanes[front.GroupIndex].Shape.StandAt(front.Cell!.Value);
            var stack = _standbyStacks[position];
            if (!ReferenceEquals(stack.Front, front)) continue;
            front.Due = true;
            if (TryActivateStandby(front, wave) && stack.Front is { } next &&
                next.WaveIndex == wave.Index && next.Slot < _scheduledNormalSlots[wave.Index])
            {
                due.Enqueue(next, next.Slot);
            }
        }
    }

    private bool SpawnLargeManually(LargeStandbyWave large, WaveState wave)
    {
        MaterializeUnplacedLargeLanes(large);
        AdvanceScheduledCursor(wave.Index);
        int index = _scheduledNormalSlots[wave.Index];
        if (index >= large.Plan.SpawnCount) return false;
        if (large.Dead.Contains(index))
        {
            _scheduledNormalSlots[wave.Index]++;
            large.Completed.Add(index);
            wave.Spawned++;
            _internalWaveSpawned[wave.Index]++;
            return true;
        }
        if (!large.Fronts.TryGetValue(index, out var front) || !TryActivateStandby(front, wave)) return false;
        _scheduledNormalSlots[wave.Index]++;
        return true;
    }

    private void ReconcileLargeSkippedSpawns(LargeStandbyWave large, WaveState wave, int skipped)
    {
        if (wave.Spawned >= wave.TotalCount)
        {
            large.Abandoned = true;
            _livingStandbyCount -= large.LivingCount;
            large.LivingCount = 0;
            _scheduledNormalSlots[wave.Index] = large.Plan.SpawnCount;
            foreach (var slot in large.Fronts.Values.ToArray())
            {
                slot.Consumed = true;
                AdvanceStandby(slot);
            }
            return;
        }
        skipped = Math.Min(skipped, large.Plan.SpawnCount - large.CountCompletedBelow(large.Plan.SpawnCount));
        if (skipped == 0) return;
        int low = large.Completed.Prefix, high = large.Plan.SpawnCount;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if ((long)middle - large.CountCompletedBelow(middle) >= skipped) high = middle;
            else low = middle + 1;
        }
        int skippedAlive = skipped - (large.Dead.CountUncompletedBelow(low, large.Completed) -
            large.Dead.CountUncompletedBelow(Math.Min(low, large.DeadDuePrefix), large.Completed));
        _livingStandbyCount -= skippedAlive;
        large.LivingCount -= skippedAlive;
        large.Completed.AddPrefix(low);
        foreach (var front in large.Fronts.Values.Where(front => front.Slot < low).ToArray())
        {
            front.Consumed = true;
            AdvanceStandby(front);
        }
    }
}
