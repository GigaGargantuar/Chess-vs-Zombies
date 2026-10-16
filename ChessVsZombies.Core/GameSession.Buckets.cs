using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private LiquidBucketSystem? _buckets;
    public LiquidBucketSystem Buckets => _buckets ??= new LiquidBucketSystem(World, Liquids);
    private readonly Dictionary<Cell, Cell> _bucketRoutes = [];
    private readonly Dictionary<ZombieSize, Dictionary<Cell, Cell>> _sizedBucketRoutes = [];
    private readonly Dictionary<ZombieSize, Dictionary<Cell, Cell>> _waterAttackRoutes = [];
    private readonly Dictionary<(ZombieSize Size, bool AttackLava), BucketRouteWorkspace> _bucketRouteWorkspaces = [];
    private readonly HashSet<Cell> _bucketWorkCells = [];
    private int _bucketWorldRevision = -1, _bucketTopologyRevision = -1;
    private long _bucketLiquidRevision = -1;

    private sealed class BucketRouteWorkspace
    {
        public Dictionary<Cell, Cell> Routes { get; } = [];
        public Dictionary<Cell, bool> Valid { get; } = [];
        public Queue<Cell> Frontier { get; } = [];
    }

    private void ResetBucketBehavior()
    {
        _buckets = null;
        _bucketRoutes.Clear();
        _sizedBucketRoutes.Clear();
        _waterAttackRoutes.Clear();
        _bucketRouteWorkspaces.Clear();
        _bucketWorkCells.Clear();
        _bucketWorldRevision = _bucketTopologyRevision = -1;
        _bucketLiquidRevision = -1;
    }

    private bool NeedsBucketCarrier(Cell spawn, Zombie? shape = null)
    {
        RefreshBucketRoutes();
        if (_bucketWorkCells.Count == 0) return false;
        if (shape is { Size: not ZombieSize.Standard })
        {
            if (!TryGetBucketWorkRoute(shape, spawn, out _)) return false;
            var sizedCurrent = spawn;
            var sizedVisited = new HashSet<Cell>();
            while (sizedVisited.Add(sizedCurrent) &&
                   TryGetSizedRoute(shape, sizedCurrent, out var sizedNext, includeFallback: false))
            {
                if (IsCoreApproach(sizedCurrent, shape)) return false;
                for (int x = 0; x < shape.GridFootprint; x++)
                    for (int z = 0; z < shape.GridFootprint; z++)
                        if (Liquids.GetTotalUnits(sizedNext + new Cell(x, 0, z)) > 0) return true;
                sizedCurrent = sizedNext;
            }
            return !(TryGetBridgeStep(spawn, out var bridge) && CanUseSizedBridgeStep(shape, spawn, bridge));
        }
        int lane = spawn.X == 0 ? 0 : spawn.X == 31 ? 1 : spawn.Z == 0 ? 2 : 3;
        var current = spawn;
        var visited = new HashSet<Cell>();
        while (visited.Add(current) && _routes[lane].TryGetValue(current, out var next))
        {
            if (Liquids.GetTotalUnits(next) > 0) return true;
            current = next;
        }
        // A viable dry route or dry bridge needs no special equipment. A liquid
        // disconnected bank needs a carrier within the existing wave roster.
        if (Adjacent.Any(direction => WorldGrid.IsCore(current + direction))) return false;
        return !TryGetBridgeStep(spawn, out _);
    }

    private bool TryBucketAction(Zombie zombie)
    {
        if (!zombie.CarriesBucket) return false;
        bool grounded = HasSolidFooting(zombie.Position, zombie);
        if (!grounded && !(TryBuoyantSurface(zombie, zombie.Position, out var surface) &&
                          Math.Abs(zombie.Position.Y - surface - .4f * zombie.BodyScale) <= .1f)) return false;
        if (IsKiloUsingBuckets(zombie)) return grounded;
        if (zombie.HeldBucketUnits > 0)
        {
            if (zombie.Cooldown > 0) return grounded;
            bool waterWork = CarriesWaterForLava(zombie);
            if (waterWork && !TryGetWaterThrowDirection(zombie, out _) &&
                TryGetBucketWorkRoute(zombie, zombie.Cell, out var waterStep, attackLava: true) && waterStep != zombie.Cell)
                return false;
            // A filled worker carries its real payload to a reachable outer bank.
            // If no such route exists it can still move the liquid away from the
            // objective instead of camping forever with a full bucket.
            if (!waterWork && !WithinDisposalThrowRange(zombie) &&
                TryGetBucketDisposalStep(zombie, zombie.Cell, out var disposalStep) && disposalStep != zombie.Cell)
                return false;
            bool launched = false;
            for (int index = 0; index < zombie.BucketCount; index++)
            {
                var load = zombie.BucketLoads[index];
                if (waterWork && load.Kind != LiquidKind.Water && !WithinDisposalThrowRange(zombie)) continue;
                Vector3 direction = DisposalDirection(zombie.Position);
                if (load.Kind == LiquidKind.Water && waterWork)
                {
                    // Supplied water is a tool for solidifying the lava barrier,
                    // never a waste parcel thrown away while that work remains.
                    if (!TryGetWaterThrowDirection(zombie, out direction)) continue;
                }
                if (load.Units <= 0 || load.Kind is not { } kind ||
                    !Buckets.Launch(BucketHandPosition(zombie, index), direction, kind, load.Units,
                        standingHeight: zombie.Position.Y - zombie.StandingHeight)) continue;
                load.Units = 0;
                load.Kind = null;
                zombie.FaceToward(direction);
                launched = true;
            }
            if (!launched) return false;
            BeginKiloBucketAction(zombie);
            zombie.Cooldown = Balance.BucketActionSeconds;
            return grounded;
        }
        RefreshBucketRoutes();
        if (zombie.Cooldown > 0) return grounded && TryReachZombieBucketLiquid(zombie, out _);
        bool collected = false;
        for (int index = 0; index < zombie.BucketCount; index++)
        {
            var hand = BucketHandPosition(zombie, index);
            var load = zombie.BucketLoads[index];
            while (load.Units < zombie.BucketCapacity &&
                   TryReachBucketLiquidFromHand(hand, out var source, load.Kind))
            {
                int units = Liquids.TakeSurfaceLiquid(source, zombie.BucketCapacity - load.Units, out var liquid);
                if (units == 0) break;
                load.Units += units;
                load.Kind = liquid;
                zombie.FaceToward(source.Center - zombie.Position);
                collected = true;
            }
        }
        if (!collected) return false;
        BeginKiloBucketAction(zombie);
        RecordZombieProgress(zombie);
        zombie.Cooldown = Balance.BucketActionSeconds;
        return grounded && HasSolidFooting(zombie.Position, zombie);
    }

    private void SpillZombieBucket(Zombie zombie)
    {
        foreach (var load in zombie.BucketLoads)
        {
            if (load.Units <= 0 || load.Kind is not { } kind || !Buckets.Spill(zombie.Position, kind, load.Units)) continue;
            load.Units = 0;
            load.Kind = null;
        }
    }

    private bool TryGetBucketRoute(Zombie zombie, Cell current, out Cell next)
    {
        next = default;
        if (!zombie.CarriesBucket) return false;
        if (zombie.HeldBucketUnits > 0)
            return CarriesWaterForLava(zombie)
                ? TryGetBucketWorkRoute(zombie, current, out next, attackLava: true)
                : TryGetBucketDisposalStep(zombie, current, out next);
        return TryGetBucketWorkRoute(zombie, current, out next);
    }

    private bool CarriesWaterForLava(Zombie zombie)
    {
        if (!zombie.BucketLoads.Any(load => load.Units > 0 && load.Kind == LiquidKind.Water)) return false;
        RefreshBucketRoutes();
        if (!_bucketWorkCells.Any(cell => Liquids.GetUnits(cell, LiquidKind.Lava) > 0)) return false;
        // Buried or physically unreachable lava cannot use this payload yet.
        // Empty it outside normally so the worker can scoop the covering liquid.
        return TryGetWaterThrowDirection(zombie, out _) ||
            TryGetBucketWorkRoute(zombie, zombie.Cell, out _, attackLava: true);
    }

    private static Vector3 DisposalDirection(Vector3 position)
    {
        float closest = position.X;
        var direction = -Vector3.UnitX;
        if (WorldGrid.Size - position.X < closest) { closest = WorldGrid.Size - position.X; direction = Vector3.UnitX; }
        if (position.Z < closest) { closest = position.Z; direction = -Vector3.UnitZ; }
        if (WorldGrid.Size - position.Z < closest) direction = Vector3.UnitZ;
        return direction;
    }

    private static bool WithinDisposalThrowRange(Zombie zombie) =>
        Math.Min(Math.Min(zombie.Position.X, WorldGrid.Size - zombie.Position.X),
            Math.Min(zombie.Position.Z, WorldGrid.Size - zombie.Position.Z)) <= 2.5f;

    private bool TryGetBucketDisposalStep(Zombie zombie, Cell current, out Cell next)
    {
        next = current;
        if (!_retreatRoutes.TryGetValue(zombie.Size, out var routes) || routes.WorldRevision != World.Revision ||
            routes.TopologyRevision != _topologyRevision ||
            routes.LiquidRevision != Liquids.Revision && _retreatNavigationTime - routes.BuiltAt >= .5f)
            _retreatRoutes[zombie.Size] = routes = BuildRetreatRoutes(zombie.Size);
        if (!routes.Steps.TryGetValue(current, out var step) || !RetreatStandIsDry(zombie, step)) return false;
        next = step;
        return true;
    }

    private bool TryGetWaterThrowDirection(Zombie zombie, out Vector3 direction, Cell? targetSource = null)
    {
        direction = default;
        RefreshBucketRoutes();
        float best = float.PositiveInfinity;
        IEnumerable<Cell> sources = targetSource.HasValue ? [targetSource.Value] : _bucketWorkCells;
        foreach (var source in sources)
        {
            if (!Liquids.TryGetSurfaceLiquid(source, out var kind, out _) || kind != LiquidKind.Lava) continue;
            var target = new Vector3(source.Center.X, Liquids.GetSurfaceHeight(source), source.Center.Z);
            var horizontal = target - zombie.Position;
            horizontal.Y = 0;
            float distance = horizontal.Length();
            if (distance < 1.5f || distance > 4.25f || distance >= best ||
                Math.Abs(target.Y - (zombie.Position.Y - zombie.StandingHeight)) > 1.01f) continue;
            var aim = horizontal / distance;
            var landing = BucketHandPosition(zombie, 0) + aim * 3;
            // The actual three-by-three splash must overlap the lava cell.
            if (Math.Abs(landing.X - target.X) > 1.45f || Math.Abs(landing.Z - target.Z) > 1.45f) continue;
            direction = aim;
            best = distance;
        }
        return float.IsFinite(best);
    }

    private bool HasReachableBucketWork(Zombie zombie) =>
        TryGetBucketWorkRoute(zombie, zombie.Cell, out _) || TryReachZombieBucketLiquid(zombie, out _);

    private bool NeedsWaterForLava(Zombie zombie)
    {
        RefreshBucketRoutes();
        return _bucketWorkCells.Any(cell => Liquids.GetUnits(cell, LiquidKind.Lava) > 0) &&
            NeedsBucketCarrier(zombie.Cell, zombie);
    }

    private bool TryReachZombieBucketLiquid(Zombie zombie, out Cell source)
    {
        for (int index = 0; index < zombie.BucketCount; index++)
            if (TryReachBucketLiquidFromHand(BucketHandPosition(zombie, index), out source)) return true;
        source = default;
        return false;
    }

    private bool TryGetBucketWorkRoute(Zombie zombie, Cell current, out Cell next, bool attackLava = false)
    {
        RefreshBucketRoutes();
        if (!attackLava && zombie.Size == ZombieSize.Standard) return _bucketRoutes.TryGetValue(current, out next);
        var cache = attackLava ? _waterAttackRoutes : _sizedBucketRoutes;
        if (!cache.TryGetValue(zombie.Size, out var routes))
            cache[zombie.Size] = routes = BuildSizedBucketRoutes(zombie.Size, attackLava);
        return routes.TryGetValue(current, out next);
    }

    private static Vector3 BucketHandPosition(Zombie zombie, int index)
    {
        var position = zombie.Position + Vector3.UnitY * .15f;
        if (!zombie.IsGiant) return position;
        var right = new Vector3(zombie.Facing.Z, 0, -zombie.Facing.X);
        return position + right * ((index == 0 ? -.45f : .45f) * zombie.BodyScale / 2);
    }

    private bool TryReachBucketLiquid(Vector3 position, out Cell source) =>
        TryReachBucketLiquidFromHand(position + Vector3.UnitY * .15f, out source);

    private bool TryReachBucketLiquidFromHand(Vector3 hand, out Cell source, LiquidKind? requiredKind = null)
    {
        source = default;
        var cell = ToCell(hand);
        float best = float.PositiveInfinity;
        int reach = (int)MathF.Ceiling(Balance.BucketReach);
        for (int x = -reach; x <= reach; x++)
            for (int y = -2; y <= 1; y++)
                for (int z = -reach; z <= reach; z++)
                {
                    var candidate = cell + new Cell(x, y, z);
                    if (!_bucketWorkCells.Contains(candidate) || !Liquids.TryGetSurfaceLiquid(candidate, out var kind, out _) ||
                        requiredKind.HasValue && kind != requiredKind.Value) continue;
                    var surface = new Vector3(candidate.Center.X, Liquids.GetSurfaceHeight(candidate) + .01f, candidate.Center.Z);
                    float distance = Vector3.DistanceSquared(hand, surface);
                    if (distance > Balance.BucketReach * Balance.BucketReach || distance >= best || !HasLineOfSight(hand, surface)) continue;
                    source = candidate;
                    best = distance;
                }
        return float.IsFinite(best);
    }

    private bool IsBucketStand(Cell cell) => WorldGrid.IsInside(cell) && World.GetMaterial(cell) == Material.Air &&
        !HasLiveIndexedTurret(cell) && Liquids.GetTotalUnits(cell) == 0 && HasZombieSupport(cell) && IsBodyClear(cell.Center);

    private bool IsSizedBucketStand(Cell cell, Zombie shape)
    {
        if (!WorldGrid.IsInside(cell) || !HasZombieSupport(cell, shape) || !IsBodyClear(shape.StandAt(cell), shape)) return false;
        for (int x = 0; x < shape.GridFootprint; x++)
            for (int z = 0; z < shape.GridFootprint; z++)
                if (Liquids.GetTotalUnits(cell + new Cell(x, 0, z)) > 0) return false;
        return true;
    }

    private Dictionary<Cell, Cell> BuildSizedBucketRoutes(ZombieSize size, bool attackLava = false)
    {
        var shape = new Zombie { Size = size };
        if (!_bucketRouteWorkspaces.TryGetValue((size, attackLava), out var workspace))
            _bucketRouteWorkspaces[(size, attackLava)] = workspace = new BucketRouteWorkspace();
        // Revision changes still rebuild the complete field immediately. Keep
        // its scratch storage and published dictionary instead of collecting
        // them every time water moves or a worker changes the pool.
        var routes = workspace.Routes;
        var frontier = workspace.Frontier;
        var valid = workspace.Valid;
        routes.Clear();
        frontier.Clear();
        valid.Clear();
        bool IsStand(Cell cell)
        {
            if (!valid.TryGetValue(cell, out bool result)) valid[cell] = result = IsSizedBucketStand(cell, shape);
            return result;
        }
        foreach (var source in _bucketWorkCells)
        {
            if (!Liquids.TryGetSurfaceLiquid(source, out var liquid, out _) || attackLava && liquid != LiquidKind.Lava) continue;
            for (int x = attackLava ? -5 : -3; x <= (attackLava ? 4 : 2); x++)
                for (int y = -2; y <= 2; y++)
                    for (int z = attackLava ? -5 : -3; z <= (attackLava ? 4 : 2); z++)
                    {
                        var stand = source + new Cell(x, y, z);
                        if (routes.ContainsKey(stand) || !IsStand(stand)) continue;
                        shape.Position = shape.StandAt(stand);
                        if (!(attackLava ? TryGetWaterThrowDirection(shape, out _, source) : TryReachZombieBucketLiquid(shape, out _))) continue;
                        routes[stand] = stand;
                        frontier.Enqueue(stand);
                    }
        }
        while (frontier.TryDequeue(out var destination))
            foreach (var horizontal in Horizontal)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    var previous = destination + horizontal + new Cell(0, dy, 0);
                    if (routes.ContainsKey(previous) || !IsStand(previous) ||
                        !CanYieldStep(previous, destination, shape)) continue;
                    routes[previous] = destination;
                    frontier.Enqueue(previous);
                }
                var launch = destination + horizontal + horizontal;
                if (!routes.ContainsKey(launch) && IsStand(launch) &&
                    CanPlanJump(launch, destination, out _, shape))
                {
                    routes[launch] = destination;
                    frontier.Enqueue(launch);
                }
            }
        return routes;
    }

    private void RefreshBucketRoutes()
    {
        if (_bucketWorldRevision == World.Revision && _bucketTopologyRevision == _topologyRevision &&
            _bucketLiquidRevision == Liquids.Revision) return;
        _bucketRoutes.Clear();
        _sizedBucketRoutes.Clear();
        _waterAttackRoutes.Clear();
        _bucketWorkCells.Clear();
        var wet = Liquids.Cells.Where(cell => cell.Water + cell.Mercury + cell.Lava > 0).Select(cell => cell.Cell).ToHashSet();
        var connected = new Queue<Cell>();
        foreach (var cell in wet)
            if (WorldGrid.IsBuildZone(cell) && _bucketWorkCells.Add(cell)) connected.Enqueue(cell);
        // Follow the obstructing pool, including any connected surface spill.
        // Detached puddles thrown back onto the bank are not new work targets.
        while (connected.TryDequeue(out var cell))
            foreach (var direction in Adjacent)
            {
                var next = cell + direction;
                if (wet.Contains(next) && _bucketWorkCells.Add(next)) connected.Enqueue(next);
            }
        var frontier = new Queue<Cell>();
        foreach (var source in _bucketWorkCells)
        {
            if (!Liquids.TryGetSurfaceLiquid(source, out _, out _)) continue;
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 2; y++)
                    for (int z = -1; z <= 1; z++)
                    {
                        var stand = source + new Cell(x, y, z);
                        if (_bucketRoutes.ContainsKey(stand) || !IsBucketStand(stand) || !TryReachBucketLiquid(stand.Center, out _)) continue;
                        _bucketRoutes[stand] = stand;
                        frontier.Enqueue(stand);
                    }
        }
        while (frontier.TryDequeue(out var destination))
            foreach (var horizontal in Horizontal)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    var previous = destination + horizontal + new Cell(0, dy, 0);
                    if (_bucketRoutes.ContainsKey(previous) || !IsBucketStand(previous) || !CanYieldStep(previous, destination)) continue;
                    _bucketRoutes[previous] = destination;
                    frontier.Enqueue(previous);
                }
                var launch = destination + horizontal + horizontal;
                if (!_bucketRoutes.ContainsKey(launch) && IsBucketStand(launch) && CanPlanJump(launch, destination, out _))
                {
                    _bucketRoutes[launch] = destination;
                    frontier.Enqueue(launch);
                }
            }
        _bucketWorldRevision = World.Revision;
        _bucketTopologyRevision = _topologyRevision;
        _bucketLiquidRevision = Liquids.Revision;
    }
}
