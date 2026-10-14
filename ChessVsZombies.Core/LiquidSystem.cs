using System.Numerics;
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
    int OffsetUnits = 0)
{
    [JsonIgnore] public Vector3 Min => new(Cell.X + (Face == CrustFace.East ? 1 - Units / 10f : 0),
        Cell.Y + (Face == CrustFace.Top ? 1 - (Units + OffsetUnits) / 10f : Face == CrustFace.Bottom ? OffsetUnits / 10f : 0),
        Cell.Z + (Face == CrustFace.South ? 1 - Units / 10f : 0));
    [JsonIgnore] public Vector3 Max => new(Cell.X + (Face == CrustFace.West ? Units / 10f : 1),
        Cell.Y + (Face == CrustFace.Bottom ? (Units + OffsetUnits) / 10f : Face == CrustFace.Top ? 1 - OffsetUnits / 10f : 1),
        Cell.Z + (Face == CrustFace.North ? Units / 10f : 1));
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
}

/// <summary>
/// A closed, integer-volume simulation. A unit is one tenth of a block; only reactions remove
/// liquid, accounting for it as solid lava or vapor. A crust seals fluid flow through its face,
/// while its hot reaction front can keep consuming reactants on the two sides.
/// </summary>
public sealed class LiquidSystem
{
    public const int UnitsPerBlock = 10;
    public const int WaterCost = 100, MercuryCost = 800, LavaCost = 1000;
    public const float ObsidianHpPerUnit = 50;
    public const float StepSeconds = .1f;
    private const double StepDuration = .1d;
    private static readonly LiquidKind[] DensityOrder = [LiquidKind.Mercury, LiquidKind.Lava, LiquidKind.Water];
    private static readonly LiquidKind[] SurfaceOrder = [LiquidKind.Water, LiquidKind.Lava, LiquidKind.Mercury];
    private static readonly Cell[] LateralDirections = [new(1, 0, 0), new(0, 0, 1), new(-1, 0, 0), new(0, 0, -1)];
    private static readonly Cell[] PositiveDirections = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
    private readonly WorldGrid _world;
    private readonly Dictionary<Cell, int[]> _cells = [];
    private readonly List<VaporCloud> _hazards = [];
    private double _accumulator;
    private int _flowTick;

    public LiquidSystem(WorldGrid world) => _world = world ?? throw new ArgumentNullException(nameof(world));
    public IEnumerable<LiquidCell> Cells => _cells.Keys.Union(_world.ObsidianCells.Select(s => s.Cell))
        .OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).Select(Snapshot).ToArray();
    public IEnumerable<VaporCloud> Hazards => _hazards.ToArray();
    public static int PurchaseCost(LiquidKind kind) => kind switch
    {
        LiquidKind.Water => WaterCost, LiquidKind.Mercury => MercuryCost, LiquidKind.Lava => LavaCost,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public int GetUnits(Cell cell, LiquidKind kind) => Enum.IsDefined(kind) && _cells.TryGetValue(cell, out var amounts) ? amounts[(int)kind] : 0;
    /// <summary>Liquid volume only; solid crust units are available from WorldGrid.GetObsidian.</summary>
    public int GetTotalUnits(Cell cell) => _cells.TryGetValue(cell, out var amounts) ? amounts[0] + amounts[1] + amounts[2] : 0;

    /// <summary>A falling solid displaces liquid into its vacated cell without deleting any units.</summary>
    internal void SwapDisplacedLiquid(Cell from, Cell to)
    {
        _cells.Remove(from, out var fromLiquid);
        _cells.Remove(to, out var toLiquid);
        if (fromLiquid is not null) _cells[to] = fromLiquid;
        if (toLiquid is not null) _cells[from] = toLiquid;
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
        foreach (var volume in GetVolumes(cell))
            if (Contains(volume.Min, volume.Max, point)) return volume.Kind;
        return null;
    }

    public float GetSurfaceHeight(Cell cell) => GetVolumes(cell).Select(v => v.Max.Y)
        .DefaultIfEmpty(_world.GetObsidian(cell) is { Face: CrustFace.Bottom } crust ? crust.Max.Y : cell.Y).Max();
    public float GetDepth(Vector3 point) => GetLiquidAt(point) is null ? 0 : MathF.Max(0, GetSurfaceHeight(CellAt(point)) - point.Y);

    /// <summary>All-or-none pour. GameSession additionally rejects cells occupied by turrets.</summary>
    public bool TryAdd(Cell cell, LiquidKind kind, int units)
    {
        if (!Enum.IsDefined(kind) || units <= 0 || !CanHoldLiquid(cell) || units > FreeUnits(cell, kind)) return false;
        Change(cell, kind, units);
        return true;
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
            foreach (var empty in _cells.Where(pair => pair.Value.All(n => n == 0)).Select(pair => pair.Key).ToArray()) _cells.Remove(empty);
            _flowTick = _flowTick == int.MaxValue ? 0 : _flowTick + 1;
        }
    }

    public LiquidState ExportState() => new()
    {
        Cells = Cells.ToList(), Obsidian = _world.ObsidianCells.OrderBy(c => c.Cell.X).ThenBy(c => c.Cell.Y).ThenBy(c => c.Cell.Z).ToList(),
        Hazards = _hazards.ToList(), Accumulator = _accumulator, FlowTick = _flowTick
    };

    /// <summary>Validates a complete snapshot before changing either fluids or world collision.</summary>
    public void RestoreState(LiquidState state)
    {
        if (state is null || state.Cells is null || state.Obsidian is null || state.Hazards is null ||
            !double.IsFinite(state.Accumulator) || state.Accumulator < 0 || state.Accumulator >= StepDuration || state.FlowTick < 0)
            throw new InvalidDataException("Liquid snapshot has missing collections or an invalid flow clock.");
        var solids = new Dictionary<Cell, ObsidianCell>();
        foreach (var crust in state.Obsidian)
        {
            if (crust is null || !WorldGrid.IsInside(crust.Cell) || !Enum.IsDefined(crust.Face) || crust.Units is < 1 or > UnitsPerBlock ||
                !float.IsFinite(crust.Hp) || crust.Hp <= 0 || crust.Hp > crust.Units * ObsidianHpPerUnit ||
                crust.OffsetUnits < 0 || crust.OffsetUnits > UnitsPerBlock - crust.Units ||
                crust.OffsetUnits != 0 && crust.Face is not (CrustFace.Bottom or CrustFace.Top) ||
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
            if (cell.Water + cell.Mercury + cell.Lava + (crust?.Units ?? 0) > UnitsPerBlock ||
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
        _hazards.AddRange(state.Hazards);
        _accumulator = state.Accumulator;
        _flowTick = state.FlowTick;
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
        if (crust is not null && (crust.Face != face || crust.OffsetUnits != offsetUnits) ||
            (crust?.Units ?? 0) + lavaPerReaction + offsetUnits > UnitsPerBlock) return;
        if (lavaCell == reactantCell && crust is { OffsetUnits: > 0 })
        {
            var lavaVolume = GetVolumes(lavaCell).First(v => v.Kind == LiquidKind.Lava);
            var reactantVolume = GetVolumes(reactantCell).First(v => v.Kind == reactant);
            // Heat crosses the solid reaction front, never an air gap left by receding fluid.
            if (face == CrustFace.Bottom && (lavaVolume.Min.Y > crust.Max.Y + .00001f || reactantVolume.Max.Y < crust.Min.Y - .00001f) ||
                face == CrustFace.Top && (lavaVolume.Max.Y < crust.Min.Y - .00001f || reactantVolume.Min.Y > crust.Max.Y + .00001f)) return;
        }
        else if (lavaCell != reactantCell)
        {
            // Only this crust's reactive face conducts heat. A second crust on the reagent side
            // still separates the materials and cannot be consumed through another solid wall.
            if (_world.GetObsidian(reactantCell) is { } other && (other.Units == UnitsPerBlock || other.Face == FaceToward(reactantCell, lavaCell))) return;
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
        var dps = units * (kind == DamageKind.Chemical ? 4f : 2f);
        var duration = (kind == DamageKind.Chemical ? 8 : 4) + units;
        if (index < 0) _hazards.Add(new VaporCloud(center, kind == DamageKind.Chemical ? 2 : 1, kind, duration, dps, units));
        else
        {
            var old = _hazards[index];
            _hazards[index] = old with { Dps = old.Dps + dps, RemainingSeconds = MathF.Max(old.RemainingSeconds, duration) + units,
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
                if (!CanTransferKind(upper, lower, kind)) continue;
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
                        !CanTransferKind(upper, lower, dense) || !CanTransferKind(lower, upper, buoyant)) continue;
                    Move(upper, lower, dense, exchange);
                    Move(lower, upper, buoyant, exchange);
                }
        }
    }

    private void Level(Func<Cell, bool>? occupied)
    {
        var cells = _cells.Keys.OrderBy(c => c.Y).ThenBy(c => c.X).ThenBy(c => c.Z).ToArray();
        foreach (var from in cells)
            for (var direction = 0; direction < LateralDirections.Length; direction++)
            {
                var to = from + LateralDirections[(direction + _flowTick % LateralDirections.Length) % LateralDirections.Length];
                if (!CanTransfer(from, to, occupied)) continue;
                for (var i = 0; i < UnitsPerBlock && FreeUnits(to) > 0 && GetTotalUnits(from) > 0; i++)
                {
                    var transferable = SurfaceOrder.Where(k => GetUnits(from, k) > 0 && FreeUnits(to, k) > 0 && CanTransferKind(from, to, k)).Select(k => (LiquidKind?)k).FirstOrDefault();
                    if (transferable is not { } kind) break;
                    var sourceSurface = RegionSurface(from, kind);
                    var targetSurface = RegionSurface(to, kind);
                    var sourceArea = OpenHorizontalArea(from);
                    var targetArea = OpenHorizontalArea(to);
                    // Do not move a whole discrete unit when doing so would reverse the height difference.
                    if (sourceSurface - targetSurface + .00001f < .1f / sourceArea + .1f / targetArea) break;
                    Move(from, to, kind, 1);
                }
            }
    }

    private bool CanTransfer(Cell from, Cell to, Func<Cell, bool>? occupied) => WorldGrid.IsInside(from) && WorldGrid.IsInside(to) &&
        occupied?.Invoke(from) != true && occupied?.Invoke(to) != true && CanHoldLiquid(from) && CanHoldLiquid(to) && !_world.BlocksFace(from, to);
    private bool CanHoldLiquid(Cell cell) => WorldGrid.IsInside(cell) && _world.GetMaterial(cell) is Material.Air or Material.Obsidian &&
        (_world.GetObsidian(cell)?.Units ?? 0) < UnitsPerBlock;
    private int FreeUnits(Cell cell) => UnitsPerBlock - GetTotalUnits(cell) - (_world.GetObsidian(cell)?.Units ?? 0);
    private int FreeUnits(Cell cell, LiquidKind kind)
    {
        var (min, max) = Region(cell, kind);
        var capacity = (int)MathF.Round((max.X - min.X) * (max.Y - min.Y) * (max.Z - min.Z) * UnitsPerBlock);
        return capacity - DensityOrder.Where(k => SameRegion(cell, k, kind)).Sum(k => GetUnits(cell, k));
    }

    private (Vector3 Min, Vector3 Max) Region(Cell cell, LiquidKind kind)
    {
        var min = new Vector3(cell.X, cell.Y, cell.Z);
        var max = min + Vector3.One;
        if (_world.GetObsidian(cell) is not { } crust) return (min, max);
        if (crust.OffsetUnits > 0)
        {
            var below = crust.Face == CrustFace.Bottom ? kind == LiquidKind.Mercury : kind != LiquidKind.Water;
            if (below) max.Y = crust.Min.Y;
            else min.Y = crust.Max.Y;
        }
        else switch (crust.Face)
        {
            case CrustFace.West: min.X = crust.Max.X; break;
            case CrustFace.East: max.X = crust.Min.X; break;
            case CrustFace.Bottom: min.Y = crust.Max.Y; break;
            case CrustFace.Top: max.Y = crust.Min.Y; break;
            case CrustFace.North: min.Z = crust.Max.Z; break;
            case CrustFace.South: max.Z = crust.Min.Z; break;
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
    private bool CanTransferKind(Cell from, Cell to, LiquidKind kind)
    {
        if (GetUnits(from, kind) == 0) return false;
        var source = GetVolumes(from).First(v => v.Kind == kind);
        var (min, max) = Region(to, kind);
        var d = to - from;
        if (d.Y < 0 && (source.Min.Y > from.Y + .00001f || max.Y < to.Y + .99999f) ||
            d.Y > 0 && (source.Max.Y < from.Y + .99999f || min.Y > to.Y + .00001f)) return false;
        return d.X != 0 ? Overlaps(source.Min.Y, source.Max.Y, min.Y, max.Y) && Overlaps(source.Min.Z, source.Max.Z, min.Z, max.Z) :
            d.Y != 0 ? Overlaps(source.Min.X, source.Max.X, min.X, max.X) && Overlaps(source.Min.Z, source.Max.Z, min.Z, max.Z) :
            Overlaps(source.Min.X, source.Max.X, min.X, max.X) && Overlaps(source.Min.Y, source.Max.Y, min.Y, max.Y);
    }
    private float OpenHorizontalArea(Cell cell)
    {
        var crust = _world.GetObsidian(cell);
        return crust is not null && crust.Face is CrustFace.West or CrustFace.East or CrustFace.North or CrustFace.South ? 1 - crust.Units / 10f : 1;
    }
    private void Change(Cell cell, LiquidKind kind, int change)
    {
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
