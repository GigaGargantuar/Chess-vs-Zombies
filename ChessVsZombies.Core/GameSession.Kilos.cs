using System.Numerics;

namespace ChessVsZombies.Core;

/// <summary>A real owned cover block, retaining its material and durability while held or stowed.</summary>
public sealed record KiloCover(int CarrierId, Material Material, float Hp, bool WasBarrier, Vector3 Min, Vector3 Max, int BlockIndex, bool IsActive = true);

public sealed partial class GameSession
{
    private sealed class HeldKiloBlock(int blockIndex, Material material, float hp, bool wasBarrier)
    {
        public int BlockIndex { get; } = blockIndex;
        public Material Material { get; } = material;
        public float Hp { get; set; } = hp;
        public bool WasBarrier { get; } = wasBarrier;
        public Vector3 Offset { get; set; }
    }

    private sealed class HurledAlly(Vector3 origin, Vector3 landing, float height)
    {
        public Vector3 Origin { get; } = origin;
        public Vector3 Landing { get; } = landing;
        public float Height { get; } = height;
        public float Elapsed { get; set; }
        public const float Duration = 1.2f;
        public Vector3 At(float progress) => Vector3.Lerp(Origin, Landing, progress) +
            Vector3.UnitY * (4 * Height * progress * (1 - progress));
    }

    private readonly Dictionary<int, List<HeldKiloBlock>> _kiloBlocks = [];
    private readonly Dictionary<int, HurledAlly> _hurledAllies = [];
    private readonly Dictionary<int, float> _kiloActionCooldowns = [];

    public IReadOnlyList<KiloCover> KiloCovers
    {
        get
        {
            var covers = new List<KiloCover>(_kiloBlocks.Count * 2);
            foreach (var zombie in Zombies)
            {
                if (zombie.Hp <= 0 || !_kiloBlocks.TryGetValue(zombie.Id, out var blocks)) continue;
                foreach (var held in blocks)
                {
                    var center = zombie.Position + held.Offset;
                    covers.Add(new KiloCover(zombie.Id, held.Material, held.Hp, held.WasBarrier,
                        center - new Vector3(.5f), center + new Vector3(.5f), held.BlockIndex, !IsKiloUsingBuckets(zombie)));
                }
            }
            return covers;
        }
    }

    public bool IsZombieHurled(Zombie zombie) => zombie.Hp > 0 && _hurledAllies.ContainsKey(zombie.Id);

    public bool IsKiloUsingBuckets(Zombie zombie) => zombie.Hp > 0 && zombie.IsGiant &&
        zombie.KiloEquipmentMode == KiloEquipmentMode.Buckets;

    private static void BeginKiloBucketAction(Zombie zombie)
    {
        if (!zombie.IsGiant) return;
        zombie.KiloEquipmentMode = KiloEquipmentMode.Buckets;
        zombie.KiloBucketActionRemaining = Balance.BucketActionSeconds;
    }

    private static void UpdateKiloEquipment(Zombie zombie, float dt)
    {
        if (!zombie.IsGiant || zombie.KiloEquipmentMode != KiloEquipmentMode.Buckets) return;
        zombie.KiloBucketActionRemaining = Math.Max(0, zombie.KiloBucketActionRemaining - dt);
        if (zombie.KiloBucketActionRemaining <= .00001f)
        {
            zombie.KiloBucketActionRemaining = 0;
            zombie.KiloEquipmentMode = KiloEquipmentMode.Covers;
        }
    }

    private void ResetKilos()
    {
        _kiloBlocks.Clear();
        _hurledAllies.Clear();
        _kiloActionCooldowns.Clear();
    }

    private void UpdateKilos(float dt)
    {
        foreach (var id in _kiloActionCooldowns.Keys.ToArray())
        {
            _kiloActionCooldowns[id] -= dt;
            if (_kiloActionCooldowns[id] <= 0) _kiloActionCooldowns.Remove(id);
        }
        foreach (var zombie in Zombies)
        {
            if (zombie.Hp <= 0) { HandleKiloDeath(zombie); continue; }
            // Finish simultaneous idle pickups/hurls before anyone requests a
            // movement field. Every real terrain edit is then visible to one
            // shared rebuild, rather than rebuilding between each carrier.
            // Interrupted steps and partial support still recover in UpdateZombie.
            if (zombie.Size != ZombieSize.Kilo || zombie.NextCell.HasValue ||
                _liquidDestinations.ContainsKey(zombie.Id) || IsZombieHurled(zombie) || IsZombieRetreating(zombie) ||
                Vector3.DistanceSquared(zombie.Position, zombie.StandAt(zombie.Cell)) > .000001f ||
                !KiloHasFooting(zombie, zombie.Position) || TryBuoyantSurface(zombie, zombie.Position, out _) ||
                TryFindBodyObstacle(zombie.Position, zombie.Position, out _, zombie: zombie)) continue;
            TryKiloAction(zombie, dt);
        }
    }

    private void TryKiloAction(Zombie zombie, float dt)
    {
        if (zombie.Size != ZombieSize.Kilo || IsKiloUsingBuckets(zombie) || IsZombieRetreating(zombie) || _kiloActionCooldowns.ContainsKey(zombie.Id) ||
            zombie.NextCell.HasValue || _hurledAllies.ContainsKey(zombie.Id) || !KiloHasFooting(zombie, zombie.Position)) return;
        if (TryHurlAlly(zombie))
        {
            _kiloActionCooldowns[zombie.Id] = 3;
            return;
        }
        int heldCount = GetKiloConstructionBlocks(zombie);
        if (zombie.CarriedBlocks > 0 && (heldCount > 0 || zombie.CarriedBlocks >= 2))
        {
            // Supply can refill both backpack slots after both hands were lost.
            // Raise those real blocks before considering any terrain pickup.
            while (TryRaiseKiloBackpackCover(zombie)) { }
        }
        else TryGrabKiloCover(zombie);
    }

    private bool CanKiloHurlForProgress(Zombie kilo) => kilo.Size == ZombieSize.Kilo && kilo.Hp > 0 && !IsKiloUsingBuckets(kilo) && !IsZombieRetreating(kilo) &&
        !_hurledAllies.ContainsKey(kilo.Id) && KiloHasFooting(kilo, kilo.Position) && TryFindKiloHurl(kilo, out _, out _);

    private bool TryHurlAlly(Zombie kilo)
    {
        if (!TryFindKiloHurl(kilo, out var ally, out var flight)) return false;
        ClearZombieMovement(ally);
        Falls.CancelZombie(ally);
        _fallStartHeights.TryAdd(ally.Id, ally.Position.Y);
        _hurledAllies.Add(ally.Id, flight);
        RecordZombieProgress(kilo);
        RecordZombieProgress(ally);
        kilo.FaceToward(flight.Landing - kilo.Position);
        ally.FaceToward(flight.Landing - ally.Position);
        return true;
    }

    private bool TryFindKiloHurl(Zombie kilo, out Zombie hurled, out HurledAlly selectedFlight)
    {
        hurled = null!;
        selectedFlight = null!;
        var direction = new Vector3(16 - kilo.Position.X, 0, 16 - kilo.Position.Z);
        if (direction.LengthSquared() < .0001f) return false;
        direction = Math.Abs(direction.X) >= Math.Abs(direction.Z)
            ? new Vector3(MathF.Sign(direction.X), 0, 0) : new Vector3(0, 0, MathF.Sign(direction.Z));
        foreach (var ally in Zombies.Where(ally => ally.Id != kilo.Id && ally.Hp > 0 && !ally.IsGiant &&
                     !IsZombieRetreating(ally) && !ally.NextCell.HasValue && !_hurledAllies.ContainsKey(ally.Id) &&
                     Vector3.DistanceSquared(ally.Position, kilo.Position) <= 9).OrderBy(ally => Vector3.DistanceSquared(ally.Position, kilo.Position)))
        {
            if (!KiloHasFooting(ally, ally.Position) || _liquidDestinations.ContainsKey(ally.Id)) continue;
            // The Kilo must actually reach its ally; a neighboring wall blocks a grab.
            var reachOrigin = new Vector3(kilo.Position.X, ally.Position.Y, kilo.Position.Z);
            if (KiloBoxSweep(reachOrigin, ally.Position, new Vector3(.05f), out _)) continue;
            var side = new Vector3(-direction.Z, 0, direction.X);
            foreach (var distance in new[] { 4, 5, 3, 6 })
                foreach (var lateral in new[] { 0, -1, 1 })
                {
                    var horizontal = ally.Position + direction * distance + side * lateral;
                    if (!TryKiloFloor(ally, new Vector3(horizontal.X, Math.Min(WorldGrid.Size - ally.BodyHalfExtents.Y, ally.Position.Y + 1), horizontal.Z), out var height)) continue;
                    var landing = new Vector3(horizontal.X, height, horizontal.Z);
                    if (Vector3.DistanceSquared(landing, new Vector3(16, landing.Y, 16)) >=
                        Vector3.DistanceSquared(ally.Position, new Vector3(16, ally.Position.Y, 16)) || !KiloBodyClear(ally, landing)) continue;
                    var flight = new HurledAlly(ally.Position, landing, 2.5f);
                    if (!KiloFlightClear(ally, flight, 0, 1)) continue;
                    hurled = ally;
                    selectedFlight = flight;
                    return true;
                }
        }
        return false;
    }

    private bool TryUpdateKiloFlight(Zombie zombie, float dt)
    {
        if (!_hurledAllies.TryGetValue(zombie.Id, out var flight)) return false;
        var before = Math.Clamp(flight.Elapsed / HurledAlly.Duration, 0, 1);
        flight.Elapsed += dt;
        var after = Math.Clamp(flight.Elapsed / HurledAlly.Duration, 0, 1);
        var count = Math.Max(1, (int)MathF.Ceiling((after - before) *
            (Vector3.Distance(flight.Origin, flight.Landing) + 4 * flight.Height) / .04f));
        for (var sample = 1; sample <= count; sample++)
        {
            var position = flight.At(before + (after - before) * sample / count);
            if (!KiloBodyClear(zombie, position) || KiloBoxSweep(zombie.Position, position, zombie.BodyHalfExtents, out _))
            {
                // Leave the body at its last clear position. Ordinary gravity
                // resumes next slice, retaining the actual height already reached.
                _hurledAllies.Remove(zombie.Id);
                return true;
            }
            zombie.FaceToward(position - zombie.Position);
            zombie.Position = position;
            _fallStartHeights[zombie.Id] = Math.Max(_fallStartHeights.GetValueOrDefault(zombie.Id, position.Y), position.Y);
        }
        if (after >= 1)
        {
            _hurledAllies.Remove(zombie.Id);
            if (KiloHasFooting(zombie, zombie.Position)) CompleteZombieFall(zombie, zombie.Position.Y);
        }
        return true;
    }

    private bool KiloFlightClear(Zombie zombie, HurledAlly flight, float start, float end)
    {
        var count = Math.Max(1, (int)MathF.Ceiling((Vector3.Distance(flight.Origin, flight.Landing) + 4 * flight.Height) / .04f));
        var previous = flight.At(start);
        for (var sample = 1; sample <= count; sample++)
        {
            var position = flight.At(start + (end - start) * sample / count);
            if (!KiloBodyClear(zombie, position) || KiloBoxSweep(previous, position, zombie.BodyHalfExtents, out _)) return false;
            previous = position;
        }
        return true;
    }

    private bool TryGrabKiloCover(Zombie zombie)
    {
        _kiloBlocks.TryGetValue(zombie.Id, out var held);
        if ((held?.Count ?? 0) + zombie.CarriedBlocks >= 2) return false;
        var blockIndex = held?.Any(block => block.BlockIndex == 0) == true ? 1 : 0;
        var center = zombie.Position + KiloCoverOffset(zombie, blockIndex);
        // A fallen teammate leaves ordinary world terrain. Recover reachable
        // blocks beside/above the body before excavating another floor block.
        if (TryGrabNearbyKiloCover(zombie, center, blockIndex)) return true;
        var front = zombie.Position + KiloFacingOnSquare(zombie) *
            (Math.Max(zombie.BodyHalfExtents.X, zombie.BodyHalfExtents.Z) + .5f);
        var footY = zombie.Position.Y - zombie.StandingHeight;
        var source = new Cell((int)MathF.Floor(front.X), (int)MathF.Floor(footY - .001f), (int)MathF.Floor(front.Z));
        return TryTakeKiloCover(zombie, source, center, blockIndex);
    }

    private bool TryRaiseKiloBackpackCover(Zombie zombie)
    {
        if (zombie.CarriedBlocks <= 0) return false;
        _kiloBlocks.TryGetValue(zombie.Id, out var blocks);
        if (blocks is { Count: >= 2 }) return false;
        int index = blocks?.Any(block => block.BlockIndex == 0) == true ? 1 : 0;
        var offset = KiloCoverOffset(zombie, index);
        var center = zombie.Position + offset;
        if (KiloBoxSweep(center, center, new Vector3(.5f), out _) ||
            Zombies.Any(ally => ally.Id != zombie.Id && ally.Hp > 0 && KiloBoxesOverlap(
                ally.Position - ally.BodyHalfExtents, ally.Position + ally.BodyHalfExtents,
                center - new Vector3(.5f), center + new Vector3(.5f)))) return false;
        var material = zombie.CarriedBlockMaterial;
        if (blocks == null) _kiloBlocks.Add(zombie.Id, blocks = []);
        blocks.Add(CreateHeldKiloBlock(zombie, index, material, Balance.BlockHp(material), false));
        zombie.CarriedBlocks--;
        return true;
    }

    private static Vector3 KiloFacingOnSquare(Zombie zombie)
    {
        var facing = new Vector3(zombie.Facing.X, 0, zombie.Facing.Z);
        var projection = Math.Max(Math.Abs(facing.X), Math.Abs(facing.Z));
        return projection > .0001f ? facing / projection : Vector3.UnitZ;
    }

    private HeldKiloBlock CreateHeldKiloBlock(Zombie zombie, int index, Material material, float hp, bool wasBarrier) =>
        new(index, material, hp, wasBarrier) { Offset = KiloCoverOffset(zombie, index) };

    private Vector3 KiloCoverOffset(Zombie zombie, int blockIndex)
    {
        if (_kiloBlocks.TryGetValue(zombie.Id, out var blocks) && blocks.Find(block => block.BlockIndex == blockIndex) is { } held)
            return held.Offset;
        // A full cube in each hand, side by side. Choose a face of the reserved
        // square, so diagonal headings cannot make the two cubes overlap.
        var facing = KiloFacingOnSquare(zombie);
        float side = blockIndex == 0 ? -.5f : .5f;
        var offset = Math.Abs(facing.X) > Math.Abs(facing.Z)
            ? new Vector3(MathF.Sign(facing.X) * .5f, 0, side)
            : new Vector3(side, 0, MathF.Sign(facing.Z) * .5f);
        // A replacement goes into an empty part of the body's reserved volume,
        // even if the surviving hand has already moved to guard a different side.
        if (blocks is { Count: > 0 } && KiloHandOffsetsOverlap(offset, blocks[0].Offset))
            offset = OppositeKiloHandOffset(blocks[0].Offset);
        return offset;
    }

    private static bool KiloHandOffsetsOverlap(Vector3 first, Vector3 second) =>
        Math.Abs(first.X - second.X) < 1 && Math.Abs(first.Y - second.Y) < 1 && Math.Abs(first.Z - second.Z) < 1;

    private static Vector3 OppositeKiloHandOffset(Vector3 other)
    {
        if (Math.Abs(other.X) == .5f) return new Vector3(-other.X, other.Y, other.Z);
        if (Math.Abs(other.Z) == .5f) return new Vector3(other.X, other.Y, -other.Z);
        return new Vector3(other.X, -other.Y, other.Z);
    }

    private bool TryFindKiloCoverImpact(Vector3 from, Vector3 to, out float fraction, out int carrierId, out int blockIndex)
    {
        fraction = float.PositiveInfinity;
        carrierId = 0;
        blockIndex = -1;
        // Projectile sampling is a hot path: use the actual held blocks directly,
        // without allocating the renderer's public cover snapshots for every ray.
        foreach (var zombie in Zombies)
        {
            if (zombie.Hp <= 0 || IsKiloUsingBuckets(zombie) || !_kiloBlocks.TryGetValue(zombie.Id, out var blocks)) continue;
            foreach (var held in blocks)
            {
                var center = zombie.Position + held.Offset;
                if (ProjectileBoxIntersection(from, to - from, center - new Vector3(.5f), center + new Vector3(.5f), out var hit) &&
                    (hit < fraction || hit == fraction && (zombie.Id < carrierId || zombie.Id == carrierId && held.BlockIndex < blockIndex)))
                { fraction = hit; carrierId = zombie.Id; blockIndex = held.BlockIndex; }
            }
        }
        return float.IsFinite(fraction);
    }

    private void DamageKiloCover(int carrierId, int blockIndex, float damage)
    {
        if (!float.IsFinite(damage) || damage <= 0 || !_kiloBlocks.TryGetValue(carrierId, out var blocks)) return;
        var held = blocks.FirstOrDefault(block => block.BlockIndex == blockIndex);
        if (held == null) return;
        held.Hp -= damage;
        // Projectile-destroyed cover is material loss, just like ordinary shot terrain.
        if (held.Hp > 0) return;
        blocks.Remove(held);
        if (blocks.Count == 0) _kiloBlocks.Remove(carrierId);
    }

    private int GetKiloConstructionBlocks(Zombie zombie) =>
        _kiloBlocks.TryGetValue(zombie.Id, out var blocks) ? blocks.Count : 0;

    private bool HasKiloBackpackCover(Zombie zombie) =>
        _kiloBlocks.TryGetValue(zombie.Id, out var blocks) && blocks.Any(block => block.BlockIndex == 1);

    private bool TryPeekKiloConstructionBlock(Zombie zombie, out BlockDrop block)
    {
        if (_kiloBlocks.TryGetValue(zombie.Id, out var blocks) && blocks.Count > 0)
        {
            var held = blocks[0];
            block = new BlockDrop(zombie.Cell, held.Material, held.Hp, held.WasBarrier);
            return true;
        }
        block = null!;
        return false;
    }

    private void ConsumeKiloConstructionBlock(Zombie zombie)
    {
        if (!_kiloBlocks.TryGetValue(zombie.Id, out var blocks) || blocks.Count == 0) return;
        blocks.RemoveAt(0);
        if (blocks.Count == 0) _kiloBlocks.Remove(zombie.Id);
    }

    private void HandleKiloDeath(Zombie zombie)
    {
        _hurledAllies.Remove(zombie.Id);
        _kiloActionCooldowns.Remove(zombie.Id);
        _megaActionCooldowns.Remove(zombie.Id);
        if (!_kiloBlocks.Remove(zombie.Id, out var blocks)) return;
        foreach (var held in blocks)
        {
            var center = zombie.Position + held.Offset;
            var cell = new Cell(Math.Clamp((int)MathF.Floor(center.X), 0, WorldGrid.Size - 1),
                Math.Clamp((int)MathF.Floor(center.Y), 0, WorldGrid.Size - 1), Math.Clamp((int)MathF.Floor(center.Z), 0, WorldGrid.Size - 1));
            _pendingBlockDrops.Add(new BlockDrop(cell, held.Material, held.Hp, held.WasBarrier));
        }
    }

    private bool KiloHasFooting(Zombie zombie, Vector3 position) => TryKiloFloor(zombie, position, out var height) && Math.Abs(position.Y - height) <= .011f;

    private bool TryKiloFloor(Zombie zombie, Vector3 position, out float height)
    {
        height = float.NegativeInfinity;
        var half = zombie.BodyHalfExtents;
        for (int x = Math.Max(0, (int)MathF.Floor(position.X - half.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(position.X + half.X) - 1); x++)
            for (int z = Math.Max(0, (int)MathF.Floor(position.Z - half.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(position.Z + half.Z) - 1); z++)
                for (int y = Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(position.Y)); y >= 0; y--)
                {
                    var cell = new Cell(x, y, z);
                    if (World.GetMaterial(cell) == Material.Air && !HasLiveIndexedTurret(cell)) continue;
                    foreach (var bounds in SolidCollisionBounds(cell))
                        if (position.X + half.X > bounds.Min.X && position.X - half.X < bounds.Max.X &&
                            position.Z + half.Z > bounds.Min.Z && position.Z - half.Z < bounds.Max.Z &&
                            bounds.Max.Y + half.Y <= position.Y + .00001f)
                            height = Math.Max(height, bounds.Max.Y + zombie.StandingHeight);
                    if (float.IsFinite(height)) break;
                }
        return float.IsFinite(height);
    }

    private bool KiloBodyClear(Zombie zombie, Vector3 position)
    {
        var min = position - zombie.BodyHalfExtents;
        var max = position + zombie.BodyHalfExtents;
        return min.X >= 0 && min.Y >= 0 && min.Z >= 0 && max.X <= WorldGrid.Size && max.Y <= WorldGrid.Size && max.Z <= WorldGrid.Size &&
            !KiloBoxSweep(position, position, zombie.BodyHalfExtents, out _);
    }

    private bool KiloBoxSweep(Vector3 from, Vector3 to, Vector3 half, out Cell obstacle, Cell? ignored = null)
    {
        obstacle = new Cell(-1, -1, -1);
        var low = Vector3.Min(from, to) - half;
        var high = Vector3.Max(from, to) + half;
        if (low.X < 0 || low.Y < 0 || low.Z < 0 || high.X > WorldGrid.Size || high.Y > WorldGrid.Size || high.Z > WorldGrid.Size) return true;
        var first = float.PositiveInfinity;
        for (int x = Math.Max(0, (int)MathF.Floor(low.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(high.X)); x++)
            for (int y = Math.Max(0, (int)MathF.Floor(low.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(high.Y)); y++)
                for (int z = Math.Max(0, (int)MathF.Floor(low.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(high.Z)); z++)
                {
                    var cell = new Cell(x, y, z);
                    if (cell == ignored || World.GetMaterial(cell) == Material.Air && !HasLiveIndexedTurret(cell)) continue;
                    foreach (var bounds in SolidCollisionBounds(cell))
                        if (ProjectileBoxIntersection(from, to - from, bounds.Min - half + new Vector3(.00001f),
                            bounds.Max + half - new Vector3(.00001f), out var hit) && hit < first)
                        { first = hit; obstacle = cell; }
                }
        return float.IsFinite(first);
    }

    private static bool KiloBoxesOverlap(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMax.X > bMin.X && aMin.X < bMax.X && aMax.Y > bMin.Y && aMin.Y < bMax.Y && aMax.Z > bMin.Z && aMin.Z < bMax.Z;
}
