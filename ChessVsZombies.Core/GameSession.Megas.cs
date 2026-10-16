using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    // Physical defaults are kept together for later level/balance configuration.
    public const float MegaThrowAttackMultiplier = 1.5f;
    public const float MegaThrowRange = 14;
    public const float MegaThrowCooldownSeconds = 4;
    public const float MegaWeaponGravity = 8;
    private const float MegaPickupCooldownSeconds = .35f;

    private sealed class MegaWeaponFlight
    {
        public int OwnerId;
        public MegaWeaponFormation Formation;
        public bool IsDebris;
        public Vector3 Position, Velocity;
        public float Age, Damage;
        public List<HeldKiloBlock> Blocks = [];
        public HashSet<int> DamagedTowerIds = [];
    }

    private readonly List<MegaWeaponFlight> _megaFlights = [];
    private readonly Dictionary<int, float> _megaActionCooldowns = [];

    public IReadOnlyList<MegaWeaponBlock> MegaWeapons => Zombies
        .Where(zombie => zombie.Hp > 0 && zombie.Size == ZombieSize.Mega && _kiloBlocks.ContainsKey(zombie.Id))
        .SelectMany(zombie => _kiloBlocks[zombie.Id].Select(block =>
        {
            var center = zombie.Position + block.Offset;
            return new MegaWeaponBlock(zombie.Id, block.BlockIndex, block.Material, block.Hp, block.WasBarrier,
                center - new Vector3(.5f), center + new Vector3(.5f), !IsKiloUsingBuckets(zombie));
        })).ToArray();

    public IReadOnlyList<MegaThrownBlock> MegaProjectiles => _megaFlights.SelectMany(flight => flight.Blocks.Select(block =>
    {
        var center = flight.Position + block.Offset;
        return new MegaThrownBlock(flight.OwnerId, block.BlockIndex, block.Material, block.Hp, block.WasBarrier,
            center - new Vector3(.5f), center + new Vector3(.5f), flight.Formation, flight.IsDebris);
    })).ToArray();

    private void ResetMegas()
    {
        _megaFlights.Clear();
        _megaActionCooldowns.Clear();
        _megaGuardMemory.Clear();
        _megaGuardPredictionBatch = null;
    }

    private void UpdateMegas(float dt)
    {
        UpdateMegaGuardTimers(dt);
        UpdateMegaHeldPoses();
        foreach (var id in _megaActionCooldowns.Keys.ToArray())
        {
            _megaActionCooldowns[id] -= dt;
            if (_megaActionCooldowns[id] <= 0) _megaActionCooldowns.Remove(id);
        }
        foreach (var zombie in Zombies)
            if (zombie.Size == ZombieSize.Mega && zombie.Hp > 0 && !zombie.NextCell.HasValue &&
                Vector3.DistanceSquared(zombie.Position, zombie.StandAt(zombie.Cell)) <= .000001f)
                TryMegaAction(zombie, dt);
    }

    private void TryMegaAction(Zombie mega, float dt)
    {
        if (mega.Size == ZombieSize.Mega) UpdateMegaHeldPose(mega);
        if (mega.Size != ZombieSize.Mega || mega.Hp <= 0 || IsKiloUsingBuckets(mega) ||
            IsZombieRetreating(mega) || _megaActionCooldowns.ContainsKey(mega.Id) || mega.NextCell.HasValue ||
            !KiloHasFooting(mega, mega.Position) || TryFindBodyObstacle(mega.Position, mega.Position, out _, zombie: mega)) return;
        if (GetKiloConstructionBlocks(mega) >= 4 && MegaGuardReadyToThrow(mega) && TryThrowMegaWeapon(mega))
        {
            _megaActionCooldowns[mega.Id] = MegaThrowCooldownSeconds;
            return;
        }
        if (GetKiloConstructionBlocks(mega) >= 4) return;
        if (TryGrabMegaWorldBlock(mega))
        {
            BeginMegaGuardWindup(mega);
        }
        // A failed grip also takes time; do not repeat the entire connected
        // extraction search every simulation slice while a site is obstructed.
        _megaActionCooldowns[mega.Id] = MegaPickupCooldownSeconds;
    }

    private static Vector3 MegaHeldOffset(Zombie mega, int index)
    {
        var facing = mega.Facing;
        var forward = Math.Abs(facing.X) >= Math.Abs(facing.Z)
            ? new Vector3(MathF.Sign(facing.X), 0, 0) : new Vector3(0, 0, MathF.Sign(facing.Z));
        var side = new Vector3(forward.Z, 0, -forward.X);
        // Two vertical pairs, one in each hand. The front faces coincide with
        // the reserved three-cube body's front face, so they receive real
        // incoming contacts before the body without extending into terrain.
        return forward + side * (index % 2 == 0 ? -.5f : .5f) +
            Vector3.UnitY * (index / 2 == 0 ? -.5f : .5f);
    }

    private void UpdateMegaHeldPoses()
    {
        // Trace each incoming projectile once for the whole group, then choose
        // one simultaneous pose per carrier before resolving any impacts.
        _megaGuardPredictionBatch = PredictMegaIncomingShots();
        try
        {
            foreach (var mega in Zombies)
                if (mega.Size == ZombieSize.Mega && mega.Hp > 0) UpdateMegaHeldPose(mega);
        }
        finally { _megaGuardPredictionBatch = null; }
    }

    private void UpdateMegaHeldPose(Zombie mega)
    {
        if (!_kiloBlocks.TryGetValue(mega.Id, out var blocks) || blocks.Count == 0) return;
        if (TryUpdateMegaPredictiveCover(mega, blocks)) return;
        foreach (var block in blocks)
        {
            var center = mega.Position + MegaHeldOffset(mega, block.BlockIndex);
            if (KiloBoxSweep(center, center, new Vector3(.49999f), out _) ||
                Zombies.Any(ally => ally.Id != mega.Id && ally.Hp > 0 && KiloBoxesOverlap(
                    center - new Vector3(.5f), center + new Vector3(.5f),
                    ally.Position - ally.BodyHalfExtents, ally.Position + ally.BodyHalfExtents))) return;
        }
        foreach (var block in blocks) block.Offset = MegaHeldOffset(mega, block.BlockIndex);
    }

    private int NextMegaBlockIndex(List<HeldKiloBlock>? held)
    {
        for (int index = 0; index < 4; index++)
            if (held == null || held.All(block => block.BlockIndex != index)) return index;
        return -1;
    }

    private bool TryGrabMegaWorldBlock(Zombie mega) => TryExtractMegaChunk(mega);

    private bool TryThrowMegaWeapon(Zombie mega)
    {
        if (mega.Attack <= 0 || !_kiloBlocks.TryGetValue(mega.Id, out var held) || held.Count != 4 || IsKiloUsingBuckets(mega)) return false;
        var target = Turrets.Where(turret => turret.Hp > 0 &&
                Vector3.DistanceSquared(turret.Cell.Center, mega.Position) <= MegaThrowRange * MegaThrowRange)
            .OrderBy(turret => Vector3.DistanceSquared(turret.Cell.Center, mega.Position)).ThenBy(turret => turret.Id).FirstOrDefault();
        if (target == null) return false;
        var toward = target.Cell.Center - mega.Position;
        var horizontal = new Vector3(toward.X, 0, toward.Z);
        if (horizontal.LengthSquared() < .00001f) return false;
        var forward = Vector3.Normalize(horizontal);
        // The material pieces remain axis-aligned full cubes. A diagonal line
        // of unit-spaced cube centers would overlap; orient the formation to
        // its dominant grid axis while the whole assembly flies at the target.
        var formationForward = Math.Abs(forward.X) >= Math.Abs(forward.Z)
            ? new Vector3(MathF.Sign(forward.X), 0, 0) : new Vector3(0, 0, MathF.Sign(forward.Z));
        var side = new Vector3(-formationForward.Z, 0, formationForward.X);
        var formation = Turrets.Count(turret => turret.Hp > 0 && Vector3.DistanceSquared(turret.Cell.Center, target.Cell.Center) <= 6.25f) > 1
            ? MegaWeaponFormation.Chunk : MegaWeaponFormation.Stick;
        var origin = mega.Position + Vector3.UnitY * .25f;
        Vector3 Offset(int index, MegaWeaponFormation shape) => shape == MegaWeaponFormation.Stick
            ? formationForward * (index - 1.5f)
            : side * (index % 2 == 0 ? -.5f : .5f) + Vector3.UnitY * (index / 2 == 0 ? -.5f : .5f);
        bool ArrangementClear(MegaWeaponFormation shape) => held.All(block =>
        {
            var center = origin + Offset(block.BlockIndex, shape);
            return !KiloBoxSweep(center, center, new Vector3(.49999f), out _) &&
                Zombies.All(ally => ally.Id == mega.Id || ally.Hp <= 0 || !KiloBoxesOverlap(
                    center - new Vector3(.5f), center + new Vector3(.5f),
                    ally.Position - ally.BodyHalfExtents, ally.Position + ally.BodyHalfExtents));
        });
        if (!ArrangementClear(formation)) formation = MegaWeaponFormation.Chunk;
        if (!ArrangementClear(formation)) return false;
        float duration = Math.Clamp(horizontal.Length() / 7, .6f, 2);
        var flight = new MegaWeaponFlight
        {
            OwnerId = mega.Id, Formation = formation, Position = origin,
            Velocity = (target.Cell.Center - origin) / duration + Vector3.UnitY * (MegaWeaponGravity * duration * .5f),
            Damage = mega.Attack * MegaThrowAttackMultiplier,
            Blocks = held
        };
        foreach (var block in held) block.Offset = Offset(block.BlockIndex, formation);
        _kiloBlocks.Remove(mega.Id);
        _megaFlights.Add(flight);
        mega.FaceToward(toward);
        RecordZombieProgress(mega);
        return true;
    }

    private void UpdateMegaProjectiles(float dt)
    {
        var pending = new Queue<(MegaWeaponFlight Flight, float Duration)>(
            _megaFlights.Select(flight => (flight, dt)));
        while (pending.TryDequeue(out var work))
        {
            var (flight, duration) = work;
            if (!_megaFlights.Contains(flight)) continue;
            if (flight.IsDebris)
                flight.DamagedTowerIds.RemoveWhere(id =>
                {
                    var tower = Turrets.FirstOrDefault(candidate => candidate.Id == id && candidate.Hp > 0);
                    return tower == null || flight.Blocks.All(block => !KiloBoxesOverlap(
                        flight.Position + block.Offset - new Vector3(.5f), flight.Position + block.Offset + new Vector3(.5f),
                        tower.Cell.Center - new Vector3(.5f), tower.Cell.Center + new Vector3(.5f)));
                });
            float travelBound = (flight.Velocity.Length() + MegaWeaponGravity * duration) * duration;
            int samples = Math.Max(1, (int)MathF.Ceiling(travelBound / .08f));
            float maximumSlice = duration / samples;
            float remaining = duration;
            while (remaining > 0 && _megaFlights.Contains(flight))
            {
                float slice = Math.Min(maximumSlice, remaining);
                var from = flight.Position;
                var to = from + flight.Velocity * slice - Vector3.UnitY * (MegaWeaponGravity * slice * slice * .5f);
                if (FindMegaWeaponImpact(flight, from, flight.Velocity, slice,
                    out float fraction, out var contactTowers, out var contactTerrain))
                {
                    fraction = Math.Clamp(fraction, 0, 1);
                    float elapsed = slice * fraction;
                    flight.Position = from + flight.Velocity * elapsed -
                        Vector3.UnitY * (MegaWeaponGravity * elapsed * elapsed * .5f);
                    flight.Velocity -= Vector3.UnitY * (MegaWeaponGravity * elapsed);
                    flight.Age += elapsed;
                    remaining -= elapsed;
                    bool stopped = contactTerrain.Count > 0;
                    var towerTargets = !flight.IsDebris && flight.Formation == MegaWeaponFormation.Stick
                        ? contactTowers.OrderBy(contact => contact.Tower.Id).Take(1) : contactTowers;
                    foreach (var contact in towerTargets)
                    {
                        if (contact.Tower.Hp <= 0 || !flight.Blocks.Contains(contact.Block) ||
                            !flight.DamagedTowerIds.Add(contact.Tower.Id)) continue;
                        float towerHp = contact.Tower.Hp;
                        contact.Tower.Hp -= MegaBlockImpactDamage(flight, contact.Block);
                        contact.Block.Hp -= towerHp;
                        if (contact.Block.Hp <= 0) flight.Blocks.Remove(contact.Block);
                        stopped |= contact.Tower.Hp > 0;
                    }
                    foreach (var contact in contactTerrain)
                        if (flight.Blocks.Contains(contact.Block))
                            DamageProjectileTerrain(contact.Cell, MegaBlockImpactDamage(flight, contact.Block));
                    if (contactTowers.Count > 0) SettleDefenses();
                    if (flight.Blocks.Count > 0 && !stopped)
                        continue; // Re-scan the unspent time through the newly opened path.
                    _megaFlights.Remove(flight);
                    // Surviving towers end the impact; they are not repeatedly
                    // attacked by a stationary weapon. Only real surviving parts scatter.
                    // Release the surviving parts at the physical contact. An
                    // earlier chord position can leave them suspended above it.
                    if (flight.IsDebris) SettleMegaWeaponBlocks(flight);
                    else
                        foreach (var child in ScatterMegaWeapon(flight))
                            if (remaining > 0) pending.Enqueue((child, remaining));
                    break;
                }
                flight.Position = to;
                flight.Velocity -= Vector3.UnitY * (MegaWeaponGravity * slice);
                flight.Age += slice;
                remaining -= slice;
                if (flight.Age < 12 && flight.Position.Y > -2 && flight.Position.Y < WorldGrid.Size + 8 &&
                    flight.Position.X is > -4 and < WorldGrid.Size + 4 && flight.Position.Z is > -4 and < WorldGrid.Size + 4) continue;
                _megaFlights.Remove(flight);
                SettleMegaWeaponBlocks(flight);
                break;
            }
        }
    }

    private static float MegaBlockImpactDamage(MegaWeaponFlight flight, HeldKiloBlock block)
    {
        float share = flight.IsDebris || flight.Formation == MegaWeaponFormation.Chunk ? .25f : flight.Blocks.Count / 4f;
        // Damage is the original whole-weapon attack, also retained by debris.
        // Apply the contacting block's surviving durability once at impact.
        return flight.Damage * share * (block.Hp / Balance.DirtBlockHp);
    }

    private bool FindMegaWeaponImpact(MegaWeaponFlight flight, Vector3 from, Vector3 velocity, float duration, out float fraction,
        out List<(Turret Tower, HeldKiloBlock Block)> towers, out List<(Cell Cell, HeldKiloBlock Block)> terrain)
    {
        fraction = float.PositiveInfinity;
        towers = [];
        terrain = [];
        var towerHits = new List<(Turret Tower, HeldKiloBlock Block, float Fraction)>();
        var terrainHits = new List<(Cell Cell, HeldKiloBlock Block, float Fraction)>();
        foreach (var block in flight.Blocks)
        {
            var start = from + block.Offset;
            var half = new Vector3(.49999f);
            foreach (var turret in Turrets)
                if (turret.Hp > 0 && !flight.DamagedTowerIds.Contains(turret.Id) &&
                    MegaBallisticBoxIntersection(start, velocity, duration, turret.Cell.Center - new Vector3(.5f) - half,
                        turret.Cell.Center + new Vector3(.5f) + half, out var hit))
                {
                    towerHits.Add((turret, block, hit));
                    fraction = Math.Min(fraction, hit);
                }
            MegaBallisticBounds(start, velocity, duration, half, out var min, out var max);
            for (int x = Math.Max(0, (int)MathF.Floor(min.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.X)); x++)
                for (int y = Math.Max(0, (int)MathF.Floor(min.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Y)); y++)
                    for (int z = Math.Max(0, (int)MathF.Floor(min.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Z)); z++)
                    {
                        var cell = new Cell(x, y, z);
                        if (World.GetMaterial(cell) == Material.Air) continue;
                        foreach (var bounds in SolidCollisionBounds(cell))
                            if (MegaBallisticBoxIntersection(start, velocity, duration, bounds.Min - half, bounds.Max + half, out var hit))
                            {
                                terrainHits.Add((cell, block, hit));
                                fraction = Math.Min(fraction, hit);
                            }
                    }
        }
        if (!float.IsFinite(fraction)) return false;
        // Resolve the first physical contact per target. Touching a seam between
        // cubes still produces one impact, using that cube's own remaining HP.
        float firstContact = fraction;
        towers = towerHits.Where(hit => hit.Fraction <= firstContact + .0001f)
            .OrderBy(hit => hit.Fraction).ThenBy(hit => hit.Block.BlockIndex)
            .GroupBy(hit => hit.Tower.Id).Select(group => (group.First().Tower, group.First().Block)).ToList();
        terrain = terrainHits.Where(hit => hit.Fraction <= firstContact + .0001f)
            .OrderBy(hit => hit.Fraction).ThenBy(hit => hit.Block.BlockIndex)
            .GroupBy(hit => hit.Cell).Select(group => (group.First().Cell, group.First().Block)).ToList();
        return true;
    }

    private List<MegaWeaponFlight> ScatterMegaWeapon(MegaWeaponFlight flight)
    {
        var scattered = new List<MegaWeaponFlight>();
        for (int index = 0; index < flight.Blocks.Count; index++)
        {
            var block = flight.Blocks[index];
            var offset = block.Offset;
            var incident = new Vector3(flight.Velocity.X, 0, flight.Velocity.Z);
            var away = incident.LengthSquared() > .00001f ? -Vector3.Normalize(incident) : Vector3.UnitX;
            var side = new Vector3(-away.Z, 0, away.X);
            block.Offset = Vector3.Zero;
            var child = new MegaWeaponFlight
            {
                OwnerId = flight.OwnerId, Formation = flight.Formation, IsDebris = true,
                Position = flight.Position + offset,
                Velocity = away * 2 + side * (index % 2 == 0 ? -3 : 3) + Vector3.UnitY * (2 + index / 2),
                Damage = flight.Damage, Blocks = [block], DamagedTowerIds = new HashSet<int>(flight.DamagedTowerIds)
            };
            _megaFlights.Add(child);
            scattered.Add(child);
        }
        return scattered;
    }

    private void SettleMegaWeaponBlocks(MegaWeaponFlight flight)
    {
        foreach (var block in flight.Blocks)
        {
            var center = flight.Position + block.Offset;
            var cell = new Cell(Math.Clamp((int)MathF.Floor(center.X), 0, WorldGrid.Size - 1),
                Math.Clamp((int)MathF.Floor(center.Y), 0, WorldGrid.Size - 1), Math.Clamp((int)MathF.Floor(center.Z), 0, WorldGrid.Size - 1));
            _pendingBlockDrops.Add(new BlockDrop(cell, block.Material, block.Hp, block.WasBarrier));
        }
    }

    internal List<SavedMegaWeaponFlight> ExportMegaWeaponFlights() => _megaFlights.Select(flight => new SavedMegaWeaponFlight
    {
        OwnerId = flight.OwnerId, Formation = flight.Formation, IsDebris = flight.IsDebris,
        X = flight.Position.X, Y = flight.Position.Y, Z = flight.Position.Z,
        VelocityX = flight.Velocity.X, VelocityY = flight.Velocity.Y, VelocityZ = flight.Velocity.Z,
        Age = flight.Age, Damage = flight.Damage, DamagedTowerIds = flight.DamagedTowerIds.ToList(),
        Blocks = flight.Blocks.Select(block => new SavedMegaWeaponPart
        {
            BlockIndex = block.BlockIndex, Material = block.Material, Hp = block.Hp, WasBarrier = block.WasBarrier,
            OffsetX = block.Offset.X, OffsetY = block.Offset.Y, OffsetZ = block.Offset.Z
        }).ToList()
    }).ToList();

    internal void RestoreMegaWeaponFlights(IEnumerable<SavedMegaWeaponFlight> saved)
    {
        var states = saved.ToArray();
        ValidateMegaWeaponFlights(states);
        _megaFlights.Clear();
        foreach (var state in states)
            _megaFlights.Add(new MegaWeaponFlight
            {
                OwnerId = state.OwnerId, Formation = state.Formation, IsDebris = state.IsDebris,
                Position = new Vector3(state.X, state.Y, state.Z), Velocity = new Vector3(state.VelocityX, state.VelocityY, state.VelocityZ),
                Age = state.Age, Damage = state.Damage, DamagedTowerIds = state.DamagedTowerIds.ToHashSet(),
                Blocks = state.Blocks.Select(block => new HeldKiloBlock(block.BlockIndex, block.Material, block.Hp, block.WasBarrier)
                { Offset = new Vector3(block.OffsetX, block.OffsetY, block.OffsetZ) }).ToList()
            });
    }

    internal static void ValidateMegaWeaponFlights(IEnumerable<SavedMegaWeaponFlight> saved)
    {
        foreach (var flight in saved)
        {
            if (flight == null || flight.OwnerId <= 0 || !Enum.IsDefined(flight.Formation) ||
                !float.IsFinite(flight.X) || !float.IsFinite(flight.Y) || !float.IsFinite(flight.Z) ||
                flight.X is < -8 or > WorldGrid.Size + 8 || flight.Y is < -8 or > WorldGrid.Size + 16 || flight.Z is < -8 or > WorldGrid.Size + 8 ||
                !float.IsFinite(flight.VelocityX) || !float.IsFinite(flight.VelocityY) || !float.IsFinite(flight.VelocityZ) ||
                Math.Abs(flight.VelocityX) > 256 || Math.Abs(flight.VelocityY) > 256 || Math.Abs(flight.VelocityZ) > 256 ||
                !float.IsFinite(flight.Age) || flight.Age is < 0 or > 12 || !float.IsFinite(flight.Damage) || flight.Damage <= 0 ||
                flight.Blocks == null || flight.Blocks.Count is < 1 or > 4 || flight.IsDebris && flight.Blocks.Count != 1 ||
                flight.DamagedTowerIds == null || flight.DamagedTowerIds.Any(id => id <= 0) ||
                flight.DamagedTowerIds.Count != flight.DamagedTowerIds.Distinct().Count())
                throw new InvalidDataException("Invalid saved Mega projectile motion, damage, or material count.");
            var occupied = new List<(Vector3 Offset, int Index)>();
            foreach (var block in flight.Blocks)
            {
                if (block == null || block.BlockIndex is < 0 or > 3 || !Balance.IsTerrainMaterial(block.Material) ||
                    !float.IsFinite(block.Hp) || block.Hp <= 0 || block.Hp > Balance.BlockHp(block.Material) ||
                    !float.IsFinite(block.OffsetX) || !float.IsFinite(block.OffsetY) || !float.IsFinite(block.OffsetZ) ||
                    Math.Abs(block.OffsetX) > 1.5f || Math.Abs(block.OffsetY) > 1.5f || Math.Abs(block.OffsetZ) > 1.5f)
                    throw new InvalidDataException("Invalid saved Mega projectile material or offset.");
                var offset = new Vector3(block.OffsetX, block.OffsetY, block.OffsetZ);
                if (flight.IsDebris && offset != Vector3.Zero || occupied.Any(other => other.Index == block.BlockIndex ||
                    Math.Abs(other.Offset.X - offset.X) < .99999f && Math.Abs(other.Offset.Y - offset.Y) < .99999f &&
                    Math.Abs(other.Offset.Z - offset.Z) < .99999f))
                    throw new InvalidDataException("Saved Mega projectile blocks overlap or share an index.");
                occupied.Add((offset, block.BlockIndex));
            }
        }
    }
}
