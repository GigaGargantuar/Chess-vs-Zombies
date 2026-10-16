using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace ChessVsZombies.Core;

public enum LiquidKind { Water, Mercury, Lava }
public enum CrustFace { West, East, Bottom, Top, North, South }

public sealed record LiquidCell(
    [property: JsonRequired] Cell Cell,
    [property: JsonRequired] int Water,
    [property: JsonRequired] int Mercury,
    [property: JsonRequired] int Lava,
    int ObsidianUnits = 0,
    CrustFace? CrustFace = null);

public sealed record ObsidianCell(
    [property: JsonRequired] Cell Cell,
    [property: JsonRequired] int Units,
    [property: JsonRequired] CrustFace Face,
    [property: JsonRequired] float Hp,
    int OffsetUnits = 0,
    int OppositeUnits = 0,
    float OppositeHp = 0)
{
    // Units and Hp remain the cell totals for mining, capacity, and older saves.
    // The two leaves retain their actual bounds and durability independently.
    [JsonIgnore] public int PrimaryUnits => Units - OppositeUnits;
    [JsonIgnore] public IEnumerable<ObsidianCell> Parts
    {
        get
        {
            if (OppositeUnits == 0) { yield return this; yield break; }
            yield return this with { Units = PrimaryUnits, Hp = Hp - OppositeHp, OppositeUnits = 0, OppositeHp = 0 };
            yield return new ObsidianCell(Cell, OppositeUnits, OppositeFace(Face), OppositeHp);
        }
    }
    [JsonIgnore] public Vector3 Min => new(Cell.X + (Face == CrustFace.East ? 1 - PrimaryUnits / 10f : 0),
        Cell.Y + (Face == CrustFace.Top ? 1 - (PrimaryUnits + OffsetUnits) / 10f : Face == CrustFace.Bottom ? OffsetUnits / 10f : 0),
        Cell.Z + (Face == CrustFace.South ? 1 - PrimaryUnits / 10f : 0));
    [JsonIgnore] public Vector3 Max => new(Cell.X + (Face == CrustFace.West ? PrimaryUnits / 10f : 1),
        Cell.Y + (Face == CrustFace.Bottom ? (PrimaryUnits + OffsetUnits) / 10f : Face == CrustFace.Top ? 1 - OffsetUnits / 10f : 1),
        Cell.Z + (Face == CrustFace.North ? PrimaryUnits / 10f : 1));

    public static CrustFace OppositeFace(CrustFace face) => face switch
    {
        CrustFace.West => CrustFace.East, CrustFace.East => CrustFace.West,
        CrustFace.Bottom => CrustFace.Top, CrustFace.Top => CrustFace.Bottom,
        CrustFace.North => CrustFace.South, CrustFace.South => CrustFace.North,
        _ => throw new ArgumentOutOfRangeException(nameof(face))
    };

    internal bool HasValidGeometryAndDurability() => WorldGrid.IsInside(Cell) && Enum.IsDefined(Face) &&
        Units is >= 1 and <= LiquidSystem.UnitsPerBlock &&
        float.IsFinite(Hp) && Hp > 0 && Hp <= Units * LiquidSystem.ObsidianHpPerUnit &&
        OffsetUnits >= 0 && OffsetUnits <= LiquidSystem.UnitsPerBlock - Units &&
        (OffsetUnits == 0 || Face is CrustFace.Bottom or CrustFace.Top) &&
        OppositeUnits >= 0 && float.IsFinite(OppositeHp) &&
        (OppositeUnits == 0 ? OppositeHp == 0 :
            OppositeUnits < Units && OffsetUnits == 0 &&
            Face is CrustFace.West or CrustFace.East or CrustFace.North or CrustFace.South &&
            OppositeHp > 0 && OppositeHp <= OppositeUnits * LiquidSystem.ObsidianHpPerUnit &&
            Hp > OppositeHp && Hp - OppositeHp <= PrimaryUnits * LiquidSystem.ObsidianHpPerUnit);
}

public sealed record VaporCloud(
    [property: JsonRequired] Cell Center,
    [property: JsonRequired] int Radius,
    [property: JsonRequired] DamageKind Kind,
    [property: JsonRequired] float RemainingSeconds,
    [property: JsonRequired] float Dps,
    [property: JsonRequired] int VaporizedUnits);

public readonly record struct LiquidVolume(LiquidKind Kind, Vector3 Min, Vector3 Max);

public sealed class LiquidState
{
    [JsonRequired] public List<LiquidCell> Cells { get; set; } = [];
    [JsonRequired] public List<ObsidianCell> Obsidian { get; set; } = [];
    [JsonRequired] public List<VaporCloud> Hazards { get; set; } = [];
    [JsonRequired] public double Accumulator { get; set; }
    [JsonRequired] public int FlowTick { get; set; }
    public long EscapedWaterUnits { get; set; }
    public long EscapedMercuryUnits { get; set; }
    public long EscapedLavaUnits { get; set; }
    public long SuppliedWaterUnits { get; set; }
}

/// <summary>
/// An integer-volume simulation with open battlefield boundaries. A unit is one tenth of a
/// block; reactions and escaped parcels retain explicit accounting. A crust seals its face,
/// while its hot reaction front can keep consuming reactants on the two sides.
/// </summary>
public sealed class LiquidSystem
{
    public const int UnitsPerBlock = 10;
    public const int TurretCapacityUnits = 5;
    public const int WaterCost = 100, MercuryCost = 800, LavaCost = 1000;
    public const float ObsidianHpPerUnit = 5;
    public const float StepSeconds = .1f;
    private const double StepDuration = .1d;
    private static readonly LiquidKind[] DensityOrder = [LiquidKind.Mercury, LiquidKind.Lava, LiquidKind.Water];
    private static readonly LiquidKind[] SurfaceOrder = [LiquidKind.Water, LiquidKind.Lava, LiquidKind.Mercury];
    private static readonly Cell[] LateralDirections = [new(1, 0, 0), new(1, 0, 1), new(0, 0, 1), new(-1, 0, 1),
        new(-1, 0, 0), new(-1, 0, -1), new(0, 0, -1), new(1, 0, -1)];
    private static readonly Cell[] DisplacementDirections = [new(0, 1, 0), new(1, 0, 0), new(0, 0, 1), new(-1, 0, 0), new(0, 0, -1), new(0, -1, 0)];
    private static readonly Cell[] PositiveDirections = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
    private readonly WorldGrid _world;
    private readonly Dictionary<Cell, int[]> _cells = [];
    private readonly List<VaporCloud> _hazards = [];
    private double _accumulator;
    private int _flowTick;
    private readonly long[] _escapedUnits = new long[3];
    public long SuppliedWaterUnits { get; private set; }
    /// <summary>Changes only when liquid amounts or positions change, not when a flow timer ticks.</summary>
    public long Revision { get; private set; }
    /// <summary>Turrets reserve half a cell; the remaining capacity is shared by liquid and crust.</summary>
    public Func<Cell, bool>? IsTurretCell { get; set; }

    public LiquidSystem(WorldGrid world) => _world = world ?? throw new ArgumentNullException(nameof(world));
    public IEnumerable<LiquidCell> Cells => _cells.Keys.Union(_world.ObsidianCells.Select(s => s.Cell))
        .OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).Select(Snapshot).ToArray();
    public IEnumerable<VaporCloud> Hazards => _hazards.ToArray();
    // Internal readers use these only while the simulation cannot mutate the
    // liquid system. Public enumeration keeps its independent snapshot contract.
    internal ReadOnlySpan<VaporCloud> ActiveHazards => CollectionsMarshal.AsSpan(_hazards);
    internal bool HasLiquidCells => _cells.Count != 0;
    public static int PurchaseCost(LiquidKind kind) => kind switch
    {
        LiquidKind.Water => WaterCost, LiquidKind.Mercury => MercuryCost, LiquidKind.Lava => LavaCost,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public int GetUnits(Cell cell, LiquidKind kind) => Enum.IsDefined(kind) && _cells.TryGetValue(cell, out var amounts) ? amounts[(int)kind] : 0;
    /// <summary>Liquid volume only; solid crust units are available from WorldGrid.GetObsidian.</summary>
    public int GetTotalUnits(Cell cell) => _cells.TryGetValue(cell, out var amounts) ? amounts[0] + amounts[1] + amounts[2] : 0;
    public long GetEscapedUnits(LiquidKind kind) => Enum.IsDefined(kind) ? _escapedUnits[(int)kind] : 0;

    internal void ReleaseOutside(LiquidKind kind, int units)
    {
        if (!Enum.IsDefined(kind) || units <= 0) throw new ArgumentOutOfRangeException(nameof(units));
        _escapedUnits[(int)kind] = checked(_escapedUnits[(int)kind] + units);
        Revision++;
    }

    internal void RecordSuppliedWater(int units)
    {
        if (units < 0) throw new ArgumentOutOfRangeException(nameof(units));
        SuppliedWaterUnits = checked(SuppliedWaterUnits + units);
    }

    /// <summary>A falling solid displaces liquid into its vacated cell without deleting any units.</summary>
    internal void SwapDisplacedLiquid(Cell from, Cell to)
    {
        _cells.Remove(from, out var fromLiquid);
        _cells.Remove(to, out var toLiquid);
        if (fromLiquid is not null) _cells[to] = fromLiquid;
        if (toLiquid is not null) _cells[from] = toLiquid;
        if (fromLiquid is not null || toLiquid is not null) Revision++;
    }

    /// <summary>
    /// Side crusts can share a cell without sharing occupied volume. Keep its liquid
    /// in the opening, moving only the overflow into the space the falling leaf vacated.
    /// Called after the world has moved the leaf; each liquid kind is conserved.
    /// </summary>
    internal void DisplaceForObsidianMove(Cell from, Cell to)
    {
        var source = _cells.TryGetValue(from, out var fromAmounts) ? (int[])fromAmounts.Clone() : new int[3];
        var target = _cells.TryGetValue(to, out var toAmounts) ? (int[])toAmounts.Clone() : new int[3];
        bool changed = false;
        foreach (var kind in SurfaceOrder)
        {
            var excess = Math.Max(0, -FreeInRegion(to, kind, target));
            var move = Math.Min(target[(int)kind], excess);
            if (move == 0) continue;
            if (move > FreeInRegion(from, kind, source))
                throw new InvalidOperationException("Falling obsidian has insufficient vacated space for displaced liquid.");
            target[(int)kind] -= move;
            source[(int)kind] += move;
            changed = true;
        }
        if (!changed) return;
        if (source.Any(amount => amount > 0)) _cells[from] = source;
        else _cells.Remove(from);
        if (target.Any(amount => amount > 0)) _cells[to] = target;
        else _cells.Remove(to);
        Revision++;

        int FreeInRegion(Cell cell, LiquidKind kind, int[] amounts)
        {
            var (min, max) = Region(cell, kind);
            var capacity = (int)MathF.Round((max.X - min.X) * (max.Y - min.Y) * (max.Z - min.Z) * UnitsPerBlock);
            return Math.Min(capacity - DensityOrder.Where(k => SameRegion(cell, k, kind)).Sum(k => amounts[(int)k]),
                CellCapacity(cell) - (_world.GetObsidian(cell)?.Units ?? 0) - amounts.Sum());
        }
    }

    public IEnumerable<LiquidVolume> GetVolumes(Cell cell)
    {
        if (GetTotalUnits(cell) == 0) yield break;
        if (_world.GetObsidian(cell)?.Units == UnitsPerBlock) yield break;
        foreach (var kind in DensityOrder)
        {
            var units = GetUnits(cell, kind);
            if (units == 0) continue;
            var (min, max) = Region(cell, kind);
            var area = (max.X - min.X) * (max.Z - min.Z);
            var below = DensityOrder.TakeWhile(k => k != kind).Where(k => SameRegion(cell, k, kind)).Sum(k => GetUnits(cell, k));
            var bottom = min.Y + below / (10f * area);
            var top = MathF.Min(max.Y, bottom + units / (10f * area));
            yield return new LiquidVolume(kind, new Vector3(min.X, bottom, min.Z), new Vector3(max.X, top, max.Z));
        }
    }

    public LiquidKind? GetLiquidAt(Vector3 point)
    {
        if (!IsFinite(point)) return null;
        var cell = CellAt(point);
        if (GetTotalUnits(cell) == 0) return null;
        foreach (var volume in GetVolumes(cell))
            if (Contains(volume.Min, volume.Max, point)) return volume.Kind;
        return null;
    }

    public float GetSurfaceHeight(Cell cell) => GetVolumes(cell).Select(v => v.Max.Y)
        .DefaultIfEmpty(_world.GetObsidian(cell) is { Face: CrustFace.Bottom } crust ? crust.Max.Y : cell.Y).Max();
    public float GetDepth(Vector3 point) => GetLiquidAt(point) is null ? 0 : MathF.Max(0, GetSurfaceHeight(CellAt(point)) - point.Y);

    /// <summary>Checks material, available volume, and crust geometry without changing liquid.</summary>
    public bool CanAdd(Cell cell, LiquidKind kind, int units) => Enum.IsDefined(kind) && units > 0 &&
        CanHoldLiquid(cell) && units <= FreeUnits(cell, kind);

    /// <summary>All-or-none pour, including the reduced capacity of a turret cell.</summary>
    public bool TryAdd(Cell cell, LiquidKind kind, int units)
    {
        if (!CanAdd(cell, kind, units)) return false;
        Change(cell, kind, units);
        return true;
    }

    /// <summary>
    /// Plans placement pressure without changing the simulation. The limit is liquid units left
    /// in this cell; an optional final crust also constrains each retained phase to its opening.
    /// Pressure follows open faces, preferring upward space; escaping parcels remain accounted.
    /// </summary>
    public bool CanDisplaceLiquid(Cell cell, int maximumRemainingUnits = 0, ObsidianCell? remainingCrust = null) =>
        TryPlanDisplacement(cell, maximumRemainingUnits, remainingCrust, out _);

    /// <summary>Applies the complete placement displacement, or leaves every unit untouched.</summary>
    public bool TryDisplaceLiquid(Cell cell, int maximumRemainingUnits = 0, ObsidianCell? remainingCrust = null)
    {
        if (!TryPlanDisplacement(cell, maximumRemainingUnits, remainingCrust, out var plan)) return false;
        foreach (var parcel in plan)
        {
            Change(cell, parcel.Kind, -parcel.Units);
            if (parcel.Escaped) ReleaseOutside(parcel.Kind, parcel.Units);
            else Change(parcel.To, parcel.Kind, parcel.Units);
        }
        if (GetTotalUnits(cell) == 0) _cells.Remove(cell);
        return true;
    }

    private readonly record struct DisplacedParcel(Cell To, LiquidKind Kind, int Units, bool Escaped = false);

    private bool TryPlanDisplacement(Cell cell, int maximumRemainingUnits, ObsidianCell? remainingCrust,
        out List<DisplacedParcel> plan)
    {
        plan = [];
        if (!WorldGrid.IsInside(cell) || maximumRemainingUnits is < 0 or > UnitsPerBlock ||
            remainingCrust is not null && (remainingCrust.Cell != cell || !remainingCrust.HasValidGeometryAndDurability())) return false;
        var finalCrust = remainingCrust ?? _world.GetObsidian(cell);
        int[] retained = [GetUnits(cell, LiquidKind.Water), GetUnits(cell, LiquidKind.Mercury), GetUnits(cell, LiquidKind.Lava)];
        int limit = Math.Min(maximumRemainingUnits, Math.Max(0, CellCapacity(cell) - (finalCrust?.Units ?? 0)));
        var required = new int[3];
        foreach (var kind in SurfaceOrder)
        {
            var region = Region(cell, kind, finalCrust);
            int capacity = RegionCapacity(region);
            int regionUnits = DensityOrder.Where(k => Region(cell, k, finalCrust) == region).Sum(k => retained[(int)k]);
            int move = Math.Min(retained[(int)kind], Math.Max(Math.Max(0, retained.Sum() - limit), regionUnits - capacity));
            retained[(int)kind] -= move;
            required[(int)kind] = move;
        }
        var reserved = new Dictionary<Cell, int[]>();
        var escaped = new int[3];
        foreach (var kind in SurfaceOrder)
        {
            int remaining = required[(int)kind];
            if (remaining == 0) continue;
            var pending = new Queue<Cell>();
            var visited = new HashSet<Cell> { cell };
            pending.Enqueue(cell);
            while (pending.TryDequeue(out var current) && remaining > 0)
            {
                if (current != cell)
                {
                    reserved.TryGetValue(current, out var amounts);
                    int reservedInRegion = amounts is null ? 0 : DensityOrder.Where(k => SameRegion(current, k, kind)).Sum(k => amounts[(int)k]);
                    int free = Math.Min(FreeUnits(current) - (amounts?.Sum() ?? 0), FreeUnits(current, kind) - reservedInRegion);
                    int move = Math.Min(remaining, Math.Max(0, free));
                    if (move > 0)
                    {
                        if (amounts is null) reserved.Add(current, amounts = new int[3]);
                        amounts[(int)kind] += move;
                        plan.Add(new(current, kind, move));
                        remaining -= move;
                    }
                }
                if (remaining == 0) break;
                if (RegionReachesOpenBoundary(current, kind))
                {
                    escaped[(int)kind] += remaining;
                    plan.Add(new(current, kind, remaining, true));
                    remaining = 0;
                    break;
                }
                foreach (var direction in DisplacementDirections)
                {
                    var next = current + direction;
                    if (next == cell || visited.Contains(next) || !CanHoldLiquid(next) ||
                        !RegionsConnect(current, next, kind)) continue;
                    visited.Add(next);
                    pending.Enqueue(next);
                }
            }
            if (remaining != 0) return TryPlanReassignedDisplacement(cell, limit, finalCrust, out plan);
        }
        return DensityOrder.All(kind => _escapedUnits[(int)kind] <= long.MaxValue - escaped[(int)kind]);
    }

    private sealed class DisplacementEdge(int to, int reverse, int capacity)
    {
        public int To { get; } = to;
        public int Reverse { get; } = reverse;
        public int InitialCapacity { get; } = capacity;
        public int Capacity { get; set; } = capacity;
    }

    // The usual pressure plan needs only the nearest available space. If a crust
    // compartment makes that greedy assignment fail, reassign retained and outgoing
    // phases together. At most ten real source units pass through this network.
    private bool TryPlanReassignedDisplacement(Cell source, int retentionLimit, ObsidianCell? finalCrust,
        out List<DisplacedParcel> plan)
    {
        plan = [];
        var network = new List<List<DisplacementEdge>> { new(), new() };
        const int start = 0, sink = 1;
        var cellNodes = new Dictionary<Cell, int>();
        var regionNodes = new Dictionary<(Cell Cell, Vector3 Min, Vector3 Max), int>();
        var parcels = new List<(DisplacementEdge Edge, Cell Cell, LiquidKind Kind, bool Escaped)>();
        int required = GetTotalUnits(source);
        foreach (var kind in DensityOrder)
        {
            int units = GetUnits(source, kind);
            if (units == 0) continue;
            int kindNode = Node();
            Edge(start, kindNode, units);
            var retainedRegion = Region(source, kind, finalCrust);
            if (retentionLimit > 0 && RegionCapacity(retainedRegion) > 0)
                Edge(kindNode, Destination(source, retainedRegion, retentionLimit, RegionCapacity(retainedRegion)), units);
            bool escapes = false;
            var pending = new Queue<Cell>();
            var visited = new HashSet<Cell> { source };
            pending.Enqueue(source);
            while (pending.TryDequeue(out var current))
            {
                if (current != source)
                {
                    var region = Region(current, kind);
                    int regionFree = RegionCapacity(region) - DensityOrder.Where(k => SameRegion(current, k, kind)).Sum(k => GetUnits(current, k));
                    int free = FreeUnits(current);
                    if (free > 0 && regionFree > 0)
                    {
                        var edge = Edge(kindNode, Destination(current, region, free, regionFree), units);
                        parcels.Add((edge, current, kind, false));
                    }
                }
                escapes |= RegionReachesOpenBoundary(current, kind);
                foreach (var direction in DisplacementDirections)
                {
                    var next = current + direction;
                    if (visited.Contains(next) || !RegionsConnect(current, next, kind)) continue;
                    visited.Add(next);
                    pending.Enqueue(next);
                }
            }
            if (escapes)
            {
                var edge = Edge(kindNode, sink, units);
                parcels.Add((edge, default, kind, true));
            }
        }
        int sent = 0;
        while (sent < required)
        {
            int amount = Send(start, required - sent, new bool[network.Count]);
            if (amount == 0) return false;
            sent += amount;
        }
        foreach (var parcel in parcels)
        {
            int units = parcel.Edge.InitialCapacity - parcel.Edge.Capacity;
            if (units > 0) plan.Add(new(parcel.Cell, parcel.Kind, units, parcel.Escaped));
        }
        foreach (var kind in DensityOrder)
            if (_escapedUnits[(int)kind] > long.MaxValue - plan.Where(parcel => parcel.Escaped && parcel.Kind == kind).Sum(parcel => parcel.Units))
                return false;
        return true;

        int Node() { network.Add([]); return network.Count - 1; }
        DisplacementEdge Edge(int from, int to, int capacity)
        {
            var forward = new DisplacementEdge(to, network[to].Count, capacity);
            var reverse = new DisplacementEdge(from, network[from].Count, 0);
            network[from].Add(forward);
            network[to].Add(reverse);
            return forward;
        }
        int Destination(Cell cell, (Vector3 Min, Vector3 Max) region, int cellFree, int regionFree)
        {
            if (!cellNodes.TryGetValue(cell, out int cellNode))
            {
                cellNodes.Add(cell, cellNode = Node());
                Edge(cellNode, sink, cellFree);
            }
            var key = (cell, region.Min, region.Max);
            if (!regionNodes.TryGetValue(key, out int regionNode))
            {
                regionNodes.Add(key, regionNode = Node());
                Edge(regionNode, cellNode, regionFree);
            }
            return regionNode;
        }
        int Send(int node, int amount, bool[] visited)
        {
            if (node == sink) return amount;
            visited[node] = true;
            foreach (var edge in network[node])
            {
                if (edge.Capacity <= 0 || visited[edge.To]) continue;
                int transfer = Send(edge.To, Math.Min(amount, edge.Capacity), visited);
                if (transfer == 0) continue;
                edge.Capacity -= transfer;
                network[edge.To][edge.Reverse].Capacity += transfer;
                return transfer;
            }
            return 0;
        }
    }

    private bool RegionReachesOpenBoundary(Cell cell, LiquidKind kind)
    {
        var (min, max) = Region(cell, kind);
        return cell.X == 0 && min.X <= .00001f || cell.X == WorldGrid.Size - 1 && max.X >= WorldGrid.Size - .00001f ||
            cell.Z == 0 && min.Z <= .00001f || cell.Z == WorldGrid.Size - 1 && max.Z >= WorldGrid.Size - .00001f ||
            cell.Y == 0 && min.Y <= .00001f;
    }

    /// <summary>The upper exposed phase can be scooped, without reaching through a crust or roof.</summary>
    public bool TryGetSurfaceLiquid(Cell cell, out LiquidKind kind, out int availableUnits)
    {
        kind = default;
        availableUnits = 0;
        var volumes = GetVolumes(cell).OrderByDescending(volume => volume.Max.Y).ToArray();
        if (volumes.Length == 0) return false;
        var surface = volumes[0];
        var center = (surface.Min + surface.Max) * .5f;
        if (!OpenAbove(cell, center, surface.Max.Y) ||
            GetLiquidAt(new Vector3(center.X, surface.Max.Y + .0001f, center.Z)).HasValue) return false;
        kind = surface.Kind;
        availableUnits = GetUnits(cell, kind);
        return availableUnits > 0;
    }

    /// <summary>Removes only the exposed top liquid kind, conserving the scooped units in its caller's bucket.</summary>
    public int TakeSurfaceLiquid(Cell cell, int maximumUnits, out LiquidKind kind)
    {
        if (!TryGetSurfaceLiquid(cell, out kind, out var available) || maximumUnits <= 0) return 0;
        int units = Math.Min(UnitsPerBlock, Math.Min(maximumUnits, available));
        Change(cell, kind, -units);
        return units;
    }

    internal bool CanReceiveFromAbove(Cell cell, LiquidKind kind)
    {
        if (!CanAdd(cell, kind, 1)) return false;
        var (min, max) = Region(cell, kind);
        return OpenAbove(cell, (min + max) * .5f, max.Y);
    }

    private bool OpenAbove(Cell cell, Vector3 center, float fromY)
    {
        for (float y = fromY + .0001f; y <= cell.Y + 1.0001f; y += .025f)
            if (_world.IsSolidAt(new Vector3(center.X, y, center.Z))) return false;
        return !_world.IsSolidAt(new Vector3(center.X, cell.Y + 1.0001f, center.Z));
    }

    public void Update(float dt, Func<Cell, bool>? occupied = null)
    {
        if (!float.IsFinite(dt) || dt < 0) throw new ArgumentOutOfRangeException(nameof(dt));
        _accumulator += dt;
        while (_accumulator >= StepDuration)
        {
            _accumulator -= StepDuration;
            for (var i = _hazards.Count - 1; i >= 0; i--)
            {
                var cloud = _hazards[i];
                if (cloud.RemainingSeconds <= StepSeconds) _hazards.RemoveAt(i);
                else _hazards[i] = cloud with { RemainingSeconds = cloud.RemainingSeconds - StepSeconds };
            }
            React(occupied);
            Fall(occupied);
            Level(occupied);
            DrainBoundary(occupied);
            foreach (var empty in _cells.Where(pair => pair.Value.All(n => n == 0)).Select(pair => pair.Key).ToArray()) _cells.Remove(empty);
            _flowTick = _flowTick == int.MaxValue ? 0 : _flowTick + 1;
        }
    }

    public LiquidState ExportState() => new()
    {
        Cells = Cells.ToList(), Obsidian = _world.ObsidianCells.OrderBy(c => c.Cell.X).ThenBy(c => c.Cell.Y).ThenBy(c => c.Cell.Z).ToList(),
        Hazards = _hazards.ToList(), Accumulator = _accumulator, FlowTick = _flowTick,
        EscapedWaterUnits = _escapedUnits[0], EscapedMercuryUnits = _escapedUnits[1], EscapedLavaUnits = _escapedUnits[2],
        SuppliedWaterUnits = SuppliedWaterUnits
    };

    /// <summary>Validates a complete snapshot before changing either fluids or world collision.</summary>
    public void RestoreState(LiquidState state)
    {
        if (state is null || state.Cells is null || state.Obsidian is null || state.Hazards is null ||
            !double.IsFinite(state.Accumulator) || state.Accumulator < 0 || state.Accumulator >= StepDuration || state.FlowTick < 0 ||
            state.EscapedWaterUnits < 0 || state.EscapedMercuryUnits < 0 || state.EscapedLavaUnits < 0 || state.SuppliedWaterUnits < 0)
            throw new InvalidDataException("Liquid snapshot has missing collections or an invalid flow clock.");
        var solids = new Dictionary<Cell, ObsidianCell>();
        foreach (var crust in state.Obsidian)
        {
            if (crust is null || !crust.HasValidGeometryAndDurability() ||
                crust.Units > CellCapacity(crust.Cell) ||
                _world.GetMaterial(crust.Cell) is not (Material.Air or Material.Obsidian) || !solids.TryAdd(crust.Cell, crust))
                throw new InvalidDataException("Invalid or overlapping obsidian crust.");
        }
        var liquids = new Dictionary<Cell, int[]>();
        foreach (var cell in state.Cells)
        {
            if (cell is null || !WorldGrid.IsInside(cell.Cell) || cell.Water is < 0 or > UnitsPerBlock || cell.Mercury is < 0 or > UnitsPerBlock ||
                cell.Lava is < 0 or > UnitsPerBlock || _world.GetMaterial(cell.Cell) is not (Material.Air or Material.Obsidian))
                throw new InvalidDataException("Invalid liquid cell.");
            solids.TryGetValue(cell.Cell, out var crust);
            if (cell.Water + cell.Mercury + cell.Lava + (crust?.Units ?? 0) > CellCapacity(cell.Cell) ||
                cell.ObsidianUnits != (crust?.Units ?? 0) || cell.CrustFace != crust?.Face ||
                !liquids.TryAdd(cell.Cell, [cell.Water, cell.Mercury, cell.Lava]))
                throw new InvalidDataException("Duplicate or overfilled liquid cell, or mismatched crust geometry.");
            if (crust is { OffsetUnits: > 0 } &&
                (crust.Face == CrustFace.Bottom && (cell.Mercury > crust.OffsetUnits || cell.Lava + cell.Water > UnitsPerBlock - crust.Units - crust.OffsetUnits) ||
                 crust.Face == CrustFace.Top && (cell.Water > crust.OffsetUnits || cell.Mercury + cell.Lava > UnitsPerBlock - crust.Units - crust.OffsetUnits)))
                throw new InvalidDataException("Liquid exceeds one side of an internal crust.");
        }
        foreach (var cloud in state.Hazards)
            if (cloud is null || !WorldGrid.IsInside(cloud.Center) || cloud.Kind is not (DamageKind.Chemical or DamageKind.Thermal) ||
                cloud.Radius != (cloud.Kind == DamageKind.Chemical ? 2 : 1) || cloud.VaporizedUnits <= 0 ||
                !float.IsFinite(cloud.RemainingSeconds) || cloud.RemainingSeconds <= 0 || !float.IsFinite(cloud.Dps) || cloud.Dps <= 0)
                throw new InvalidDataException("Invalid vapor cloud.");
        if (state.Hazards.GroupBy(c => (c.Center, c.Kind)).Any(group => group.Count() > 1))
            throw new InvalidDataException("Duplicate vapor cloud.");
        _world.RestoreObsidian(solids.Values);
        _cells.Clear();
        foreach (var entry in liquids) if (entry.Value.Any(n => n > 0)) _cells.Add(entry.Key, entry.Value);
        _hazards.Clear();
        // Old checkpoints stored steam intensity per vaporized unit. Steam now has
        // one fixed rate per cloud; mercury fumes retain their accumulated intensity.
        _hazards.AddRange(state.Hazards.Select(cloud => cloud.Kind == DamageKind.Thermal
            ? cloud with { Dps = Balance.SteamDamagePerSecond } : cloud));
        _accumulator = state.Accumulator;
        _flowTick = state.FlowTick;
        _escapedUnits[0] = state.EscapedWaterUnits;
        _escapedUnits[1] = state.EscapedMercuryUnits;
        _escapedUnits[2] = state.EscapedLavaUnits;
        SuppliedWaterUnits = state.SuppliedWaterUnits;
        Revision++;
    }

    private void DrainBoundary(Func<Cell, bool>? occupied)
    {
        foreach (var cell in _cells.Keys.ToArray())
        {
            if (occupied?.Invoke(cell) == true) continue;
            foreach (var kind in DensityOrder)
            {
                int units = GetUnits(cell, kind);
                if (units == 0) continue;
                bool escapes = cell.X == 0 && TouchesFace(cell, kind, CrustFace.West) ||
                    cell.X == WorldGrid.Size - 1 && TouchesFace(cell, kind, CrustFace.East) ||
                    cell.Z == 0 && TouchesFace(cell, kind, CrustFace.North) ||
                    cell.Z == WorldGrid.Size - 1 && TouchesFace(cell, kind, CrustFace.South) ||
                    cell.Y == 0 && TouchesFace(cell, kind, CrustFace.Bottom);
                if (!escapes) continue;
                Change(cell, kind, -units);
                ReleaseOutside(kind, units);
            }
        }
    }

    private void React(Func<Cell, bool>? occupied)
    {
        var cells = _cells.Keys.OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).ToArray();
        foreach (var cell in cells)
        {
            if (occupied?.Invoke(cell) == true) continue;
            // Internal crust consumes the lava directly at its phase boundary: upward from
            // mercury below it, or downward from water above it. It never jumps to the floor.
            var existing = _world.GetObsidian(cell);
            if (existing is null || existing is { Face: CrustFace.Bottom, OffsetUnits: > 0 })
                ReactPair(cell, cell, CrustFace.Bottom, LiquidKind.Mercury, existing?.OffsetUnits ?? GetUnits(cell, LiquidKind.Mercury));
            existing = _world.GetObsidian(cell);
            if (existing is null || existing is { Face: CrustFace.Top, OffsetUnits: > 0 })
                ReactPair(cell, cell, CrustFace.Top, LiquidKind.Water, existing?.OffsetUnits ?? UnitsPerBlock - GetUnits(cell, LiquidKind.Mercury) - GetUnits(cell, LiquidKind.Lava));
            foreach (var direction in PositiveDirections)
            {
                var other = cell + direction;
                if (!WorldGrid.IsInside(other) || occupied?.Invoke(other) == true) continue;
                ReactPair(cell, other, FaceToward(cell, other), LiquidKind.Mercury);
                ReactPair(other, cell, FaceToward(other, cell), LiquidKind.Mercury);
                ReactPair(cell, other, FaceToward(cell, other), LiquidKind.Water);
                ReactPair(other, cell, FaceToward(other, cell), LiquidKind.Water);
            }
        }
    }

    private void ReactPair(Cell lavaCell, Cell reactantCell, CrustFace face, LiquidKind reactant, int offsetUnits = 0)
    {
        var lava = GetUnits(lavaCell, LiquidKind.Lava);
        var amount = GetUnits(reactantCell, reactant);
        var lavaPerReaction = reactant == LiquidKind.Mercury ? 2 : 1;
        if (lava < lavaPerReaction || amount == 0) return;
        var crust = _world.GetObsidian(lavaCell);
        var front = crust?.Parts.FirstOrDefault(part => part.Face == face && part.OffsetUnits == offsetUnits);
        if (!_world.CanAddObsidian(lavaCell, lavaPerReaction, face, offsetUnits)) return;
        if (lavaCell == reactantCell && front is { OffsetUnits: > 0 })
        {
            var lavaVolume = GetVolumes(lavaCell).First(v => v.Kind == LiquidKind.Lava);
            var reactantVolume = GetVolumes(reactantCell).First(v => v.Kind == reactant);
            // Heat crosses the solid reaction front, never an air gap left by receding fluid.
            if (face == CrustFace.Bottom && (lavaVolume.Min.Y > front.Max.Y + .00001f || reactantVolume.Max.Y < front.Min.Y - .00001f) ||
                face == CrustFace.Top && (lavaVolume.Max.Y < front.Min.Y - .00001f || reactantVolume.Min.Y > front.Max.Y + .00001f)) return;
        }
        else if (lavaCell != reactantCell)
        {
            // Each leaf can conduct through its own reaction front. A separate crust on the
            // reagent side still separates the materials and cannot be consumed through it.
            if (_world.GetObsidian(reactantCell) is { } other && (other.Units == UnitsPerBlock ||
                other.Parts.Any(part => part.Face == FaceToward(reactantCell, lavaCell)))) return;
            if (crust is null && !HaveFaceContact(lavaCell, LiquidKind.Lava, reactantCell, reactant)) return;
            if (crust is not null && !TouchesFace(reactantCell, reactant, FaceToward(reactantCell, lavaCell))) return;
        }
        if (!_world.AddObsidian(lavaCell, lavaPerReaction, face, offsetUnits)) return;
        Change(lavaCell, LiquidKind.Lava, -lavaPerReaction);
        Change(reactantCell, reactant, -1);
        AddVapor(reactantCell, reactant == LiquidKind.Mercury ? DamageKind.Chemical : DamageKind.Thermal, 1);
    }

    private void AddVapor(Cell center, DamageKind kind, int units)
    {
        var index = _hazards.FindIndex(c => c.Center == center && c.Kind == kind);
        var dps = kind == DamageKind.Chemical ? units * 4f : Balance.SteamDamagePerSecond;
        var duration = (kind == DamageKind.Chemical ? 8 : 4) + units;
        if (index < 0) _hazards.Add(new VaporCloud(center, kind == DamageKind.Chemical ? 2 : 1, kind, duration, dps, units));
        else
        {
            var old = _hazards[index];
            _hazards[index] = old with { Dps = kind == DamageKind.Chemical ? old.Dps + dps : Balance.SteamDamagePerSecond,
                RemainingSeconds = MathF.Max(old.RemainingSeconds, duration) + units,
                VaporizedUnits = checked(old.VaporizedUnits + units) };
        }
    }

    private void Fall(Func<Cell, bool>? occupied)
    {
        // Bottom first limits a parcel to one downward cell per tick.
        foreach (var upper in _cells.Keys.OrderBy(c => c.Y).ThenBy(c => c.X).ThenBy(c => c.Z).ToArray())
        {
            var lower = upper + new Cell(0, -1, 0);
            if (!CanTransfer(upper, lower, occupied)) continue;
            foreach (var kind in DensityOrder)
            {
                if (!CanTransferKind(upper, lower, kind, occupied)) continue;
                var transfer = Math.Min(GetUnits(upper, kind), FreeUnits(lower, kind));
                if (transfer > 0) Move(upper, lower, kind, transfer);
            }
            // Stable immiscible density sorting also works when both cells are full.
            for (var heavy = 0; heavy < DensityOrder.Length; heavy++)
                for (var light = DensityOrder.Length - 1; light > heavy; light--)
                {
                    var dense = DensityOrder[heavy];
                    var buoyant = DensityOrder[light];
                    var exchange = Math.Min(GetUnits(upper, dense), GetUnits(lower, buoyant));
                    if (exchange <= 0 || IsReactivePair(dense, buoyant) ||
                        !CanTransferKind(upper, lower, dense, occupied) || !CanTransferKind(lower, upper, buoyant, occupied)) continue;
                    Move(upper, lower, dense, exchange);
                    Move(lower, upper, buoyant, exchange);
                }
        }
    }

    private void Level(Func<Cell, bool>? occupied)
    {
        var cells = _cells.Keys.OrderBy(c => c.Y).ThenBy(c => c.X).ThenBy(c => c.Z).ToArray();
        var mergedCells = new HashSet<Cell>();
        foreach (var from in cells)
            if (TryMergeSingleUnit(from, occupied, out var target))
            {
                mergedCells.Add(from);
                mergedCells.Add(target);
            }
        foreach (var from in cells)
        {
            if (mergedCells.Contains(from)) continue;
            for (var direction = 0; direction < LateralDirections.Length; direction++)
            {
                var to = from + LateralDirections[(direction + _flowTick % LateralDirections.Length) % LateralDirections.Length];
                if (!CanTransfer(from, to, occupied)) continue;
                while (FreeUnits(to) > 0 && GetTotalUnits(from) > 0)
                {
                    var transferable = SurfaceOrder.Where(k => GetUnits(from, k) > 0 && FreeUnits(to, k) > 0 && CanTransferKind(from, to, k, occupied)).Select(k => (LiquidKind?)k).FirstOrDefault();
                    if (transferable is not { } kind) break;
                    var sourceSurface = RegionSurface(from, kind);
                    var targetSurface = RegionSurface(to, kind);
                    var sourceArea = OpenHorizontalArea(from);
                    var targetArea = OpenHorizontalArea(to);
                    // Equalize a whole parcel before inspecting single-unit remnants. Checking
                    // intermediate units would reject ordinary 10 -> 5/5 spreading as reversible.
                    int transfer = Math.Min(GetUnits(from, kind), Math.Min(FreeUnits(to, kind),
                        (int)MathF.Floor((sourceSurface - targetSurface + .00001f) / (.1f / sourceArea + .1f / targetArea))));
                    // Do not create a remnant that would immediately merge back into a deeper
                    // neighbor. This keeps 4 -> 2/2 stable in an L-shaped cavity while still
                    // allowing an isolated 2 -> 1/1 split with equal-depth neighbors.
                    while (transfer > 0 && (WouldMergeAfterTransfer(from, from, to, kind, transfer, occupied) ||
                        WouldMergeAfterTransfer(to, from, to, kind, transfer, occupied))) transfer--;
                    if (transfer <= 0) break;
                    Move(from, to, kind, transfer);
                }
            }
        }
    }

    private bool TryMergeSingleUnit(Cell from, Func<Cell, bool>? occupied, out Cell target)
    {
        target = default;
        if (GetTotalUnits(from) != 1) return false;
        var kind = SurfaceOrder.First(k => GetUnits(from, k) == 1);
        float sourceDepth = ColumnDepth(from, kind);
        Cell? deepest = null;
        float deepestDepth = sourceDepth;
        for (int direction = 0; direction < LateralDirections.Length; direction++)
        {
            var to = from + LateralDirections[(direction + _flowTick % LateralDirections.Length) % LateralDirections.Length];
            if (!CanTransfer(from, to, occupied) || FreeUnits(to, kind) < 1 || !CanTransferKind(from, to, kind, occupied)) continue;
            float depth = ColumnDepth(to, kind);
            if (depth <= deepestDepth + .00001f) continue;
            deepest = to;
            deepestDepth = depth;
        }
        if (deepest is not { } destination) return false;
        target = destination;
        Move(from, target, kind, 1);
        return true;
    }

    private float ColumnDepth(Cell cell, LiquidKind kind)
    {
        float depth = RegionSurface(cell, kind) - Region(cell, kind).Min.Y;
        while (Region(cell, kind).Min.Y <= cell.Y + .00001f)
        {
            var lower = cell + new Cell(0, -1, 0);
            if (!RegionsConnect(cell, lower, kind)) break;
            var (min, max) = Region(lower, kind);
            // A lower pool contributes only when it actually reaches this cell's floor.
            if (RegionSurface(lower, kind) < lower.Y + .99999f) break;
            depth += max.Y - min.Y;
            cell = lower;
        }
        return depth;
    }

    private bool WouldMergeAfterTransfer(Cell candidate, Cell from, Cell to, LiquidKind movedKind, int transfer, Func<Cell, bool>? occupied)
    {
        if (TotalAfter(candidate) != 1) return false;
        var kind = SurfaceOrder.First(k => UnitsAfter(candidate, k) == 1);
        float depth = DepthAfter(candidate, kind);
        var (min, max) = Region(candidate, kind);
        var source = new LiquidVolume(kind, min, new Vector3(max.X,
            MathF.Min(max.Y, min.Y + .1f / ((max.X - min.X) * (max.Z - min.Z))), max.Z));
        foreach (var direction in LateralDirections)
        {
            var neighbor = candidate + direction;
            if (!CanTransfer(candidate, neighbor, occupied) || !CanTransferVolume(candidate, neighbor, kind, source, occupied)) continue;
            int regionUnits = DensityOrder.Where(k => SameRegion(neighbor, k, kind)).Sum(k => UnitsAfter(neighbor, k));
            int free = Math.Min(CellCapacity(neighbor) - (_world.GetObsidian(neighbor)?.Units ?? 0) - TotalAfter(neighbor),
                RegionCapacity(Region(neighbor, kind)) - regionUnits);
            if (free > 0 && DepthAfter(neighbor, kind) > depth + .00001f) return true;
        }
        return false;

        int Delta(Cell cell) => cell == from ? -transfer : cell == to ? transfer : 0;
        int UnitsAfter(Cell cell, LiquidKind phase) => GetUnits(cell, phase) + (phase == movedKind ? Delta(cell) : 0);
        int TotalAfter(Cell cell) => GetTotalUnits(cell) + Delta(cell);
        float DepthAfter(Cell cell, LiquidKind phase) => ColumnDepth(cell, phase) +
            (SameRegion(cell, phase, movedKind) ? Delta(cell) * .1f / OpenHorizontalArea(cell) : 0);
    }

    private bool CanTransfer(Cell from, Cell to, Func<Cell, bool>? occupied)
    {
        if (!WorldGrid.IsInside(from) || !WorldGrid.IsInside(to) || occupied?.Invoke(from) == true || occupied?.Invoke(to) == true ||
            !CanHoldLiquid(from) || !CanHoldLiquid(to)) return false;
        var delta = to - from;
        if (delta.Manhattan == 1) return !_world.BlocksFace(from, to);
        if (delta.Y != 0 || Math.Abs(delta.X) != 1 || Math.Abs(delta.Z) != 1) return false;
        return DiagonalPassesThrough(from + new Cell(delta.X, 0, 0)) || DiagonalPassesThrough(from + new Cell(0, 0, delta.Z));

        bool DiagonalPassesThrough(Cell middle) => occupied?.Invoke(middle) != true && CanHoldLiquid(middle) &&
            !_world.BlocksFace(from, middle) && !_world.BlocksFace(middle, to);
    }
    private bool CanHoldLiquid(Cell cell) => WorldGrid.IsInside(cell) && _world.GetMaterial(cell) is Material.Air or Material.Obsidian &&
        (_world.GetObsidian(cell)?.Units ?? 0) < CellCapacity(cell);
    private int CellCapacity(Cell cell) => IsTurretCell?.Invoke(cell) == true ? TurretCapacityUnits : UnitsPerBlock;
    private int FreeUnits(Cell cell) => CellCapacity(cell) - GetTotalUnits(cell) - (_world.GetObsidian(cell)?.Units ?? 0);
    private int FreeUnits(Cell cell, LiquidKind kind)
    {
        return Math.Min(FreeUnits(cell), RegionCapacity(Region(cell, kind)) -
            DensityOrder.Where(k => SameRegion(cell, k, kind)).Sum(k => GetUnits(cell, k)));
    }

    private (Vector3 Min, Vector3 Max) Region(Cell cell, LiquidKind kind) => Region(cell, kind, _world.GetObsidian(cell));
    private static int RegionCapacity((Vector3 Min, Vector3 Max) region) =>
        (int)MathF.Round((region.Max.X - region.Min.X) * (region.Max.Y - region.Min.Y) * (region.Max.Z - region.Min.Z) * UnitsPerBlock);

    private static (Vector3 Min, Vector3 Max) Region(Cell cell, LiquidKind kind, ObsidianCell? crust)
    {
        var min = new Vector3(cell.X, cell.Y, cell.Z);
        var max = min + Vector3.One;
        if (crust is null) return (min, max);
        if (crust.OffsetUnits > 0)
        {
            var below = crust.Face == CrustFace.Bottom ? kind == LiquidKind.Mercury : kind != LiquidKind.Water;
            if (below) max.Y = crust.Min.Y;
            else min.Y = crust.Max.Y;
        }
        else foreach (var part in crust.Parts) switch (part.Face)
        {
            case CrustFace.West: min.X = part.Max.X; break;
            case CrustFace.East: max.X = part.Min.X; break;
            case CrustFace.Bottom: min.Y = part.Max.Y; break;
            case CrustFace.Top: max.Y = part.Min.Y; break;
            case CrustFace.North: min.Z = part.Max.Z; break;
            case CrustFace.South: max.Z = part.Min.Z; break;
        }
        return (min, max);
    }
    private bool SameRegion(Cell cell, LiquidKind a, LiquidKind b) => Region(cell, a) == Region(cell, b);
    private float RegionSurface(Cell cell, LiquidKind kind)
    {
        var (min, max) = Region(cell, kind);
        return min.Y + DensityOrder.Where(k => SameRegion(cell, k, kind)).Sum(k => GetUnits(cell, k)) /
            (UnitsPerBlock * (max.X - min.X) * (max.Z - min.Z));
    }
    private bool CanTransferKind(Cell from, Cell to, LiquidKind kind, Func<Cell, bool>? occupied = null)
    {
        if (GetUnits(from, kind) == 0) return false;
        var source = GetVolumes(from).First(v => v.Kind == kind);
        return CanTransferVolume(from, to, kind, source, occupied);
    }

    private bool CanTransferVolume(Cell from, Cell to, LiquidKind kind, LiquidVolume source, Func<Cell, bool>? occupied)
    {
        var (min, max) = Region(to, kind);
        var d = to - from;
        if (d.X != 0 && d.Z != 0)
        {
            if (!Overlaps(source.Min.Y, source.Max.Y, min.Y, max.Y)) return false;
            return Through(from + new Cell(d.X, 0, 0)) || Through(from + new Cell(0, 0, d.Z));

            bool Through(Cell middle) => occupied?.Invoke(middle) != true && RegionsConnect(from, middle, kind) &&
                CanTransferVolume(from, middle, kind, source, occupied) && RegionsConnect(middle, to, kind);
        }
        if (d.Y < 0 && (source.Min.Y > from.Y + .00001f || max.Y < to.Y + .99999f) ||
            d.Y > 0 && (source.Max.Y < from.Y + .99999f || min.Y > to.Y + .00001f)) return false;
        return d.X != 0 ? Overlaps(source.Min.Y, source.Max.Y, min.Y, max.Y) && Overlaps(source.Min.Z, source.Max.Z, min.Z, max.Z) :
            d.Y != 0 ? Overlaps(source.Min.X, source.Max.X, min.X, max.X) && Overlaps(source.Min.Z, source.Max.Z, min.Z, max.Z) :
            Overlaps(source.Min.X, source.Max.X, min.X, max.X) && Overlaps(source.Min.Y, source.Max.Y, min.Y, max.Y);
    }

    private bool RegionsConnect(Cell from, Cell to, LiquidKind kind)
    {
        if (!CanHoldLiquid(from) || !CanHoldLiquid(to) || _world.BlocksFace(from, to)) return false;
        var (sourceMin, sourceMax) = Region(from, kind);
        var (min, max) = Region(to, kind);
        var delta = to - from;
        if (delta.Y < 0 && (sourceMin.Y > from.Y + .00001f || max.Y < to.Y + .99999f) ||
            delta.Y > 0 && (sourceMax.Y < from.Y + .99999f || min.Y > to.Y + .00001f)) return false;
        return delta.X != 0 ? Overlaps(sourceMin.Y, sourceMax.Y, min.Y, max.Y) && Overlaps(sourceMin.Z, sourceMax.Z, min.Z, max.Z) :
            delta.Y != 0 ? Overlaps(sourceMin.X, sourceMax.X, min.X, max.X) && Overlaps(sourceMin.Z, sourceMax.Z, min.Z, max.Z) :
            Overlaps(sourceMin.X, sourceMax.X, min.X, max.X) && Overlaps(sourceMin.Y, sourceMax.Y, min.Y, max.Y);
    }
    private float OpenHorizontalArea(Cell cell)
    {
        var crust = _world.GetObsidian(cell);
        return crust is not null && crust.Face is CrustFace.West or CrustFace.East or CrustFace.North or CrustFace.South ? 1 - crust.Units / 10f : 1;
    }
    private void Change(Cell cell, LiquidKind kind, int change)
    {
        if (change == 0) return;
        Revision++;
        if (!_cells.TryGetValue(cell, out var amounts)) _cells.Add(cell, amounts = new int[3]);
        amounts[(int)kind] += change;
    }
    private void Move(Cell from, Cell to, LiquidKind kind, int amount) { Change(from, kind, -amount); Change(to, kind, amount); }
    private LiquidCell Snapshot(Cell cell)
    {
        var crust = _world.GetObsidian(cell);
        return new LiquidCell(cell, GetUnits(cell, LiquidKind.Water), GetUnits(cell, LiquidKind.Mercury), GetUnits(cell, LiquidKind.Lava), crust?.Units ?? 0, crust?.Face);
    }
    private bool TouchesFace(Cell cell, LiquidKind kind, CrustFace face) => GetVolumes(cell).Any(v => v.Kind == kind && (face switch
    {
        CrustFace.West => v.Min.X <= cell.X + .00001f, CrustFace.East => v.Max.X >= cell.X + .99999f,
        CrustFace.Bottom => v.Min.Y <= cell.Y + .00001f, CrustFace.Top => v.Max.Y >= cell.Y + .99999f,
        CrustFace.North => v.Min.Z <= cell.Z + .00001f, CrustFace.South => v.Max.Z >= cell.Z + .99999f,
        _ => false
    }));
    private bool HaveFaceContact(Cell a, LiquidKind ak, Cell b, LiquidKind bk)
    {
        if (!TouchesFace(a, ak, FaceToward(a, b)) || !TouchesFace(b, bk, FaceToward(b, a))) return false;
        var first = GetVolumes(a).First(v => v.Kind == ak);
        var second = GetVolumes(b).First(v => v.Kind == bk);
        var delta = b - a;
        return delta.X != 0 ? Overlaps(first.Min.Y, first.Max.Y, second.Min.Y, second.Max.Y) && Overlaps(first.Min.Z, first.Max.Z, second.Min.Z, second.Max.Z) :
            delta.Y != 0 ? Overlaps(first.Min.X, first.Max.X, second.Min.X, second.Max.X) && Overlaps(first.Min.Z, first.Max.Z, second.Min.Z, second.Max.Z) :
            Overlaps(first.Min.X, first.Max.X, second.Min.X, second.Max.X) && Overlaps(first.Min.Y, first.Max.Y, second.Min.Y, second.Max.Y);
    }
    private static bool Overlaps(float minA, float maxA, float minB, float maxB) => MathF.Min(maxA, maxB) - MathF.Max(minA, minB) > .00001f;
    private static bool IsReactivePair(LiquidKind a, LiquidKind b) => a == LiquidKind.Lava && b != LiquidKind.Lava || b == LiquidKind.Lava && a != LiquidKind.Lava;
    private static bool IsFinite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static Cell CellAt(Vector3 p) => new((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));
    private static bool Contains(Vector3 min, Vector3 max, Vector3 p) => p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X < max.X && p.Y < max.Y && p.Z < max.Z;
    public static CrustFace FaceToward(Cell from, Cell to)
    {
        var d = to - from;
        if (d.Manhattan != 1) throw new ArgumentException("Crust contact must be between neighboring cells.");
        return d.X < 0 ? CrustFace.West : d.X > 0 ? CrustFace.East : d.Y < 0 ? CrustFace.Bottom : d.Y > 0 ? CrustFace.Top : d.Z < 0 ? CrustFace.North : CrustFace.South;
    }
}
