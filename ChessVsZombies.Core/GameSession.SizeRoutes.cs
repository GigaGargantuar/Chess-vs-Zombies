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
            _sizeRoutes[zombie.Size] = field = BuildSizedNavigation(zombie.Size);
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

    private SizedNavigationField BuildSizedNavigation(ZombieSize size)
    {
        var shape = new Zombie { Size = size };
        const int count = WorldGrid.Size * WorldGrid.Size * WorldGrid.Size;
        const int edgesPerNode = 16;
        static int Index(Cell cell) => (cell.X * WorldGrid.Size + cell.Y) * WorldGrid.Size + cell.Z;
        var rows = new int[count];
        var nodes = new List<Cell>();
        var goals = new List<Cell>();
        var empty = new List<Cell>();
        // A grid-aligned Kilo fills its complete 2x2x2 anchor volume. Its
        // axis-aligned step sweeps therefore cover whole cells, including any
        // partial obsidian in them; there is no need to allocate collision
        // iterators and test the same slab geometry for every incoming edge.
        var occupied = shape.IsGiant || size == ZombieSize.Milli ? new byte[count] : null;
        if (occupied != null)
            for (int x = 0; x < WorldGrid.Size; x++)
                for (int y = 0; y < WorldGrid.Size; y++)
                    for (int z = 0; z < WorldGrid.Size; z++)
                    {
                        var cell = new Cell(x, y, z);
                        var material = World.GetMaterial(cell);
                        occupied[Index(cell)] = material == Material.Core ? (byte)2 :
                            size == ZombieSize.Milli && material == Material.Obsidian ? (byte)3 :
                            material != Material.Air || HasLiveIndexedTurret(cell) ? (byte)1 : (byte)0;
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
            for (int x = cell.X; x < cell.X + shape.GridFootprint; x++)
                for (int y = cell.Y; y < cell.Y + shape.GridFootprint; y++)
                    for (int z = cell.Z; z < cell.Z + shape.GridFootprint; z++)
                        if (occupied[Index(new Cell(x, y, z))] != 0) return false;
            return true;
        }
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 1; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (!HasZombieSupport(cell, shape) ||
                        !(size == ZombieSize.Milli ? occupied![Index(cell)] != 2 :
                            Segment(shape.StandAt(cell), shape.StandAt(cell), null))) continue;
                    rows[Index(cell)] = nodes.Count + 1;
                    nodes.Add(cell);
                    if (IsCoreApproach(cell, shape)) goals.Add(cell);
                    if (Clear(cell)) empty.Add(cell);
                }
        bool IsNode(Cell cell) => WorldGrid.IsInside(cell) && rows[Index(cell)] != 0;
        var edgeOffsets = new int[edgesPerNode];
        int offsetSlot = 0;
        foreach (var direction in Horizontal)
        {
            for (int dy = -1; dy <= 1; dy++) edgeOffsets[offsetSlot++] = Index(direction + new Cell(0, dy, 0));
            edgeOffsets[offsetSlot++] = Index(direction + direction);
        }
        var edges = new float[nodes.Count * edgesPerNode];
        Array.Fill(edges, float.NaN);
        int referenceAttack = Level.Waves.SelectMany(wave => wave.Groups)
            .Select(group => LevelCatalog.ZombieAttack(group.Kind, group.Tier, shape.Size)).DefaultIfEmpty(Balance.BaseAttack).Max();
        var breachCosts = new Dictionary<Cell, float>();
        float[]? gridCosts = occupied != null ? new float[count] : null;
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
        var stepBreaches = new HashSet<Cell>();
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
                            ? TryGiantGridStepCost(from, destination, shape.GridFootprint, occupied, gridCosts!, referenceAttack, out float cost)
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
                if (IsNode(launch) && (occupied == null || Clear(launch) && Clear(destination)) &&
                    CanPlanJump(launch, destination, out _, shape)) edges[slot] = 2;
                slot++;
            }
        }

        var distances = new float[count];
        Array.Fill(distances, float.PositiveInfinity);
        var next = new int[count];
        Array.Fill(next, -1);
        var frontier = new PriorityQueue<int, float>();
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
        var routes = PublishSizedRoutes(nodes, rows, next);

        var ranks = new (int Horizontal, int Vertical, float Travel)[count];
        Array.Fill(ranks, (int.MaxValue, int.MaxValue, float.PositiveInfinity));
        Array.Fill(next, -1);
        var fallbackFrontier = new PriorityQueue<int, (int Horizontal, int Vertical, float Travel)>();
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
        return new SizedNavigationField(World.Revision, _topologyRevision, routes, PublishSizedRoutes(nodes, rows, next));
    }

    private static Dictionary<Cell, Cell> PublishSizedRoutes(List<Cell> nodes, int[] rows, int[] next)
    {
        var routes = new Dictionary<Cell, Cell>();
        foreach (var cell in nodes)
        {
            int index = (cell.X * WorldGrid.Size + cell.Y) * WorldGrid.Size + cell.Z;
            if (next[index] >= 0) routes[cell] = nodes[rows[next[index]] - 1];
        }
        return routes;
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
        if (!AppendMilliGridSweep(from, waypoint, occupied, breached, ref count) ||
            !AppendMilliGridSweep(waypoint, to, occupied, breached, ref count))
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
    {
        // These two axis-aligned sweeps can overlap in at most eight cells.
        // Retain their first-seen order, exactly as the generic HashSet sweep,
        // without hashing Cell values for every edge through underground rock.
        Span<int> breached = stackalloc int[128];
        int count = 0;
        var waypoint = StepClearance(from, to) ?? to;
        if (!AppendGiantGridSweep(from, waypoint, footprint, occupied, breached, ref count) ||
            !AppendGiantGridSweep(waypoint, to, footprint, occupied, breached, ref count))
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
