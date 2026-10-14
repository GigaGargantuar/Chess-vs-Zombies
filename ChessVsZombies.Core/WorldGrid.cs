using System.Numerics;

namespace ChessVsZombies.Core;

/// <summary>The immutable objective, editable terrain, and defensive blocks in the 32-cube battlefield.</summary>
public sealed class WorldGrid
{
    public const int Size = 32;
    private static readonly Cell[] Orthogonal = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
    private readonly Material[,,] _terrain = new Material[Size, Size, Size];
    private readonly Dictionary<Cell, Barrier> _barriers = [];
    private readonly Dictionary<Cell, float> _terrainHp = [];
    private readonly Dictionary<Cell, ObsidianCell> _obsidian = [];
    public int Revision { get; private set; }
    public IEnumerable<Barrier> Barriers => _barriers.Values;
    public IEnumerable<KeyValuePair<Cell, float>> DamagedTerrain => _terrainHp.Select(pair => pair);
    public IEnumerable<ObsidianCell> ObsidianCells => _obsidian.Values.ToArray();

    public WorldGrid()
    {
        for (var x = 0; x < Size; x++)
            for (var y = 0; y < Size; y++)
                for (var z = 0; z < Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    _terrain[x, y, z] = InitialMaterial(cell);
                }
    }

    public static bool IsInside(Cell cell) => cell.X is >= 0 and < Size && cell.Y is >= 0 and < Size && cell.Z is >= 0 and < Size;
    public static bool IsCore(Cell cell) => cell.X is >= 15 and <= 16 && cell.Y is >= 15 and <= 16 && cell.Z is >= 15 and <= 16;
    public static bool IsBuildZone(Cell cell) => cell.X is >= 12 and <= 19 && cell.Y is >= 12 and <= 19 && cell.Z is >= 12 and <= 19;
    public static bool IsTurretZone(Cell cell) => cell.X is >= 1 and <= 30 && cell.Y is >= 1 and <= 30 && cell.Z is >= 1 and <= 30 && !IsBuildZone(cell);
    public static bool IsFortificationZone(Cell cell) => cell.X is >= 1 and <= 30 && cell.Y is >= 1 and <= 30 && cell.Z is >= 1 and <= 30 && !IsCore(cell);
    public static bool IsSpawnZone(Cell cell) => IsInside(cell) &&
        (cell.X is 0 or 31 || cell.Y is 0 or 31 || cell.Z is 0 or 31);
    public Material GetMaterial(Cell cell) => !IsInside(cell) ? Material.Air :
        _barriers.ContainsKey(cell) ? Material.Barrier : _obsidian.ContainsKey(cell) ? Material.Obsidian : _terrain[cell.X, cell.Y, cell.Z];
    public static Material InitialMaterial(Cell cell) => !IsInside(cell) ? Material.Air : IsCore(cell) ? Material.Core :
        cell.Y < 12 ? Material.Stone : cell.Y < 16 ? Material.Dirt : Material.Air;
    public Material GetNaturalMaterial(Cell cell) => IsInside(cell) ? _terrain[cell.X, cell.Y, cell.Z] : Material.Air;
    public bool HasSupport(Cell cell) => cell.Y > 0 && IsInside(cell) && IsSolidAt(cell.Center - new Vector3(0, .501f, 0));
    /// <summary>Block attachment follows the six neighboring grid cells, including partial obsidian cells.</summary>
    public bool HasOrthogonalBlockSupport(Cell cell) => IsInside(cell) &&
        Orthogonal.Any(direction => GetMaterial(cell + direction) != Material.Air);

    internal IEnumerable<Cell> MovableBlocks()
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    var material = GetMaterial(cell);
                    if (Balance.IsTerrainMaterial(material) || material is Material.Barrier or Material.Obsidian) yield return cell;
                }
    }

    internal bool RestoreTerrainPlacement(Cell cell, Material material)
    {
        if (!IsInside(cell) || !Balance.IsTerrainMaterial(material) || GetMaterial(cell) != Material.Air) return false;
        _terrain[cell.X, cell.Y, cell.Z] = material;
        Revision++;
        return true;
    }

    /// <summary>Moves an intact block without changing its material, provenance, or remaining HP.</summary>
    internal bool MoveBlock(Cell from, Cell to)
    {
        if (!IsInside(to) || GetMaterial(to) != Material.Air) return false;
        if (GetBarrier(from) is { } barrier)
        {
            _barriers.Remove(from);
            _barriers.Add(to, new Barrier { Cell = to, Content = barrier.Content, Hp = barrier.Hp });
        }
        else if (GetObsidian(from) is { } crust)
        {
            _obsidian.Remove(from);
            _obsidian.Add(to, crust with { Cell = to });
        }
        else if (Balance.IsTerrainMaterial(GetNaturalMaterial(from)))
        {
            _terrain[to.X, to.Y, to.Z] = _terrain[from.X, from.Y, from.Z];
            _terrain[from.X, from.Y, from.Z] = Material.Air;
            if (_terrainHp.Remove(from, out var hp)) _terrainHp[to] = hp;
        }
        else return false;
        Revision++;
        return true;
    }
    public Barrier? GetBarrier(Cell cell) => _barriers.GetValueOrDefault(cell);
    public ObsidianCell? GetObsidian(Cell cell) => _obsidian.GetValueOrDefault(cell);

    internal bool CanAddObsidian(Cell cell, int units, CrustFace face, int offsetUnits = 0)
    {
        if (!IsInside(cell) || !Enum.IsDefined(face) || units <= 0 || offsetUnits < 0 ||
            offsetUnits > LiquidSystem.UnitsPerBlock - units ||
            offsetUnits != 0 && face is not (CrustFace.Bottom or CrustFace.Top)) return false;
        var existing = GetObsidian(cell);
        if (existing == null) return GetMaterial(cell) == Material.Air;
        var part = existing.Parts.FirstOrDefault(part => part.Face == face);
        return part != null && part.OffsetUnits == offsetUnits &&
            existing.Units + units + offsetUnits <= LiquidSystem.UnitsPerBlock;
    }

    /// <summary>Moves one slab without rotating it or filling the gap between opposite slabs.</summary>
    internal bool MoveObsidianPart(ObsidianCell part, Cell to)
    {
        if (!IsInside(to) || to == part.Cell || GetObsidian(part.Cell) is not { } source ||
            !source.Parts.Contains(part) || GetMaterial(to) is not (Material.Air or Material.Obsidian)) return false;
        var moved = part with { Cell = to };
        var target = GetObsidian(to);
        if (target != null && (target.OppositeUnits != 0 || part.OffsetUnits != 0 || target.OffsetUnits != 0 ||
            part.Face is CrustFace.Bottom or CrustFace.Top || target.Face != ObsidianCell.OppositeFace(part.Face) ||
            target.Units + part.Units > LiquidSystem.UnitsPerBlock)) return false;
        SetObsidianParts(part.Cell, source.Parts.Where(candidate => candidate.Face != part.Face));
        SetObsidianParts(to, target == null ? [moved] : target.Parts.Append(moved));
        Revision++;
        return true;
    }

    private void SetObsidianParts(Cell cell, IEnumerable<ObsidianCell> parts)
    {
        var remaining = parts.ToArray();
        if (remaining.Length == 0) { _obsidian.Remove(cell); return; }
        var first = remaining[0];
        _obsidian[cell] = remaining.Length == 1 ? first : first with
        {
            Units = first.Units + remaining[1].Units, Hp = first.Hp + remaining[1].Hp,
            OppositeUnits = remaining[1].Units, OppositeHp = remaining[1].Hp
        };
    }

    /// <summary>Damage the slab actually hit; its disjoint neighbor retains its own durability.</summary>
    public bool DamageObsidianPart(Cell cell, CrustFace face, float damage)
    {
        if (!float.IsFinite(damage) || damage <= 0 || GetObsidian(cell) is not { } crust) return false;
        var parts = crust.Parts.ToList();
        int index = parts.FindIndex(part => part.Face == face);
        if (index < 0) return false;
        if (damage < parts[index].Hp)
        {
            parts[index] = parts[index] with { Hp = parts[index].Hp - damage };
            SetObsidianParts(cell, parts);
            return false;
        }
        parts.RemoveAt(index);
        SetObsidianParts(cell, parts);
        Revision++;
        return true;
    }

    /// <summary>Point collision respects the actual thickness of an obsidian crust.</summary>
    public bool IsSolidAt(Vector3 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return false;
        var cell = new Cell((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z));
        if (!IsInside(cell)) return false;
        if (GetObsidian(cell) is { } crust)
            return crust.Parts.Any(part => point.X >= part.Min.X && point.Y >= part.Min.Y && point.Z >= part.Min.Z &&
                point.X < part.Max.X && point.Y < part.Max.Y && point.Z < part.Max.Z);
        return GetMaterial(cell) != Material.Air;
    }

    /// <summary>A crust seals its own face; other faces retain their unobstructed opening.</summary>
    public bool BlocksFace(Cell from, Cell to)
    {
        if (!IsInside(from) || !IsInside(to) || (to - from).Manhattan != 1) return true;
        if (Seals(from, LiquidSystem.FaceToward(from, to)) || Seals(to, LiquidSystem.FaceToward(to, from))) return true;
        var (aMin, aMax) = OpenBounds(from);
        var (bMin, bMax) = OpenBounds(to);
        var delta = to - from;
        return delta.X != 0 ? !Overlap(aMin.Y, aMax.Y, bMin.Y, bMax.Y) || !Overlap(aMin.Z, aMax.Z, bMin.Z, bMax.Z) :
            delta.Y != 0 ? !Overlap(aMin.X, aMax.X, bMin.X, bMax.X) || !Overlap(aMin.Z, aMax.Z, bMin.Z, bMax.Z) :
            !Overlap(aMin.X, aMax.X, bMin.X, bMax.X) || !Overlap(aMin.Y, aMax.Y, bMin.Y, bMax.Y);
    }

    private bool Seals(Cell cell, CrustFace face)
    {
        if (GetObsidian(cell) is { } crust) return crust.Units == LiquidSystem.UnitsPerBlock ||
            crust.Parts.Any(part => part.OffsetUnits == 0 && part.Face == face);
        return GetMaterial(cell) != Material.Air;
    }

    private (Vector3 Min, Vector3 Max) OpenBounds(Cell cell)
    {
        var min = new Vector3(cell.X, cell.Y, cell.Z);
        var max = min + Vector3.One;
        if (GetObsidian(cell) is { } cellCrust)
            foreach (var crust in cellCrust.Parts.Where(part => part.OffsetUnits == 0))
            switch (crust.Face)
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

    private static bool Overlap(float aMin, float aMax, float bMin, float bMax) => MathF.Min(aMax, bMax) - MathF.Max(aMin, bMin) > .00001f;

    internal bool AddObsidian(Cell cell, int units, CrustFace face, int offsetUnits = 0)
    {
        if (!CanAddObsidian(cell, units, face, offsetUnits)) return false;
        var previous = GetObsidian(cell);
        if (previous == null)
            _obsidian[cell] = new ObsidianCell(cell, units, face, units * LiquidSystem.ObsidianHpPerUnit, offsetUnits);
        else SetObsidianParts(cell, previous.Parts.Select(part => part.Face == face
            ? part with { Units = part.Units + units, Hp = part.Hp + units * LiquidSystem.ObsidianHpPerUnit } : part));
        Revision++;
        return true;
    }

    /// <summary>Returns true only when the crust is destroyed; its residual liquids remain.</summary>
    public bool DamageObsidian(Cell cell, float damage)
    {
        if (!float.IsFinite(damage) || damage <= 0 || GetObsidian(cell) is not { } crust) return false;
        if (damage < crust.Hp)
        {
            // Whole-cell damage is retained for mining and legacy callers. Split
            // nonlethal damage proportionally without silently healing either slab.
            float fraction = (crust.Hp - damage) / crust.Hp;
            SetObsidianParts(cell, crust.Parts.Select(part => part with { Hp = part.Hp * fraction }));
            return false;
        }
        _obsidian.Remove(cell);
        Revision++;
        return true;
    }

    internal void RestoreObsidian(IEnumerable<ObsidianCell> cells)
    {
        _obsidian.Clear();
        foreach (var cell in cells) _obsidian.Add(cell.Cell, cell);
        Revision++;
    }

    public float GetTerrainHp(Cell cell)
    {
        var material = GetMaterial(cell);
        return Balance.IsTerrainMaterial(material) ? _terrainHp.GetValueOrDefault(cell, Balance.BlockHp(material)) : 0;
    }

    /// <summary>Damages natural terrain; returns true only when a block is destroyed.</summary>
    internal bool DamageTerrain(Cell cell, float damage)
    {
        if (!float.IsFinite(damage) || damage <= 0 || !Balance.IsTerrainMaterial(GetMaterial(cell))) return false;
        var remaining = GetTerrainHp(cell) - damage;
        if (remaining <= 0) return Dig(cell);
        _terrainHp[cell] = remaining;
        // Partial damage does not change collision or invalidate the terrain mesh/navigation.
        return false;
    }

    internal bool RestoreTerrainHp(Cell cell, float hp)
    {
        var material = GetMaterial(cell);
        if (!Balance.IsTerrainMaterial(material) || !float.IsFinite(hp) || hp <= 0 || hp >= Balance.BlockHp(material)) return false;
        _terrainHp[cell] = hp;
        return true;
    }

    internal bool AddBarrier(Cell cell, Material content = Material.Dirt, float? hp = null)
    {
        if (!Balance.IsTerrainMaterial(content) || !IsInside(cell) || GetMaterial(cell) != Material.Air) return false;
        var durability = hp ?? Balance.BlockHp(content);
        if (!float.IsFinite(durability) || durability <= 0 || durability > Balance.BlockHp(content)) return false;
        _barriers.Add(cell, new Barrier { Cell = cell, Content = content, Hp = durability });
        Revision++;
        return true;
    }

    internal bool RemoveBarrier(Cell cell)
    {
        if (!_barriers.Remove(cell)) return false;
        Revision++;
        return true;
    }

    internal bool Dig(Cell cell)
    {
        if (!IsInside(cell) || !Balance.IsTerrainMaterial(GetMaterial(cell))) return false;
        _terrain[cell.X, cell.Y, cell.Z] = Material.Air;
        _terrainHp.Remove(cell);
        Revision++;
        return true;
    }
}
