using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private const int CoverAttackGroupSize = 5;
    private const int CoverMinimumGroupSize = 3;
    private const float CoverGatheringRadius = 2;
    private const float CoverPatienceSeconds = 30;
    private readonly Dictionary<int, Cell> _coverWaiting = [];
    private readonly Dictionary<int, Vector3> _coverAdvanceOrigins = [];
    private readonly Dictionary<int, CoverGroup> _coverGroups = [];
    private CoverGroup? _coverRally;

    private sealed class CoverGroup(Zombie leader, Turret threat)
    {
        public Zombie Leader { get; } = leader;
        public Turret Threat { get; } = threat;
        public Vector3 Anchor { get; } = leader.Position;
        public float AssembledSeconds { get; set; }
        public Dictionary<float, CoverGatheringRoutes> RoutesByScale { get; } = [];
    }

    private sealed class CoverGatheringRoutes
    {
        public int WorldRevision { get; set; } = -1;
        public int TopologyRevision { get; set; } = -1;
        public Dictionary<Cell, Cell> Routes { get; } = [];
        public Dictionary<Cell, int> Distances { get; } = [];
    }

    /// <summary>Whether a grounded zombie is currently waiting for allies behind cover.</summary>
    public bool IsZombieHiding(Zombie zombie) => zombie.BodyHp > 0 && CanGatherBehindCover(zombie) && !zombie.NextCell.HasValue &&
        HasSolidFooting(zombie.Position, zombie) && _coverWaiting.ContainsKey(zombie.Id);

    private bool CanGatherBehindCover(Zombie zombie) => !zombie.IsGiga && zombie.Size != ZombieSize.Micro && !IsZombieRetreating(zombie) &&
        !(zombie.IsGiant && GetKiloConstructionBlocks(zombie) > 0);

    private void ResetCoverBehavior()
    {
        _coverWaiting.Clear();
        _coverAdvanceOrigins.Clear();
        _coverGroups.Clear();
        _coverRally = null;
    }

    private Zombie? SelectTurretTarget(Turret turret, out ShotKind shot, out float nearest)
        => SelectTurretTargetWithAim(turret, out shot, out nearest, out _);

    private Zombie? SelectTurretTargetWithAim(Turret turret, out ShotKind shot, out float nearest, out Vector3 selectedAim)
    {
        Zombie? target = null;
        shot = ShotKind.Melee;
        nearest = float.PositiveInfinity;
        selectedAim = default;
        foreach (var zombie in Zombies)
        {
            if (zombie.BodyHp <= 0 || !TryGetTurretShotAndAim(turret, zombie, out var kind, out var aim)) continue;
            // A strike requires physical reach. Ranged shots instead hit the first
            // actual solid on their ordinary projectile trajectory, wearing down cover.
            if (kind == ShotKind.Melee && !HasLineOfSight(turret.Cell.Center, aim)) continue;
            float distance = Vector3.DistanceSquared(turret.Cell.Center, aim);
            if (distance >= nearest) continue;
            target = zombie;
            shot = kind;
            nearest = distance;
            selectedAim = aim;
        }
        return target;
    }

    private bool TryGetTurretShot(Turret turret, Zombie zombie, out ShotKind kind)
        => TryGetTurretShotAndAim(turret, zombie, out kind, out _);

    private bool TryGetTurretShotAndAim(Turret turret, Zombie zombie, out ShotKind kind, out Vector3 aim)
    {
        if (zombie.IsGiant)
            return TryGetKiloTurretAim(turret, zombie, null, out kind, out aim);
        aim = zombie.Position;
        var offset = zombie.Cell - turret.Cell;
        bool jump = Economy.IsJumpOffset(turret, offset);
        kind = jump ? ShotKind.Lob : turret.Kind == PieceKind.Pawn && offset.Chebyshev == 1
            ? ShotKind.Melee : ShotKind.Pellet;
        return (jump || Economy.IsDirectionalOffset(turret, offset)) &&
            !TurretShotCrossesCore(turret.Cell.Center, zombie.Position, kind);
    }

    private Vector3 GetTurretAimPoint(Turret turret, Zombie zombie, ShotKind shot) =>
        zombie.IsGiant && TryGetKiloTurretAim(turret, zombie, shot, out _, out var aim)
            ? aim : zombie.Position;

    private bool TryGetKiloTurretAim(Turret turret, Zombie zombie, ShotKind? requiredShot,
        out ShotKind selectedShot, out Vector3 selectedAim)
    {
        selectedShot = ShotKind.Melee;
        selectedAim = zombie.Position;
        float nearest = float.PositiveInfinity;
        foreach (var candidate in EnumerateKiloTurretAims(turret, zombie, requiredShot))
        {
            float distance = Vector3.DistanceSquared(turret.Cell.Center, candidate.Aim);
            if (distance >= nearest) continue;
            nearest = distance;
            selectedShot = candidate.Kind;
            selectedAim = candidate.Aim;
        }
        return float.IsFinite(nearest);
    }

    private IEnumerable<TurretShotAim> EnumerateKiloTurretAims(Turret turret, Zombie zombie, ShotKind? requiredShot = null)
    {
        var min = zombie.Position - zombie.BodyHalfExtents;
        var max = zombie.Position + zombie.BodyHalfExtents;
        var origin = turret.Cell.Center;
        int? jumpRange = null, orthogonalRange = null, planeRange = null, spaceRange = null;
        int Range(UpgradeKind kind)
        {
            // Resolve each family only where its original predicate would first
            // read it. Unused or overflowing allocations retain their lazy behavior.
            switch (kind)
            {
                case UpgradeKind.Jump:
                    if (!jumpRange.HasValue) jumpRange = Economy.GetRange(turret, kind);
                    return jumpRange.GetValueOrDefault();
                case UpgradeKind.Orthogonal:
                    if (!orthogonalRange.HasValue) orthogonalRange = Economy.GetRange(turret, kind);
                    return orthogonalRange.GetValueOrDefault();
                case UpgradeKind.PlaneDiagonal:
                    if (!planeRange.HasValue) planeRange = Economy.GetRange(turret, kind);
                    return planeRange.GetValueOrDefault();
                default:
                    if (!spaceRange.HasValue) spaceRange = Economy.GetRange(turret, kind);
                    return spaceRange.GetValueOrDefault();
            }
        }
        // Only cells with actual body volume count. A grazing grid face must
        // not grant a whole extra firing lane while a Kilo crosses a boundary.
        const float epsilon = .00001f;
        for (int x = Math.Max(0, (int)MathF.Floor(min.X + epsilon)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(max.X - epsilon) - 1); x++)
            for (int y = Math.Max(0, (int)MathF.Floor(min.Y + epsilon)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(max.Y - epsilon) - 1); y++)
                for (int z = Math.Max(0, (int)MathF.Floor(min.Z + epsilon)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(max.Z - epsilon) - 1); z++)
                {
                    var cell = new Cell(x, y, z);
                    var offset = cell - turret.Cell;
                    bool jump = Economy.IsJumpOffsetAtRange(turret, offset, Range(UpgradeKind.Jump));
                    if (!jump && !(Economy.TryGetDirectionalOffset(offset, out var family, out long reach) && reach <= Range(family))) continue;
                    var kind = jump ? ShotKind.Lob : turret.Kind == PieceKind.Pawn && offset.Chebyshev == 1
                        ? ShotKind.Melee : ShotKind.Pellet;
                    if (requiredShot.HasValue && kind != requiredShot.Value) continue;
                    var direction = cell.Center - origin;
                    if (direction.LengthSquared() <= epsilon * epsilon) continue;
                    // Extend the eligible cell's ray: a partially occupied cell
                    // may have its center in front of, or beside, the real body.
                    var ray = Vector3.Normalize(direction) * (WorldGrid.Size * 2);
                    if (!ProjectileBoxIntersection(origin, ray, min, max, out float hit)) continue;
                    var aim = origin + ray * hit;
                    if (TurretShotCrossesCore(origin, aim, kind)) continue;
                    yield return new TurretShotAim(cell, kind, aim);
                }
    }

    private static bool TurretShotCrossesCore(Vector3 origin, Vector3 aim, ShotKind shot)
    {
        var min = new Vector3(WorldGrid.Size / 2 - 1);
        var max = min + new Vector3(2);
        var delta = aim - origin;
        if (shot != ShotKind.Lob)
            return ProjectileBoxIntersection(origin, delta, min, max, out _);

        // Only the indestructible objective vetoes cover fire. Use the actual
        // lob parabola: its straight aiming chord may cross the core while the
        // projectile clears it overhead. X/Z bound the interval over the core;
        // the concave vertical curve reaches its minimum at an interval end.
        float enter = 0, leave = 1;
        if (!ProjectileAxisInterval(origin.X, delta.X, min.X, max.X, ref enter, ref leave) ||
            !ProjectileAxisInterval(origin.Z, delta.Z, min.Z, max.Z, ref enter, ref leave) || leave <= 0)
            return false;
        float height = Math.Max(1, delta.Length() * .4f);
        float Y(float t) => origin.Y + delta.Y * t + 4 * height * t * (1 - t);
        float low = Math.Min(Y(enter), Y(leave));
        float peak = Math.Clamp((delta.Y + 4 * height) / (8 * height), enter, leave);
        return low <= max.Y && Y(peak) >= min.Y;
    }

    private void OnZombieDamaged(Zombie zombie)
    {
        _coverWaiting.Remove(zombie.Id);
        // Armor impacts count as damage too. Keep advancing out of this hiding
        // spot rather than restarting its wait during the next simulation slice.
        if (zombie.BodyHp > 0) _coverAdvanceOrigins[zombie.Id] = zombie.Position;
        else _coverAdvanceOrigins.Remove(zombie.Id);
    }

    private bool IsAdvancingFromCover(Zombie zombie)
    {
        if (!_coverAdvanceOrigins.TryGetValue(zombie.Id, out var origin)) return false;
        if (Vector3.DistanceSquared(origin, zombie.Position) <= 9) return true;
        _coverAdvanceOrigins.Remove(zombie.Id);
        return false;
    }

    private bool IsFinalWaveGroup(Zombie zombie)
    {
        if (zombie.WaveIndex < 0 || zombie.WaveIndex >= Waves.Count) return false;
        var wave = Waves[zombie.WaveIndex];
        int remaining = Math.Max(0, wave.TotalCount - wave.Spawned);
        if (remaining >= CoverAttackGroupSize) return false;
        foreach (var ally in Zombies)
            if (ally.BodyHp > 0 && ally.WaveIndex == zombie.WaveIndex && ++remaining >= CoverAttackGroupSize) return false;
        return true;
    }

    private void ReleaseCoverGroup(IEnumerable<Zombie> members)
    {
        foreach (var ally in members)
        {
            _coverWaiting.Remove(ally.Id);
            _coverAdvanceOrigins[ally.Id] = ally.Position;
            _coverGroups.Remove(ally.Id);
        }
    }

    private bool IsActiveCoverGroup(CoverGroup group) => group.Leader.BodyHp > 0 && CanGatherBehindCover(group.Leader) && group.Threat.Hp > 0 &&
        !group.Leader.NextCell.HasValue &&
        Vector3.DistanceSquared(group.Leader.Position, group.Anchor) < .0001f && HasSolidFooting(group.Anchor, group.Leader) &&
        !IsAdvancingFromCover(group.Leader) && TryFindCloseCover(group.Anchor, group.Threat.Cell.Center, out _);

    private List<Zombie> AssembledCoverGroup(CoverGroup group)
    {
        var members = new List<Zombie>();
        foreach (var ally in Zombies)
            if (IsAssembledCoverMember(ally, group)) members.Add(ally);
        return members;
    }

    private bool IsAssembledCoverMember(Zombie ally, CoverGroup group) =>
        ally.BodyHp > 0 && CanGatherBehindCover(ally) && !IsAdvancingFromCover(ally) &&
        Vector3.DistanceSquared(ally.Position, group.Anchor) <= CoverGatheringRadius * CoverGatheringRadius &&
        HasSolidFooting(ally.Position, ally) && TryFindCloseCover(ally.Position, group.Threat.Cell.Center, out _);

    private void UpdateCoverGroups(float dt)
    {
        foreach (var zombie in Zombies)
            if (zombie.BodyHp > 0 && CanGatherBehindCover(zombie) && IsFinalWaveGroup(zombie))
                ReleaseCoverGroup(Zombies.Where(ally => ally.BodyHp > 0 && CanGatherBehindCover(ally) && ally.WaveIndex == zombie.WaveIndex));

        List<int>? inactive = null;
        foreach (var entry in _coverGroups)
            if (!IsActiveCoverGroup(entry.Value)) (inactive ??= []).Add(entry.Key);
        if (inactive != null)
            foreach (int id in inactive) _coverGroups.Remove(id);

        // Discover every occupied hiding spot before choosing a destination.
        // Otherwise the first zombie updated would claim the entire wave even
        // when a much stronger group was already sheltering across the map.
        foreach (var zombie in Zombies) RegisterCoverGroup(zombie);
        var rally = SelectStrongestCoverGroup();
        foreach (var group in _coverGroups.Values)
            if (group != rally) group.AssembledSeconds = 0;

        if (rally != null)
        {
            var members = AssembledCoverGroup(rally);
            rally.AssembledSeconds = members.Count >= CoverMinimumGroupSize ? rally.AssembledSeconds + dt : 0;
            if (members.Count >= CoverAttackGroupSize || rally.AssembledSeconds > CoverPatienceSeconds)
                ReleaseCoverGroup(members);
        }
    }

    private bool ShouldWaitInCover(Zombie zombie)
    {
        if (!CanGatherBehindCover(zombie) || IsAdvancingFromCover(zombie))
        {
            _coverWaiting.Remove(zombie.Id);
            return false;
        }
        if (IsFinalWaveGroup(zombie))
        {
            ReleaseCoverGroup(Zombies.Where(ally => ally.BodyHp > 0 && CanGatherBehindCover(ally) && ally.WaveIndex == zombie.WaveIndex));
            return false;
        }

        bool discovered = RegisterCoverGroup(zombie);
        var existing = discovered ? SelectStrongestCoverGroup() : FindCoverGroupToJoin(zombie, zombie.Cell);
        if (existing != null)
        {
            if (Vector3.DistanceSquared(zombie.Position, existing.Anchor) <= CoverGatheringRadius * CoverGatheringRadius &&
                TryFindCloseCover(zombie.Position, existing.Threat.Cell.Center, out var groupCover))
                return WaitWithCoverGroup(zombie, existing, groupCover);

            // Joining allies can shelter with the group even when the threat's
            // attack pattern does not currently cross their particular cell.
            _coverWaiting.Remove(zombie.Id);
            return false;
        }
        _coverWaiting.Remove(zombie.Id);
        return false;
    }

    private bool RegisterCoverGroup(Zombie zombie)
    {
        if (zombie.BodyHp <= 0 || !CanGatherBehindCover(zombie) || zombie.NextCell.HasValue || !HasSolidFooting(zombie.Position, zombie) ||
            IsAdvancingFromCover(zombie) || IsFinalWaveGroup(zombie)) return false;
        if (_coverGroups.TryGetValue(zombie.Id, out var own) && IsActiveCoverGroup(own)) return false;
        foreach (var group in _coverGroups.Values)
            if (IsActiveCoverGroup(group) &&
                Vector3.DistanceSquared(zombie.Position, group.Anchor) <= CoverGatheringRadius * CoverGatheringRadius &&
                TryFindCloseCover(zombie.Position, group.Threat.Cell.Center, out _)) return false;
        foreach (var turret in Turrets)
        {
            if (turret.Hp <= 0 || !TryGetTurretShot(turret, zombie, out var shot) || shot != ShotKind.Pellet ||
                !TryFindCloseCover(zombie.Position, turret.Cell.Center, out _)) continue;
            _coverGroups[zombie.Id] = new CoverGroup(zombie, turret);
            return true;
        }
        return false;
    }

    private CoverGroup? SelectStrongestCoverGroup()
    {
        CoverGroup? strongest = null;
        (double Hp, long Attack, int Count, long Leader) best = default;
        foreach (var group in _coverGroups.Values)
        {
            if (!IsActiveCoverGroup(group)) continue;
            double hp = 0;
            long attack = 0;
            int count = 0;
            foreach (var ally in Zombies)
            {
                if (!IsAssembledCoverMember(ally, group)) continue;
                hp += ally.Hp;
                attack = checked(attack + ally.Attack);
                count++;
            }
            var strength = (Hp: hp, Attack: attack, Count: count, Leader: -(long)group.Leader.Id);
            if (strongest != null && strength.CompareTo(best) <= 0) continue;
            strongest = group;
            best = strength;
        }
        if (_coverRally != strongest)
        {
            _coverRally = strongest;
            var assembled = strongest == null ? new HashSet<int>() : AssembledCoverGroup(strongest).Select(zombie => zombie.Id).ToHashSet();
            foreach (int id in _coverWaiting.Keys.ToArray())
                if (!assembled.Contains(id)) _coverWaiting.Remove(id);
        }
        return strongest;
    }

    private bool WaitWithCoverGroup(Zombie zombie, CoverGroup group, Cell cover)
    {
        _coverWaiting[zombie.Id] = cover;
        var gathering = AssembledCoverGroup(group);
        if (gathering.Count >= CoverAttackGroupSize ||
            gathering.Count >= CoverMinimumGroupSize && group.AssembledSeconds > CoverPatienceSeconds)
        {
            ReleaseCoverGroup(gathering);
            return false;
        }
        zombie.FaceToward(group.Threat.Cell.Center - zombie.Position);
        return true;
    }

    private bool TryGetCoverJoinStep(Zombie zombie, Cell current, out Cell next)
    {
        next = default;
        if (!CanGatherBehindCover(zombie) || IsAdvancingFromCover(zombie) || IsFinalWaveGroup(zombie)) return false;
        var group = FindCoverGroupToJoin(zombie, current);
        return group != null && BuildCoverGatheringRoutes(group, zombie).Routes.TryGetValue(current, out next) && next != current;
    }

    private CoverGroup? FindCoverGroupToJoin(Zombie zombie, Cell current)
    {
        // All bodies share one rally point, independent of their own distance
        // or the existence of a weaker local hiding spot. An unreachable body
        // must keep trying its physical navigation instead of camping alone.
        return _coverRally != null && IsActiveCoverGroup(_coverRally) ? _coverRally : SelectStrongestCoverGroup();
    }

    private CoverGatheringRoutes BuildCoverGatheringRoutes(CoverGroup group, Zombie zombie)
    {
        if (!group.RoutesByScale.TryGetValue(zombie.BodyScale, out var routes))
            group.RoutesByScale[zombie.BodyScale] = routes = new CoverGatheringRoutes();
        if (routes.WorldRevision == World.Revision && routes.TopologyRevision == _topologyRevision) return routes;
        routes.WorldRevision = World.Revision;
        routes.TopologyRevision = _topologyRevision;
        routes.Routes.Clear();
        routes.Distances.Clear();
        var frontier = new Queue<Cell>();
        var anchor = group.Leader.Cell;
        for (int x = Math.Max(0, anchor.X - 2); x <= Math.Min(WorldGrid.Size - 1, anchor.X + 2); x++)
            for (int y = Math.Max(0, anchor.Y - 2); y <= Math.Min(WorldGrid.Size - 1, anchor.Y + 2); y++)
                for (int z = Math.Max(0, anchor.Z - 2); z <= Math.Min(WorldGrid.Size - 1, anchor.Z + 2); z++)
                {
                    var cell = new Cell(x, y, z);
                    var standing = zombie.StandAt(cell);
                    if (Vector3.DistanceSquared(standing, group.Anchor) > CoverGatheringRadius * CoverGatheringRadius ||
                        !HasZombieSupport(cell, zombie) || IsZombieObstacle(cell, zombie) || !IsCoverStandingCellClear(cell, zombie) ||
                        !TryFindCloseCover(standing, group.Threat.Cell.Center, out _)) continue;
                    routes.Routes[cell] = cell;
                    routes.Distances[cell] = 0;
                    frontier.Enqueue(cell);
                }
        while (frontier.TryDequeue(out var destination))
            foreach (var horizontal in Horizontal)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var from = destination + horizontal + new Cell(0, dy, 0);
                    AddPrevious(from, destination, false);
                    if (dy == 0) AddPrevious(destination + horizontal + horizontal, destination, true);
                }
        return routes;

        void AddPrevious(Cell from, Cell destination, bool jump)
        {
            if (!WorldGrid.IsInside(from) || routes.Routes.ContainsKey(from) ||
                !HasZombieSupport(from, zombie) || IsZombieObstacle(from, zombie) || !IsCoverStandingCellClear(from, zombie) ||
                (jump ? !CanPlanJump(from, destination, out _, zombie) : !CanYieldStep(from, destination, zombie))) return;
            routes.Routes[from] = destination;
            routes.Distances[from] = routes.Distances[destination] + 1;
            frontier.Enqueue(from);
        }
    }

    private bool IsCoverStandingCellClear(Cell cell, Zombie zombie)
    {
        var position = zombie.StandAt(cell);
        var min = position - zombie.BodyHalfExtents;
        var max = position + zombie.BodyHalfExtents;
        return min.X >= 0 && min.Y >= 0 && min.Z >= 0 && max.X <= WorldGrid.Size && max.Y <= WorldGrid.Size && max.Z <= WorldGrid.Size &&
            !TryFindBodyObstacle(position, position, out _, null, zombie);
    }

    private bool TryFindCloseCover(Vector3 position, Vector3 threat, out Cell cover)
    {
        cover = default;
        var direction = threat - position;
        float length = direction.Length();
        if (length <= .0001f) return false;
        // Only stop at a nearby hiding spot, never somewhere far behind an
        // unrelated obstruction elsewhere on the battlefield.
        var delta = direction * (Math.Min(length, CoverGatheringRadius) / length);
        var min = Vector3.Min(position, position + delta);
        var max = Vector3.Max(position, position + delta);
        float nearest = float.PositiveInfinity;
        for (int x = Math.Max(0, (int)MathF.Floor(min.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.X)); x++)
            for (int y = Math.Max(0, (int)MathF.Floor(min.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Y)); y++)
                for (int z = Math.Max(0, (int)MathF.Floor(min.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Z)); z++)
                {
                    var cell = new Cell(x, y, z);
                    var material = World.GetMaterial(cell);
                    if (material is Material.Air or Material.Core) continue;
                    if (World.GetObsidian(cell) is { } crust)
                    {
                        foreach (var part in crust.Parts)
                            if (ProjectileBoxIntersection(position, delta, part.Min, part.Max, out float hit) && hit < nearest)
                            {
                                nearest = hit;
                                cover = cell;
                            }
                    }
                    else if (ProjectileBoxIntersection(position, delta, new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1), out float hit) && hit < nearest)
                    {
                        nearest = hit;
                        cover = cell;
                    }
                }
        return float.IsFinite(nearest);
    }
}
