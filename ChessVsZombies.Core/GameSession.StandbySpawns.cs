using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    // A slot keeps only combat state until activation. The FIFO at each exact
    // position exposes one living slot; the cell index contains stacks, never
    // every member of a large wave.
    private sealed class StandbySpawn(ZombieGroup group, Cell? cell, int waveIndex, int slot)
    {
        public ZombieGroup Group { get; } = group;
        public Cell? Cell { get; set; } = cell;
        public int WaveIndex { get; } = waveIndex;
        public int Slot { get; } = slot;
        public float BodyHp { get; set; } = Balance.ZombieHp * (group.Size switch
        {
            ZombieSize.Micro => .2f, ZombieSize.Milli => .5f, ZombieSize.Kilo => 4f,
            ZombieSize.Mega => 9f, _ => 1f
        });
        public List<ArmorPiece>? Armor { get; set; }
        // A new Giant carries one ordinary block. It becomes held cover when
        // struck in standby, preserving partial damage when the slot activates.
        public float? CoverHp { get; set; }
        public bool CoverWasHit { get; set; }
        public bool Consumed { get; set; }
        public float Hp => BodyHp <= 0 ? 0 : BodyHp + (Armor == null
            ? (Group.Kind is ZombieKind.Armored or ZombieKind.ArmedArmored ? 4 * Balance.GearHp(Group.Tier) *
                (Group.Size == ZombieSize.Micro ? .25f : Group.Size == ZombieSize.Milli ? .5f :
                    Group.Size == ZombieSize.Kilo ? 4f : Group.Size == ZombieSize.Mega ? 9f : 1f) : 0)
            : Armor.Sum(piece => piece.Hp));
    }

    private sealed class StandbyStack(Vector3 position, ZombieSize size)
    {
        public Vector3 Position { get; } = position;
        public ZombieSize Size { get; } = size;
        public Queue<StandbySpawn> Slots { get; } = new();
        public StandbySpawn? Front => Slots.Count == 0 ? null : Slots.Peek();
    }

    private readonly List<StandbySpawn[]> _standbyWaves = [];
    private readonly Dictionary<Vector3, StandbyStack> _standbyStacks = [];
    private readonly Dictionary<Cell, List<StandbyStack>> _standbyByCell = [];
    private Random _spawnRandom;
    private int _livingStandbyCount;
    private int[] _processedNormalSlots = [];

    public int LivingStandbyCount => _livingStandbyCount;

    private void ResetStandbySpawns()
    {
        ClearStandbySpawns();
        _spawnRandom = new Random(_seed);
    }

    private void ClearStandbySpawns()
    {
        _standbyWaves.Clear();
        _standbyStacks.Clear();
        _standbyByCell.Clear();
        _livingStandbyCount = 0;
        _processedNormalSlots = [];
        InvalidateProjectileColliders();
    }

    private void PrepareStandbySpawns(IReadOnlyList<WaveSpawnPlan> plans)
    {
        ClearStandbySpawns();
        _processedNormalSlots = new int[plans.Count];
        var candidates = new Dictionary<ZombieSize, Cell[]>();
        var shapes = new Dictionary<ZombieSize, Zombie>();
        for (int waveIndex = 0; waveIndex < plans.Count; waveIndex++)
        {
            var slots = new StandbySpawn[plans[waveIndex].SpawnCount];
            for (int index = 0; index < slots.Length; index++)
            {
                var group = WaveSpawnPlanner.SelectGroup(plans[waveIndex], index);
                if (!shapes.TryGetValue(group.Size, out var shape))
                    shapes.Add(group.Size, shape = new Zombie { Size = group.Size });
                if (!candidates.TryGetValue(group.Size, out var locations))
                    candidates.Add(group.Size, locations = FindSizedSpawnCells(shape)
                        .Where(cell => IsEmpty(cell) && HasZombieSupport(cell, shape) &&
                            IsBodyClear(shape.StandAt(cell), shape)).ToArray());
                // A fully blocked boundary can become available later. Its slot
                // retains its scheduled time and is placed once a cell exists.
                Cell? cell = locations.Length == 0 ? null : locations[_spawnRandom.Next(locations.Length)];
                var slot = slots[index] = new StandbySpawn(group, cell, waveIndex, index);
                if (shape.IsGiant) slot.CoverHp = Balance.BlockHp(group.Kind == ZombieKind.Basic
                    ? Material.Dirt : group.Tier switch
                    {
                        GearTier.Wood => Material.Wood, GearTier.Stone => Material.Stone,
                        GearTier.Copper => Material.Copper, GearTier.Iron => Material.Iron,
                        GearTier.Gold => Material.Gold, GearTier.Diamond => Material.Diamond,
                        GearTier.Osmium => Material.Osmium, _ => throw new ArgumentOutOfRangeException()
                    });
                if (cell.HasValue) IndexStandby(slot, shape.StandAt(cell.Value), shape);
                _livingStandbyCount++;
            }
            _standbyWaves.Add(slots);
        }
        InvalidateProjectileColliders();
    }

    // WaveState is publicly mutable and older scenario drivers advance Spawned
    // to skip setup slots. Treat such explicit advances as consumed slots so
    // collision and completion remain consistent with the advertised cursor.
    private void ReconcileExternallySkippedSpawns()
    {
        for (int waveIndex = 0; waveIndex < _standbyWaves.Count; waveIndex++)
        {
            int cursor = Math.Min(_standbyWaves[waveIndex].Length,
                Waves[waveIndex].Spawned - Waves[waveIndex].ReinforcementsSpawned);
            while (_processedNormalSlots[waveIndex] < cursor)
            {
                var slot = _standbyWaves[waveIndex][_processedNormalSlots[waveIndex]++];
                if (slot.Consumed) continue;
                slot.Consumed = true;
                if (slot.BodyHp > 0) _livingStandbyCount--;
                AdvanceStandby(slot);
            }
        }
    }

    private void IndexStandby(StandbySpawn slot, Vector3 position, Zombie shape)
    {
        if (!_standbyStacks.TryGetValue(position, out var stack))
        {
            _standbyStacks.Add(position, stack = new StandbyStack(position, shape.Size));
            var half = shape.IsGiant ? shape.BodyHalfExtents : new Vector3(.5f * shape.BodyScale);
            var min = position - half;
            var max = position + half;
            for (int x = Math.Max(0, (int)MathF.Floor(min.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.X)); x++)
                for (int y = Math.Max(0, (int)MathF.Floor(min.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Y)); y++)
                    for (int z = Math.Max(0, (int)MathF.Floor(min.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Z)); z++)
                    {
                        var key = new Cell(x, y, z);
                        if (!_standbyByCell.TryGetValue(key, out var occupants))
                            _standbyByCell.Add(key, occupants = []);
                        occupants.Add(stack);
                    }
        }
        stack.Slots.Enqueue(slot);
    }

    private void AdvanceStandby(StandbySpawn slot)
    {
        if (!slot.Cell.HasValue) return;
        var shape = new Zombie { Size = slot.Group.Size };
        var position = shape.StandAt(slot.Cell.Value);
        var stack = _standbyStacks[position];
        if (ReferenceEquals(stack.Front, slot))
        {
            do { stack.Slots.Dequeue(); }
            while (stack.Front is { } next && (next.Consumed || next.BodyHp <= 0));
            InvalidateProjectileColliders();
        }
    }

    private bool TryFindStandbyImpact(Vector3 from, Vector3 to, ref float fraction, out StandbySpawn? slot)
    {
        slot = null;
        if (_livingStandbyCount == 0) return false;
        var min = Vector3.Min(from, to);
        var max = Vector3.Max(from, to);
        var delta = to - from;
        for (int x = Math.Max(0, (int)MathF.Floor(min.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.X)); x++)
            for (int y = Math.Max(0, (int)MathF.Floor(min.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Y)); y++)
                for (int z = Math.Max(0, (int)MathF.Floor(min.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Z)); z++)
                {
                    if (!_standbyByCell.TryGetValue(new Cell(x, y, z), out var stacks)) continue;
                    foreach (var stack in stacks)
                    {
                        if (ProjectileDiagnostics is { } diagnostics) diagnostics.StandbyStackQueries++;
                        var front = stack.Front;
                        if (front == null || front.Consumed || front.BodyHp <= 0) continue;
                        bool hit = stack.Size is ZombieSize.Kilo or ZombieSize.Mega
                            ? ProjectileBoxIntersection(from, delta, stack.Position - new Vector3(stack.Size == ZombieSize.Kilo ? 1 : 1.5f),
                                stack.Position + new Vector3(stack.Size == ZombieSize.Kilo ? 1 : 1.5f), out var contact)
                            : ProjectileBodyIntersection(from, delta, stack.Position, out contact, stack.Size switch
                            {
                                ZombieSize.Micro => .25f, ZombieSize.Milli => .5f, _ => 1f
                            });
                        if (!hit || contact >= fraction) continue;
                        fraction = contact;
                        slot = front;
                    }
                }
        return slot != null;
    }

    private void DamageStandby(StandbySpawn slot, float damage, DamageKind kind, ShotKind shot, Vector3 incoming)
    {
        if (slot.Consumed || slot.BodyHp <= 0 || !float.IsFinite(damage) || damage <= 0) return;
        if (slot.CoverHp is > 0)
        {
            slot.CoverWasHit = true;
            slot.CoverHp = Math.Max(0, slot.CoverHp.Value - damage);
            if (slot.CoverHp == 0) InvalidateProjectileColliders();
            return; // The breaking bullet ends on this block.
        }
        var group = slot.Group;
        var zombie = new Zombie
        {
            Kind = group.Kind, Tier = group.Tier, Size = group.Size, IsBoss = group.IsBoss,
            Position = new Zombie { Size = group.Size }.StandAt(slot.Cell!.Value)
        };
        zombie.FaceToward(new Vector3(16, zombie.Position.Y, 16) - zombie.Position);
        zombie.RestoreBodyAndArmor(slot.BodyHp, slot.Armor ?? zombie.ArmorPieces);
        float before = zombie.Hp;
        // Standby Giant armor is exposed in stable slot order, one piece per
        // projectile. The shot that breaks the last piece cannot hit its body.
        if (zombie.IsGiant && zombie.ArmorPieces.Count > 0)
        {
            var armor = zombie.ArmorPieces[0];
            armor.Hp = Math.Max(0, armor.Hp - damage);
            if (armor.Hp <= 0)
            {
                ((List<ArmorPiece>)zombie.ArmorPieces).RemoveAt(0);
                InvalidateProjectileColliders();
            }
        }
        else zombie.ApplyDamage(damage, kind, shot, incoming);
        slot.BodyHp = zombie.BodyHp;
        slot.Armor = zombie.ArmorPieces.ToList();
        var wave = Waves[slot.WaveIndex];
        wave.DamageTaken = Math.Min(wave.TotalHp, wave.DamageTaken + before - zombie.Hp);
        if (slot.BodyHp <= 0)
        {
            _livingStandbyCount--;
            AdvanceStandby(slot);
        }
    }
}
