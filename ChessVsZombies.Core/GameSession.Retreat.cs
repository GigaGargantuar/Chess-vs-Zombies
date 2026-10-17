using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private sealed class RetreatState
    {
        public float SupplySeconds { get; set; }
    }

    private sealed record RetreatRoutes(int WorldRevision, int TopologyRevision, long LiquidRevision, float BuiltAt,
        Dictionary<Cell, Cell> Steps);

    private readonly Dictionary<int, RetreatState> _retreating = [];
    private readonly HashSet<Zombie> _guardedKilos = [];
    private readonly Dictionary<int, Vector3> _retreatObservedPositions = [];
    private readonly Dictionary<ZombieSize, RetreatRoutes> _retreatRoutes = [];
    private readonly Dictionary<(Vector3 Position, ZombieSize Size, bool Lava, bool Mercury), Vector3?> _retreatBuoyantSteps = [];
    private int _retreatBuoyantWorld = -1, _retreatBuoyantTopology = -1;
    private long _retreatBuoyantLiquid = -1;
    private float _strandedSeconds;
    private float _retreatNavigationTime;

    /// <summary>Retreating enemies remain alive in their original wave while walking back for supplies.</summary>
    public bool IsZombieRetreating(Zombie zombie) => zombie.Hp > 0 && _retreating.ContainsKey(zombie.Id);

    private void FaceKiloTowardShot(Zombie zombie, Vector3 incomingDirection)
    {
        if (zombie.BodyHp <= 0 || !zombie.IsGiant) return;
        zombie.SetGuardFacing(zombie.Size == ZombieSize.Mega
            ? ObserveMegaIncomingFire(zombie, incomingDirection) : -incomingDirection);
        _guardedKilos.Add(zombie);
    }

    private void ResetRetreatBehavior()
    {
        foreach (var zombie in _guardedKilos) zombie.SetGuardFacing(null);
        _guardedKilos.Clear();
        _retreating.Clear();
        _retreatObservedPositions.Clear();
        _retreatRoutes.Clear();
        _retreatBuoyantSteps.Clear();
        _retreatBuoyantWorld = _retreatBuoyantTopology = -1;
        _retreatBuoyantLiquid = -1;
        _strandedSeconds = 0;
        _retreatNavigationTime = 0;
        ResetZombieProgress();
    }

    private void UpdateRetreatBehavior(float dt)
    {
        _retreatNavigationTime += dt;
        foreach (var zombie in _guardedKilos.ToArray())
            if (zombie.Hp <= 0 || !Zombies.Contains(zombie))
            {
                zombie.SetGuardFacing(null);
                _guardedKilos.Remove(zombie);
            }
        foreach (int id in _retreating.Keys.ToArray())
            if (!Zombies.Any(zombie => zombie.Id == id && zombie.Hp > 0)) _retreating.Remove(id);

        bool physicallyAdvancing = false;
        var alive = new HashSet<int>();
        foreach (var zombie in Zombies)
        {
            if (zombie.Hp <= 0) continue;
            alive.Add(zombie.Id);
            if (_retreatObservedPositions.TryGetValue(zombie.Id, out var previous) && !IsZombieRetreating(zombie) &&
                Vector3.DistanceSquared(previous, zombie.Position) > .00000001f) physicallyAdvancing = true;
            _retreatObservedPositions[zombie.Id] = zombie.Position;
        }
        foreach (int id in _retreatObservedPositions.Keys.ToArray())
            if (!alive.Contains(id)) _retreatObservedPositions.Remove(id);
        // Individual guards can revisit the same two positions forever while
        // another enemy walks normally. Detect real gains before the army-wide
        // displacement shortcut below, without treating guard rotation as work.
        UpdateZombieProgress(dt, alive);
        // A continuous climb, descent, jump, or shoreline crossing can spend
        // seconds between supported grid nodes. Observe real displacement;
        // merely retaining a blocked NextCell is never evidence of progress.
        if (physicallyAdvancing) { _strandedSeconds = 0; return; }

        // Evaluate the army once, rather than independently changing plans in
        // each zombie's update. A finite breach or a useful remaining supply
        // keeps the others in the field while that work can still make progress.
        bool anyAttacker = false;
        foreach (var zombie in Zombies)
        {
            if (zombie.Hp <= 0 || IsZombieRetreating(zombie)) continue;
            anyAttacker = true;
            if (IsZombieHurled(zombie) || CanKiloHurlForProgress(zombie) || CanZombieOpenCoreRoute(zombie))
            {
                _strandedSeconds = 0;
                return;
            }
        }
        if (!anyAttacker) { _strandedSeconds = 0; return; }
        _strandedSeconds += dt;
        // Allow an interrupted step, landing, or fresh collapse to resolve
        // before committing the whole field to an outward journey.
        if (_strandedSeconds < 1) return;
        foreach (var zombie in Zombies)
        {
            if (zombie.Hp <= 0 || IsZombieRetreating(zombie)) continue;
            BeginZombieRetreat(zombie);
        }
        _strandedSeconds = 0;
    }

    private bool CanZombieOpenCoreRoute(Zombie zombie)
    {
        if (zombie.Attack <= 0) return false;
        if (zombie.CarriesBucket && HasReachableBucketWork(zombie)) return true;

        var visited = new HashSet<Cell>();
        var current = zombie.Cell;
        while (visited.Add(current))
        {
            if (IsCoreApproach(current, zombie)) return true;
            Cell next;
            bool route = zombie.Size == ZombieSize.Standard
                ? _routes[zombie.Lane % 4].TryGetValue(current, out next)
                : TryGetSizedRoute(zombie, current, out next, includeFallback: false);
            if (!route || next == current || WouldFloatOffDryRoute(zombie, current, next)) break;
            current = next;
        }

        if (CanEscapePillar(zombie)) return true;
        if (zombie.CarriedBlocks + GetKiloConstructionBlocks(zombie) == 0) return false;
        visited.Clear();
        current = zombie.Cell;
        while (visited.Add(current) && TryGetBridgeStep(current, out var next))
        {
            if (next == current || WouldFloatOffDryRoute(zombie, current, next) ||
                !CanUseSizedBridgeStep(zombie, current, next)) return false;
            if (!HasZombieSupport(next, zombie))
            {
                var floor = next + new Cell(0, -1, 0);
                // Only an actually reachable first construction site counts.
                // Later optimistic floors do not grant free inventory or reach.
                return current.Y == next.Y && IsEmpty(next) && IsEmpty(floor) &&
                    World.HasOrthogonalBlockSupport(floor);
            }
            current = next;
        }
        return false;
    }

    private void BeginZombieRetreat(Zombie zombie)
    {
        _retreating[zombie.Id] = new RetreatState();
        _coverWaiting.Remove(zombie.Id);
        _coverGroups.Remove(zombie.Id);
        _coverAdvanceOrigins.Remove(zombie.Id);
        _builderYields.Remove(zombie.Id);
        ClearZombieMovement(zombie);
    }

    private bool WouldFloatOffDryRoute(Zombie zombie, Cell from, Cell to)
    {
        if (IsJumpStep(from, to)) return false;
        var start = zombie.StandAt(from);
        var end = zombie.StandAt(to);
        return TryBuoyantSurface(zombie, start, out _) || TryBuoyantSurface(zombie, end, out _) ||
            TryBuoyantSurface(zombie, Vector3.Lerp(start, end, .5f), out _);
    }

    private bool TryCompleteRetreat(Zombie zombie, float dt)
    {
        if (!_retreating.TryGetValue(zombie.Id, out var state) || !AtSupplyBoundary(zombie, zombie.Position) ||
            !HasSolidFooting(zombie.Position, zombie)) return false;
        // Stop at the real current position without snapping a partial grid step.
        ClearZombieMovement(zombie);
        if (zombie.RetreatReturns >= 3)
        {
            _retreating.Remove(zombie.Id);
            _retreatObservedPositions.Remove(zombie.Id);
            _zombieProgress.Remove(zombie.Id);
            zombie.SetGuardFacing(null);
            _guardedKilos.Remove(zombie);
            DeferZombieToNextWave(zombie);
            return true;
        }
        state.SupplySeconds += dt;
        if (state.SupplySeconds < 1) return true;

        bool giant = zombie.IsGiant;
        bool needsWater = NeedsWaterForLava(zombie);
        bool needsBucket = giant || needsWater || HasReachableBucketWork(zombie);
        // Ordinary carriers exchange their tool and release its real payload.
        // Kilos keep both tools, so their buckets need no emptying or exchange.
        // Failed displacement keeps liquid in the ordinary carrier's bucket.
        if (!giant && zombie.HeldBucketUnits > 0)
        {
            SpillZombieBucket(zombie);
            if (zombie.HeldBucketUnits > 0) return true;
        }
        zombie.CarriesBucket = needsBucket;
        if (!giant)
        {
            zombie.BucketKind = null;
            zombie.BucketUnits = 0;
        }
        // A Mega extracts its weapon from terrain as one chunk, independently
        // of its bridge backpack. Preserve real supplies already carried.
        // Kilo backpack supplies still refill its two shared cover slots.
        zombie.CarriedBlocks = zombie.Size == ZombieSize.Mega ? Math.Max(1, zombie.CarriedBlocks)
            : giant ? Math.Max(0, 2 - GetKiloConstructionBlocks(zombie))
            : needsBucket ? 0 : Math.Max(1, zombie.CarriedBlocks);
        if (needsWater)
            for (int index = 0; index < zombie.BucketCount; index++)
            {
                var load = zombie.BucketLoads[index];
                // An occupied giant bucket retains its actual other liquid.
                // Empty buckets are filled from the supplies outside the map.
                if (load.Units > 0 && load.Kind != LiquidKind.Water) continue;
                int supplied = zombie.BucketCapacity - load.Units;
                load.Kind = LiquidKind.Water;
                load.Units = zombie.BucketCapacity;
                Liquids.RecordSuppliedWater(supplied);
            }
        UpgradeRetreatArmor(zombie);
        zombie.RetreatReturns++;
        zombie.KiloEquipmentMode = KiloEquipmentMode.Covers;
        zombie.KiloBucketActionRemaining = 0;
        zombie.Cooldown = Math.Max(zombie.Cooldown, .25f);
        _retreating.Remove(zombie.Id);
        zombie.SetGuardFacing(null);
        _guardedKilos.Remove(zombie);
        _strandedSeconds = 0;
        ResetZombieProgress(zombie);
        zombie.FaceToward(new Vector3(16, zombie.Position.Y, 16) - zombie.Position);
        return true;
    }

    private void UpgradeRetreatArmor(Zombie zombie)
    {
        var available = BestAvailableArmorTier(zombie.WaveIndex);
        bool alreadyArmored = zombie.Kind is ZombieKind.Armored or ZombieKind.ArmedArmored;
        if (!available.HasValue && !alreadyArmored) return;
        // Authored gear is never downgraded by the campaign's progression cap.
        var best = alreadyArmored && (!available.HasValue || zombie.Tier > available.Value)
            ? zombie.Tier : available!.Value;
        var tier = zombie.RetreatReturns >= 2 ? best : alreadyArmored
            ? (GearTier)Math.Min((int)best, (int)zombie.Tier + 1) : GearTier.Wood;
        float addedHp = zombie.ReequipArmor(tier);
        if (zombie.WaveIndex >= 0 && zombie.WaveIndex < Waves.Count)
            Waves[zombie.WaveIndex].TotalHp += addedHp;
    }

    private bool AtSupplyBoundary(Zombie zombie, Vector3 position)
    {
        var min = position - zombie.BodyHalfExtents;
        var max = position + zombie.BodyHalfExtents;
        // Standard/Milli spawn anchors sit half a tile from the outside face;
        // the exact two-cube Kilo touches that face at its first fitting anchor.
        if (min.X < -.001f || min.Z < -.001f || max.X > WorldGrid.Size + .001f || max.Z > WorldGrid.Size + .001f) return false;
        return min.X <= .401f || min.Z <= .401f || max.X >= WorldGrid.Size - .401f || max.Z >= WorldGrid.Size - .401f;
    }

    private bool TryGetRetreatStep(Zombie zombie, Cell current, out Cell next)
    {
        next = current;
        if (!IsZombieRetreating(zombie)) return false;
        if (!_retreatRoutes.TryGetValue(zombie.Size, out var routes) || routes.WorldRevision != World.Revision ||
            routes.TopologyRevision != _topologyRevision ||
            routes.LiquidRevision != Liquids.Revision && _retreatNavigationTime - routes.BuiltAt >= .5f)
            _retreatRoutes[zombie.Size] = routes = BuildRetreatRoutes(zombie.Size);
        // Small fluid redistributions do not require a new world-wide field on
        // every simulation slice. A cached edge is still checked before use.
        if (routes.Steps.TryGetValue(current, out var step) && RetreatStandIsDry(zombie, step)) next = step;
        // A genuinely isolated body waits for physical support or an available
        // breach; it never teleports to the perimeter or refills in the field.
        return true;
    }

    private RetreatRoutes BuildRetreatRoutes(ZombieSize size)
    {
        var shape = new Zombie { Size = size };
        var nodes = new HashSet<Cell>();
        var steps = new Dictionary<Cell, Cell>();
        var costs = new Dictionary<Cell, float>();
        var frontier = new PriorityQueue<Cell, float>();
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 1; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    var position = shape.StandAt(cell);
                    if (!HasZombieSupport(cell, shape) || !RetreatStandIsDry(shape, cell) ||
                        !TrySizedSegmentBreaches(position, position, shape, null)) continue;
                    nodes.Add(cell);
                    if (!AtSupplyBoundary(shape, position) || !IsBodyClear(position, shape)) continue;
                    costs[cell] = 0;
                    steps[cell] = cell;
                    frontier.Enqueue(cell, 0);
                }
        int referenceAttack = Math.Max(Balance.BaseAttack, Zombies.Where(zombie => zombie.Hp > 0)
            .Select(zombie => zombie.Attack).DefaultIfEmpty(Balance.BaseAttack).Max());
        var breaches = new HashSet<Cell>();
        var breachCosts = new Dictionary<Cell, float>();
        while (frontier.TryDequeue(out var destination, out float distance))
        {
            if (distance > costs[destination]) continue;
            foreach (var direction in Horizontal)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    var from = destination + direction + new Cell(0, dy, 0);
                    if (!nodes.Contains(from) || !TrySizedStepBreaches(from, destination, shape, breaches)) continue;
                    float cost = dy == 0 ? 1 : 2;
                    foreach (var block in breaches)
                    {
                        if (!breachCosts.TryGetValue(block, out float breach))
                            breachCosts[block] = breach = BreachCost(block, referenceAttack);
                        cost += breach;
                    }
                    Add(from, destination, distance + cost);
                }
                var launch = destination + direction + direction;
                if (nodes.Contains(launch) && CanPlanJump(launch, destination, out _, shape)) Add(launch, destination, distance + 2);
            }
        }
        return new RetreatRoutes(World.Revision, _topologyRevision, Liquids.Revision, _retreatNavigationTime, steps);

        void Add(Cell from, Cell to, float cost)
        {
            if (costs.TryGetValue(from, out float known) && known <= cost) return;
            costs[from] = cost;
            steps[from] = to;
            frontier.Enqueue(from, cost);
        }
    }

    private bool RetreatStandIsDry(Zombie zombie, Cell cell)
    {
        var feet = zombie.StandAt(cell) - Vector3.UnitY * (zombie.StandingHeight - .01f);
        return !Liquids.GetLiquidAt(feet).HasValue;
    }

    private bool TryGetRetreatBuoyantStep(Zombie zombie, out Vector3? next)
    {
        next = null;
        if (!IsZombieRetreating(zombie)) return false;
        if (_retreatBuoyantWorld != World.Revision || _retreatBuoyantTopology != _topologyRevision ||
            _retreatBuoyantLiquid != Liquids.Revision)
        {
            _retreatBuoyantSteps.Clear();
            _retreatBuoyantWorld = World.Revision;
            _retreatBuoyantTopology = _topologyRevision;
            _retreatBuoyantLiquid = Liquids.Revision;
        }
        var cacheKey = (zombie.Position, zombie.Size, zombie.SinksIn(LiquidKind.Lava), zombie.SinksIn(LiquidKind.Mercury));
        if (_retreatBuoyantSteps.TryGetValue(cacheKey, out next)) return true;
        var start = zombie.Cell;
        var positions = new Dictionary<Cell, Vector3> { [start] = zombie.Position };
        var previous = new Dictionary<Cell, Cell>();
        var frontier = new Queue<Cell>();
        frontier.Enqueue(start);
        var best = start;
        float bestDistance = RetreatEdgeDistance(zombie, zombie.Position);
        while (frontier.TryDequeue(out var current))
            foreach (var direction in Horizontal)
                foreach (int dy in new[] { 0, 1, -1 })
                {
                    var cell = current + direction + new Cell(0, dy, 0);
                    if (!WorldGrid.IsInside(cell) || positions.ContainsKey(cell)) continue;
                    var position = zombie.StandAt(cell);
                    if (TryBuoyantSurface(zombie, position, out float surface))
                    {
                        position.Y = BuoyantStandHeight(zombie, position, surface);
                        if (zombie.CellAt(position) != cell) continue;
                    }
                    else if (!HasZombieSupport(cell, zombie)) continue;
                    var origin = positions[current];
                    if (Math.Abs(position.Y - origin.Y) > 1.001f || !IsBodyClear(position, zombie)) continue;
                    var crossing = Math.Max(position.Y, origin.Y);
                    var waypoint = new Vector3(origin.X, crossing, origin.Z);
                    var crossingEnd = new Vector3(position.X, crossing, position.Z);
                    if (TryFindBodyObstacle(origin, waypoint, out _, zombie: zombie) ||
                        TryFindBodyObstacle(waypoint, crossingEnd, out _, zombie: zombie) ||
                        TryFindBodyObstacle(crossingEnd, position, out _, zombie: zombie)) continue;
                    positions[cell] = position;
                    previous[cell] = current;
                    frontier.Enqueue(cell);
                    float distance = RetreatEdgeDistance(zombie, position);
                    if (distance < bestDistance) { bestDistance = distance; best = cell; }
                }
        if (best == start) { _retreatBuoyantSteps[cacheKey] = null; return true; }
        while (previous[best] != start) best = previous[best];
        next = positions[best];
        _retreatBuoyantSteps[cacheKey] = next;
        return true;
    }

    private static float RetreatEdgeDistance(Zombie zombie, Vector3 position)
    {
        var half = zombie.BodyHalfExtents;
        return Math.Min(Math.Min(position.X - half.X, WorldGrid.Size - position.X - half.X),
            Math.Min(position.Z - half.Z, WorldGrid.Size - position.Z - half.Z));
    }
}
