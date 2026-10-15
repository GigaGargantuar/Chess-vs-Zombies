using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private LiquidBucketSystem? _buckets;
    public LiquidBucketSystem Buckets => _buckets ??= new LiquidBucketSystem(World, Liquids);
    private readonly Dictionary<Cell, Cell> _bucketRoutes = [];
    private readonly Dictionary<ZombieSize, Dictionary<Cell, Cell>> _sizedBucketRoutes = [];
    private readonly HashSet<Cell> _bucketWorkCells = [];
    private int _bucketWorldRevision = -1, _bucketTopologyRevision = -1;
    private long _bucketLiquidRevision = -1;

    private void ResetBucketBehavior()
    {
        _buckets = null;
        _bucketRoutes.Clear();
        _sizedBucketRoutes.Clear();
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
            // Remove the obstruction from the approach rather than throwing it
            // deeper into the crossing being cleared.
            var direction = zombie.Position - new Vector3(16, zombie.Position.Y, 16);
            if (direction.LengthSquared() < .0001f) direction = -zombie.Facing;
            bool launched = false;
            for (int index = 0; index < zombie.BucketCount; index++)
            {
                var load = zombie.BucketLoads[index];
                if (load.Units <= 0 || load.Kind is not { } kind ||
                    !Buckets.Launch(BucketHandPosition(zombie, index), direction, kind, load.Units,
                        standingHeight: zombie.Position.Y - zombie.StandingHeight)) continue;
                load.Units = 0;
                load.Kind = null;
                launched = true;
            }
            if (!launched) return false;
            BeginKiloBucketAction(zombie);
            zombie.FaceToward(direction);
            zombie.Cooldown = Balance.BucketActionSeconds;
            return grounded;
        }
        RefreshBucketRoutes();
        if (zombie.Cooldown > 0) return grounded && TryReachZombieBucketLiquid(zombie, out _);
        bool collected = false;
        for (int index = 0; index < zombie.BucketCount; index++)
        {
            var hand = BucketHandPosition(zombie, index);
            if (!TryReachBucketLiquidFromHand(hand, out var source)) continue;
            int units = Liquids.TakeSurfaceLiquid(source, Balance.BucketCapacity, out var liquid);
            if (units == 0) continue;
            zombie.BucketLoads[index].Units = units;
            zombie.BucketLoads[index].Kind = liquid;
            zombie.FaceToward(source.Center - zombie.Position);
            collected = true;
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
        if (!zombie.CarriesBucket || zombie.HeldBucketUnits > 0) return false;
        return TryGetBucketWorkRoute(zombie, current, out next);
    }

    private bool HasReachableBucketWork(Zombie zombie) =>
        TryGetBucketWorkRoute(zombie, zombie.Cell, out _) || TryReachZombieBucketLiquid(zombie, out _);

    private bool TryReachZombieBucketLiquid(Zombie zombie, out Cell source)
    {
        for (int index = 0; index < zombie.BucketCount; index++)
            if (TryReachBucketLiquidFromHand(BucketHandPosition(zombie, index), out source)) return true;
        source = default;
        return false;
    }

    private bool TryGetBucketWorkRoute(Zombie zombie, Cell current, out Cell next)
    {
        RefreshBucketRoutes();
        if (zombie.Size == ZombieSize.Standard) return _bucketRoutes.TryGetValue(current, out next);
        if (!_sizedBucketRoutes.TryGetValue(zombie.Size, out var routes))
            _sizedBucketRoutes[zombie.Size] = routes = BuildSizedBucketRoutes(zombie.Size);
        return routes.TryGetValue(current, out next);
    }

    private static Vector3 BucketHandPosition(Zombie zombie, int index)
    {
        var position = zombie.Position + Vector3.UnitY * .15f;
        if (zombie.Size != ZombieSize.Kilo) return position;
        var right = new Vector3(zombie.Facing.Z, 0, -zombie.Facing.X);
        return position + right * (index == 0 ? -.45f : .45f);
    }

    private bool TryReachBucketLiquid(Vector3 position, out Cell source) =>
        TryReachBucketLiquidFromHand(position + Vector3.UnitY * .15f, out source);

    private bool TryReachBucketLiquidFromHand(Vector3 hand, out Cell source)
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
                    if (!_bucketWorkCells.Contains(candidate) || !Liquids.TryGetSurfaceLiquid(candidate, out _, out _)) continue;
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

    private Dictionary<Cell, Cell> BuildSizedBucketRoutes(ZombieSize size)
    {
        var shape = new Zombie { Size = size };
        var routes = new Dictionary<Cell, Cell>();
        var frontier = new Queue<Cell>();
        var valid = new Dictionary<Cell, bool>();
        bool IsStand(Cell cell)
        {
            if (!valid.TryGetValue(cell, out bool result)) valid[cell] = result = IsSizedBucketStand(cell, shape);
            return result;
        }
        foreach (var source in _bucketWorkCells)
        {
            if (!Liquids.TryGetSurfaceLiquid(source, out _, out _)) continue;
            for (int x = -3; x <= 2; x++)
                for (int y = -2; y <= 2; y++)
                    for (int z = -3; z <= 2; z++)
                    {
                        var stand = source + new Cell(x, y, z);
                        if (routes.ContainsKey(stand) || !IsStand(stand)) continue;
                        shape.Position = shape.StandAt(stand);
                        if (!TryReachZombieBucketLiquid(shape, out _)) continue;
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
