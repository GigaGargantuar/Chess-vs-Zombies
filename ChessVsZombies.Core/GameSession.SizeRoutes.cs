using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private readonly Dictionary<ZombieSize, SizedNavigationField> _sizeRoutes = [];

    private sealed record SizedNavigationField(int WorldRevision, int TopologyRevision,
        Dictionary<Cell, Cell> Routes, Dictionary<Cell, Cell> Fallback);

    private void ResetSizeRoutes() => _sizeRoutes.Clear();

    private bool TryGetSizedRoute(Zombie zombie, Cell current, out Cell next, bool includeFallback = true)
    {
        next = current;
        if (zombie.Size == ZombieSize.Standard) return false;
        if (!_sizeRoutes.TryGetValue(zombie.Size, out var field) ||
            field.WorldRevision != World.Revision || field.TopologyRevision != _topologyRevision)
            _sizeRoutes[zombie.Size] = field = BuildSizedNavigationCore(zombie.Size, field);
        if (field.Routes.TryGetValue(current, out next)) return true;
        if (!includeFallback) { next = current; return false; }
        // Never fall through to a standard-sized goal that intersects the core
        // or a ceiling. An isolated body waits until the shared field changes.
        if (!field.Fallback.TryGetValue(current, out next)) next = current;
        return true;
    }

    private bool CanUseSizedBridgeStep(Zombie zombie, Cell from, Cell to) =>
        IsJumpStep(from, to) ? CanPlanJump(from, to, out _, zombie) :
        IsValidStep(from, to) && TrySizedStepBreaches(from, to, zombie, out _);

    private SizedNavigationField BuildSizedNavigation(ZombieSize size) => BuildSizedNavigationCore(size, null);

    private SizedNavigationField BuildSizedNavigationCore(ZombieSize size, SizedNavigationField? previousField)
    {
        using var profile = Profile?.Measure(size switch
        {
            ZombieSize.Milli => "SizedNavigationRebuild.Milli",
            ZombieSize.Kilo => "SizedNavigationRebuild.Kilo",
            ZombieSize.Mega => "SizedNavigationRebuild.Mega",
            _ => "SizedNavigationRebuild.Other"
        });
        var shape = new Zombie { Size = size };
        const int edgesPerNode = 16;
        static int Index(Cell cell) => (cell.X * WorldGrid.Size + cell.Y) * WorldGrid.Size + cell.Z;
        var workspace = _sizedNavigationWorkspace ??= new SizedNavigationWorkspace();
        var rows = workspace.Rows;
        Array.Clear(rows);
        var nodes = workspace.Nodes;
        nodes.Clear();
        var goals = workspace.Goals;
        goals.Clear();
        var empty = workspace.Empty;
        empty.Clear();
        var clearNodes = workspace.ClearNodes;
        Array.Clear(clearNodes);
        // A grid-aligned Kilo fills its complete 2x2x2 anchor volume. Its
        // axis-aligned step sweeps therefore cover whole cells, including any
        // partial obsidian in them; there is no need to allocate collision
        // iterators and test the same slab geometry for every incoming edge.
        var occupied = shape.IsGiant || size == ZombieSize.Milli ? workspace.Occupied : null;
        var occupiedZRows = occupied != null ? workspace.OccupiedZRows : null;
        if (occupied != null)
            for (int x = 0; x < WorldGrid.Size; x++)
                for (int y = 0; y < WorldGrid.Size; y++)
                {
                    uint row = 0;
                    for (int z = 0; z < WorldGrid.Size; z++)
                    {
                        var cell = new Cell(x, y, z);
                        var material = World.GetMaterial(cell);
                        byte solid = material == Material.Core ? (byte)2 :
                            size == ZombieSize.Milli && material == Material.Obsidian ? (byte)3 :
                            material != Material.Air || HasLiveIndexedTurret(cell) ? (byte)1 : (byte)0;
                        occupied[Index(cell)] = solid;
                        if (solid != 0) row |= 1u << z;
                    }
                    occupiedZRows![x * WorldGrid.Size + y] = row;
                }
        bool Segment(Vector3 from, Vector3 to, HashSet<Cell>? breaches) => shape.IsGiant && occupied != null
            ? TryGiantGridSegmentBreaches(from, to, shape.GridFootprint, breaches, occupied)
            : TrySizedSegmentBreaches(from, to, shape, breaches);
        bool Clear(Cell cell)
        {
            if (occupied == null) return IsBodyClear(shape.StandAt(cell), shape);
            if (size == ZombieSize.Milli)
            {
                byte solid = occupied[Index(cell)];
                return solid == 0 || solid == 3 && IsBodyClear(shape.StandAt(cell), shape);
            }
            uint zMask = GridZRangeMask(cell.Z, cell.Z + shape.GridFootprint);
            for (int x = cell.X; x < cell.X + shape.GridFootprint; x++)
                for (int y = cell.Y; y < cell.Y + shape.GridFootprint; y++)
                    if ((occupiedZRows![x * WorldGrid.Size + y] & zMask) != 0) return false;
            return true;
        }
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 1; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (!HasZombieSupport(cell, shape) ||
                        !(size == ZombieSize.Milli ? occupied![Index(cell)] != 2 :
                            shape.IsGiant ? x + shape.GridFootprint <= WorldGrid.Size &&
                                y + shape.GridFootprint <= WorldGrid.Size && z + shape.GridFootprint <= WorldGrid.Size &&
                                !SizedAnchorOverlapsCore(cell, shape.GridFootprint) :
                            Segment(shape.StandAt(cell), shape.StandAt(cell), null))) continue;
                    rows[Index(cell)] = nodes.Count + 1;
                    nodes.Add(cell);
                    if (SizedAnchorApproachesCore(cell, shape.GridFootprint)) goals.Add(cell);
                    if (Clear(cell))
                    {
                        clearNodes[Index(cell)] = 1;
                        empty.Add(cell);
                    }
                }
        bool IsNode(Cell cell) => WorldGrid.IsInside(cell) && rows[Index(cell)] != 0;
        var edgeOffsets = workspace.EdgeOffsets;
        int offsetSlot = 0;
        foreach (var direction in Horizontal)
        {
            for (int dy = -1; dy <= 1; dy++) edgeOffsets[offsetSlot++] = Index(direction + new Cell(0, dy, 0));
            edgeOffsets[offsetSlot++] = Index(direction + direction);
        }
        var edges = workspace.Edges;
        Array.Fill(edges, float.NaN, 0, nodes.Count * edgesPerNode);
        int referenceAttack = Level.Waves.SelectMany(wave => wave.Groups)
            .Select(group => LevelCatalog.ZombieAttack(group.Kind, group.Tier, shape.Size)).DefaultIfEmpty(Balance.BaseAttack).Max();
        var breachCosts = workspace.BreachCosts;
        breachCosts.Clear();
        float[]? gridCosts = occupied != null ? workspace.GridCosts : null;
        if (gridCosts != null) Array.Fill(gridCosts, float.NaN);
        float Cost(Cell cell)
        {
            if (gridCosts != null)
            {
                ref float cached = ref gridCosts[Index(cell)];
                if (float.IsNaN(cached)) cached = BreachCost(cell, referenceAttack);
                return cached;
            }
            if (!breachCosts.TryGetValue(cell, out float cost)) breachCosts[cell] = cost = BreachCost(cell, referenceAttack);
            return cost;
        }
        // Each edge is consumed before the next is examined, so keep its
        // temporary obstacle set instead of allocating one per supported step.
        var stepBreaches = workspace.StepBreaches;
        stepBreaches.Clear();
        bool Step(Cell from, Cell to)
        {
            stepBreaches.Clear();
            if (!IsValidStep(from, to)) return false;
            var clearance = StepClearance(from, to);
            var waypoint = shape.StandAt(clearance ?? to);
            return Segment(shape.StandAt(from), waypoint, stepBreaches) &&
                Segment(waypoint, shape.StandAt(to), stepBreaches);
        }
        for (int row = 0; row < nodes.Count; row++)
        {
            var destination = nodes[row];
            int slot = row * edgesPerNode;
            foreach (var direction in Horizontal)
            {
                for (int dy = -1; dy <= 1; dy++, slot++)
                {
                    var from = destination + direction + new Cell(0, dy, 0);
                    if (!IsNode(from)) continue;
                    if (occupied != null)
                    {
                        bool valid = shape.IsGiant
                            ? TryGiantGridStepCostWithRows(from, destination, shape.GridFootprint, occupied, occupiedZRows, gridCosts!, referenceAttack, out float cost)
                            : TryMilliGridStepCost(from, destination, occupied, gridCosts!, referenceAttack, out cost);
                        if (valid) edges[slot] = cost;
                    }
                    else if (Step(from, destination))
                    {
                        // Match Enumerable.Sum(float)'s double accumulator and
                        // order without boxing the HashSet enumerator or making
                        // a captured selector delegate for every grid edge.
                        double total = 0;
                        foreach (var block in stepBreaches) total += Cost(block);
                        edges[slot] = (from.Y == destination.Y ? 1 : 2) + (float)total;
                    }
                }
                var launch = destination + direction + direction;
                if (IsNode(launch) && (occupied == null || clearNodes[Index(launch)] != 0 && clearNodes[Index(destination)] != 0) &&
                    CanPlanJump(launch, destination, out _, shape)) edges[slot] = 2;
                slot++;
            }
        }

        var distances = workspace.Distances;
        Array.Fill(distances, float.PositiveInfinity);
        var next = workspace.Next;
        Array.Fill(next, -1);
        var frontier = workspace.Frontier;
        frontier.Clear();
        foreach (var goal in goals)
        {
            int index = Index(goal);
            distances[index] = 0;
            next[index] = index;
            frontier.Enqueue(index, 0);
        }
        while (frontier.TryDequeue(out int index, out float distance))
        {
            if (distance > distances[index]) continue;
            int edgeStart = (rows[index] - 1) * edgesPerNode;
            for (int slot = 0; slot < edgesPerNode; slot++)
            {
                if (float.IsNaN(edges[edgeStart + slot])) continue;
                int previous = index + edgeOffsets[slot];
                float candidate = distance + edges[edgeStart + slot];
                if (candidate >= distances[previous]) continue;
                distances[previous] = candidate;
                next[previous] = index;
                frontier.Enqueue(previous, candidate);
            }
        }
        var routes = PublishSizedRoutes(nodes, rows, next, previousField?.Routes);

        var ranks = workspace.Ranks;
        Array.Fill(ranks, (int.MaxValue, int.MaxValue, float.PositiveInfinity));
        Array.Fill(next, -1);
        var fallbackFrontier = workspace.FallbackFrontier;
        fallbackFrontier.Clear();
        foreach (var cell in empty)
        {
            int index = Index(cell);
            int dx = cell.X - Math.Clamp(cell.X, 15, 16), dz = cell.Z - Math.Clamp(cell.Z, 15, 16);
            ranks[index] = (dx * dx + dz * dz, Math.Abs(cell.Y - Math.Clamp(cell.Y, 15, 16)), 0);
            next[index] = index;
            fallbackFrontier.Enqueue(index, ranks[index]);
        }
        while (fallbackFrontier.TryDequeue(out int index, out var rank))
        {
            if (rank.CompareTo(ranks[index]) != 0) continue;
            int edgeStart = (rows[index] - 1) * edgesPerNode;
            for (int slot = 0; slot < edgesPerNode; slot++)
            {
                float edge = edges[edgeStart + slot];
                if (float.IsNaN(edge)) continue;
                int previous = index + edgeOffsets[slot];
                var candidate = (rank.Horizontal, rank.Vertical, rank.Travel + edge);
                if (candidate.CompareTo(ranks[previous]) >= 0) continue;
                ranks[previous] = candidate;
                next[previous] = index;
                fallbackFrontier.Enqueue(previous, candidate);
            }
        }
        return new SizedNavigationField(World.Revision, _topologyRevision, routes,
            PublishSizedRoutes(nodes, rows, next, previousField?.Fallback));
    }

    private static Dictionary<Cell, Cell> PublishSizedRoutes(List<Cell> nodes, int[] rows, int[] next,
        Dictionary<Cell, Cell>? previous = null)
    {
        var routes = previous ?? new Dictionary<Cell, Cell>(nodes.Count);
        routes.Clear();
        routes.EnsureCapacity(nodes.Count);
        foreach (var cell in nodes)
        {
            int index = (cell.X * WorldGrid.Size + cell.Y) * WorldGrid.Size + cell.Z;
            if (next[index] >= 0) routes[cell] = nodes[rows[next[index]] - 1];
        }
        return routes;
    }

    private static bool SizedAnchorOverlapsCore(Cell anchor, int footprint) =>
        anchor.X <= 16 && anchor.X + footprint > 15 &&
        anchor.Y <= 16 && anchor.Y + footprint > 15 &&
        anchor.Z <= 16 && anchor.Z + footprint > 15;

    private static bool SizedAnchorApproachesCore(Cell anchor, int footprint)
    {
        bool x = anchor.X <= 16 && anchor.X + footprint > 15;
        bool y = anchor.Y <= 16 && anchor.Y + footprint > 15;
        bool z = anchor.Z <= 16 && anchor.Z + footprint > 15;
        // The occupied integer box may touch any core face, but may never
        // contain a core cell. This is the same test as inspecting all six
        // neighbors of every occupied cell, without footprint-cubed work.
        return !(x && y && z) &&
            ((anchor.X + footprint == 15 || anchor.X == 17) && y && z ||
             (anchor.Y + footprint == 15 || anchor.Y == 17) && x && z ||
             (anchor.Z + footprint == 15 || anchor.Z == 17) && x && y);
    }

    private bool TrySizedStepBreaches(Cell from, Cell to, Zombie shape, out HashSet<Cell> breaches)
    {
        breaches = [];
        return TrySizedStepBreaches(from, to, shape, breaches);
    }

    private bool TrySizedStepBreaches(Cell from, Cell to, Zombie shape, HashSet<Cell> breaches)
    {
        breaches.Clear();
        if (!IsValidStep(from, to)) return false;
        var start = shape.StandAt(from);
        var end = shape.StandAt(to);
        var clearance = StepClearance(from, to);
        var waypoint = clearance.HasValue ? shape.StandAt(clearance.Value) : end;
        return TrySizedSegmentBreaches(start, waypoint, shape, breaches) &&
            TrySizedSegmentBreaches(waypoint, end, shape, breaches);
    }

    private bool TrySizedSegmentBreaches(Vector3 from, Vector3 to, Zombie shape, HashSet<Cell>? breaches)
    {
        // Runtime bridge and breach queries use the same exact shortcut only
        // for canonical whole-cell Kilo endpoints. Fractional movement retains
        // the slab sweep below.
        if (shape.IsGiant && IsGiantGridPoint(from, shape) && IsGiantGridPoint(to, shape) &&
            (from.X == to.X && from.Z == to.Z || from.X == to.X && from.Y == to.Y || from.Y == to.Y && from.Z == to.Z))
            return TryGiantGridSegmentBreaches(from, to, shape.GridFootprint, breaches);
        var body = shape.BodyHalfExtents;
        var boundsMin = Vector3.Min(from, to) - body;
        var boundsMax = Vector3.Max(from, to) + body;
        if (boundsMin.X < 0 || boundsMin.Y < 0 || boundsMin.Z < 0 ||
            boundsMax.X > WorldGrid.Size || boundsMax.Y > WorldGrid.Size || boundsMax.Z > WorldGrid.Size) return false;
        var movement = to - from;
        for (int x = (int)MathF.Floor(boundsMin.X); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(boundsMax.X)); x++)
            for (int y = (int)MathF.Floor(boundsMin.Y); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(boundsMax.Y)); y++)
                for (int z = (int)MathF.Floor(boundsMin.Z); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(boundsMax.Z)); z++)
                {
                    var cell = new Cell(x, y, z);
                    if (World.GetMaterial(cell) == Material.Air && !HasLiveIndexedTurret(cell)) continue;
                    foreach (var solid in SolidCollisionBounds(cell))
                    {
                        var min = solid.Min - body;
                        var max = solid.Max + body;
                        float enter = 0, leave = 1;
                        if (!JumpAxisInterval(from.X, movement.X, min.X + .00001f, max.X - .00001f, ref enter, ref leave) ||
                            !JumpAxisInterval(from.Y, movement.Y, min.Y + .00001f, max.Y - .00001f, ref enter, ref leave) ||
                            !JumpAxisInterval(from.Z, movement.Z, min.Z + .00001f, max.Z - .00001f, ref enter, ref leave)) continue;
                        if (World.GetMaterial(cell) == Material.Core) return false;
                        breaches?.Add(cell);
                    }
                }
        return true;
    }

    private static bool IsGridPoint(Vector3 point) =>
        point.X == MathF.Floor(point.X) && point.Y == MathF.Floor(point.Y) && point.Z == MathF.Floor(point.Z);

    private static bool IsGiantGridPoint(Vector3 point, Zombie shape) =>
        IsGridPoint(point - new Vector3(shape.BodyScale * .5f));

    private bool TryMilliGridStepCost(Cell from, Cell to, byte[] occupied, float[] costs, int attack, out float cost)
    {
        Span<int> breached = stackalloc int[6];
        int count = 0;
        var waypoint = StepClearance(from, to) ?? to;
        int fromIndex = (from.X * WorldGrid.Size + from.Y) * WorldGrid.Size + from.Z;
        int waypointIndex = (waypoint.X * WorldGrid.Size + waypoint.Y) * WorldGrid.Size + waypoint.Z;
        int toIndex = (to.X * WorldGrid.Size + to.Y) * WorldGrid.Size + to.Z;
        byte fromSolid = occupied[fromIndex], waypointSolid = occupied[waypointIndex], toSolid = occupied[toIndex];
        if (fromSolid != 3 && waypointSolid != 3 && toSolid != 3)
        {
            if (fromSolid == 2 || waypointSolid == 2 || toSolid == 2)
            {
                cost = 0;
                return false;
            }
            // Each one-cell axis segment visits only its two endpoints. Keep
            // their original x/y/z order and then append the second segment's
            // new endpoint. Slabs below retain their exact swept intersection.
            int first = Math.Min(fromIndex, waypointIndex), second = Math.Max(fromIndex, waypointIndex);
            if (occupied[first] != 0) breached[count++] = first;
            if (second != first && occupied[second] != 0) breached[count++] = second;
            if (toIndex != first && toIndex != second && toSolid != 0) breached[count++] = toIndex;
        }
        else if (!AppendMilliGridSweep(from, waypoint, occupied, breached, ref count) ||
                 waypoint != to && !AppendMilliGridSweep(waypoint, to, occupied, breached, ref count))
        {
            cost = 0;
            return false;
        }
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            int index = breached[i];
            ref float cached = ref costs[index];
            if (float.IsNaN(cached))
                cached = BreachCost(new Cell(index / (WorldGrid.Size * WorldGrid.Size), index / WorldGrid.Size % WorldGrid.Size, index % WorldGrid.Size), attack);
            total += cached;
        }
        cost = (from.Y == to.Y ? 1 : 2) + (float)total;
        return true;
    }

    private bool AppendMilliGridSweep(Cell from, Cell to, byte[] occupied, Span<int> breached, ref int count)
    {
        // The canonical small body stays inside its anchor cell on each axis.
        // Whole cubes along an axis sweep are therefore exact contacts. Crusts
        // retain the narrow body's actual slab sweep rather than filling a cell.
        for (int x = Math.Min(from.X, to.X); x <= Math.Max(from.X, to.X); x++)
            for (int y = Math.Min(from.Y, to.Y); y <= Math.Max(from.Y, to.Y); y++)
                for (int z = Math.Min(from.Z, to.Z); z <= Math.Max(from.Z, to.Z); z++)
                {
                    int index = (x * WorldGrid.Size + y) * WorldGrid.Size + z;
                    byte solid = occupied[index];
                    if (solid == 2) return false;
                    if (solid == 0 || breached[..count].Contains(index)) continue;
                    if (solid == 3)
                    {
                        var start = from.Center - Vector3.UnitY * .25f;
                        var end = to.Center - Vector3.UnitY * .25f;
                        var movement = end - start;
                        var body = new Vector3(.1f, .195f, .1f);
                        bool contact = false;
                        foreach (var bounds in SolidCollisionBounds(new Cell(x, y, z)))
                        {
                            var min = bounds.Min - body;
                            var max = bounds.Max + body;
                            float enter = 0, leave = 1;
                            if (JumpAxisInterval(start.X, movement.X, min.X + .00001f, max.X - .00001f, ref enter, ref leave) &&
                                JumpAxisInterval(start.Y, movement.Y, min.Y + .00001f, max.Y - .00001f, ref enter, ref leave) &&
                                JumpAxisInterval(start.Z, movement.Z, min.Z + .00001f, max.Z - .00001f, ref enter, ref leave))
                            {
                                contact = true;
                                break;
                            }
                        }
                        if (!contact) continue;
                    }
                    breached[count++] = index;
                }
        return true;
    }

    private bool TryKiloGridStepCost(Cell from, Cell to, byte[] occupied, float[] costs, int attack, out float cost)
        => TryGiantGridStepCost(from, to, 2, occupied, costs, attack, out cost);

    private bool TryGiantGridStepCost(Cell from, Cell to, int footprint, byte[] occupied, float[] costs, int attack, out float cost)
        => TryGiantGridStepCostWithRows(from, to, footprint, occupied, null, costs, attack, out cost);

    private bool TryGiantGridStepCostWithRows(Cell from, Cell to, int footprint, byte[] occupied,
        uint[]? occupiedZRows, float[] costs, int attack, out float cost)
    {
        // Scan the two boxes in their original x/y/z order, skipping only the
        // overlap already inspected by the first sweep. The ordinary grid
        // solids cannot change between these queries, so this retains the
        // HashSet's first-seen order without searching it for every cube.
        Span<int> breached = stackalloc int[128];
        int count = 0;
        var waypoint = StepClearance(from, to) ?? to;
        var firstMin = new Cell(Math.Min(from.X, waypoint.X), Math.Min(from.Y, waypoint.Y), Math.Min(from.Z, waypoint.Z));
        var firstMax = new Cell(Math.Max(from.X, waypoint.X) + footprint,
            Math.Max(from.Y, waypoint.Y) + footprint, Math.Max(from.Z, waypoint.Z) + footprint);
        if (!AppendGiantStepSweepWithRows(from, waypoint, footprint, occupied, occupiedZRows, breached, ref count, firstMin, firstMin) ||
            waypoint != to && !AppendGiantStepSweepWithRows(waypoint, to, footprint, occupied, occupiedZRows, breached, ref count, firstMin, firstMax))
        {
            cost = 0;
            return false;
        }
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            int index = breached[i];
            ref float cached = ref costs[index];
            if (float.IsNaN(cached))
                cached = BreachCost(new Cell(index / (WorldGrid.Size * WorldGrid.Size), index / WorldGrid.Size % WorldGrid.Size, index % WorldGrid.Size), attack);
            total += cached;
        }
        cost = (from.Y == to.Y ? 1 : 2) + (float)total;
        return true;
    }

    private static uint GridZRangeMask(int min, int max) => min >= max ? 0 :
        (uint.MaxValue << min) & (uint.MaxValue >> (WorldGrid.Size - max));

    private static bool AppendGiantStepSweepWithRows(Cell from, Cell to, int footprint, byte[] occupied,
        uint[]? occupiedZRows, Span<int> breached, ref int count, Cell excludedMin, Cell excludedMax)
    {
        if (occupiedZRows is null)
            return AppendGiantStepSweep(from, to, footprint, occupied, breached, ref count, excludedMin, excludedMax);
        int minX = Math.Min(from.X, to.X), minY = Math.Min(from.Y, to.Y), minZ = Math.Min(from.Z, to.Z);
        int maxX = Math.Max(from.X, to.X) + footprint, maxY = Math.Max(from.Y, to.Y) + footprint,
            maxZ = Math.Max(from.Z, to.Z) + footprint;
        uint zMask = GridZRangeMask(minZ, maxZ);
        uint excludedZMask = GridZRangeMask(excludedMin.Z, excludedMax.Z);
        for (int x = minX; x < maxX; x++)
            for (int y = minY; y < maxY; y++)
            {
                int row = x * WorldGrid.Size + y;
                uint solids = occupiedZRows[row] & zMask;
                if (x >= excludedMin.X && x < excludedMax.X && y >= excludedMin.Y && y < excludedMax.Y)
                    solids &= ~excludedZMask;
                // Clearing the lowest bit retains the original ascending z order,
                // including both core rejection and first-seen breach summation.
                while (solids != 0)
                {
                    int index = row * WorldGrid.Size + BitOperations.TrailingZeroCount(solids);
                    if (occupied[index] == 2) return false;
                    breached[count++] = index;
                    solids &= solids - 1;
                }
            }
        return true;
    }

    private static bool AppendGiantStepSweep(Cell from, Cell to, int footprint, byte[] occupied,
        Span<int> breached, ref int count, Cell excludedMin, Cell excludedMax)
    {
        int minX = Math.Min(from.X, to.X), minY = Math.Min(from.Y, to.Y), minZ = Math.Min(from.Z, to.Z);
        int maxX = Math.Max(from.X, to.X) + footprint, maxY = Math.Max(from.Y, to.Y) + footprint, maxZ = Math.Max(from.Z, to.Z) + footprint;
        for (int x = minX; x < maxX; x++)
            for (int y = minY; y < maxY; y++)
                for (int z = minZ; z < maxZ; z++)
                {
                    if (x >= excludedMin.X && x < excludedMax.X && y >= excludedMin.Y && y < excludedMax.Y &&
                        z >= excludedMin.Z && z < excludedMax.Z) continue;
                    int index = (x * WorldGrid.Size + y) * WorldGrid.Size + z;
                    byte solid = occupied[index];
                    if (solid == 2) return false;
                    if (solid != 0) breached[count++] = index;
                }
        return true;
    }

    private static bool AppendKiloGridSweep(Cell from, Cell to, byte[] occupied, Span<int> breached, ref int count)
        => AppendGiantGridSweep(from, to, 2, occupied, breached, ref count);

    private static bool AppendGiantGridSweep(Cell from, Cell to, int footprint, byte[] occupied, Span<int> breached, ref int count)
    {
        int minX = Math.Min(from.X, to.X), minY = Math.Min(from.Y, to.Y), minZ = Math.Min(from.Z, to.Z);
        int maxX = Math.Max(from.X, to.X) + footprint, maxY = Math.Max(from.Y, to.Y) + footprint, maxZ = Math.Max(from.Z, to.Z) + footprint;
        // The node builder has already rejected all out-of-bounds bodies.
        for (int x = minX; x < maxX; x++)
            for (int y = minY; y < maxY; y++)
                for (int z = minZ; z < maxZ; z++)
                {
                    int index = (x * WorldGrid.Size + y) * WorldGrid.Size + z;
                    byte solid = occupied[index];
                    if (solid == 2) return false;
                    if (solid == 0 || breached[..count].Contains(index)) continue;
                    breached[count++] = index;
                }
        return true;
    }

    private bool TryKiloGridSegmentBreaches(Vector3 from, Vector3 to, HashSet<Cell>? breaches, byte[]? occupied = null)
        => TryGiantGridSegmentBreaches(from, to, 2, breaches, occupied);

    private bool TryGiantGridSegmentBreaches(Vector3 from, Vector3 to, int footprint, HashSet<Cell>? breaches, byte[]? occupied = null)
    {
        var half = new Vector3(footprint * .5f);
        var min = Vector3.Min(from, to) - half;
        var max = Vector3.Max(from, to) + half;
        if (min.X < 0 || min.Y < 0 || min.Z < 0 || max.X > WorldGrid.Size || max.Y > WorldGrid.Size || max.Z > WorldGrid.Size)
            return false;
        for (int x = (int)min.X; x < (int)max.X; x++)
            for (int y = (int)min.Y; y < (int)max.Y; y++)
                for (int z = (int)min.Z; z < (int)max.Z; z++)
                {
                    var cell = new Cell(x, y, z);
                    byte solid;
                    if (occupied != null) solid = occupied[(x * WorldGrid.Size + y) * WorldGrid.Size + z];
                    else
                    {
                        var material = World.GetMaterial(cell);
                        solid = material == Material.Core ? (byte)2 :
                            material != Material.Air || HasLiveIndexedTurret(cell) ? (byte)1 : (byte)0;
                    }
                    if (solid == 2) return false;
                    if (solid != 0) breaches?.Add(cell);
                }
        return true;
    }
}
