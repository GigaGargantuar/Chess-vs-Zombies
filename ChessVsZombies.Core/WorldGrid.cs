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

    /// <summary>Point collision respects the actual thickness of an obsidian crust.</summary>
    public bool IsSolidAt(Vector3 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return false;
        var cell = new Cell((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z));
        if (!IsInside(cell)) return false;
        if (GetObsidian(cell) is { } crust)
            return point.X >= crust.Min.X && point.Y >= crust.Min.Y && point.Z >= crust.Min.Z &&
                point.X < crust.Max.X && point.Y < crust.Max.Y && point.Z < crust.Max.Z;
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
        if (GetObsidian(cell) is { } crust) return crust.Units == LiquidSystem.UnitsPerBlock || crust.OffsetUnits == 0 && crust.Face == face;
        return GetMaterial(cell) != Material.Air;
    }

    private (Vector3 Min, Vector3 Max) OpenBounds(Cell cell)
    {
        var min = new Vector3(cell.X, cell.Y, cell.Z);
        var max = min + Vector3.One;
        if (GetObsidian(cell) is { OffsetUnits: 0 } crust)
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
        if (!IsInside(cell) || !Enum.IsDefined(face) || units <= 0) return false;
        var previous = GetObsidian(cell);
        if (previous is null && GetMaterial(cell) != Material.Air || previous is not null && (previous.Face != face || previous.OffsetUnits != offsetUnits) ||
            offsetUnits < 0 || offsetUnits != 0 && face is not (CrustFace.Bottom or CrustFace.Top) ||
            units > LiquidSystem.UnitsPerBlock - (previous?.Units ?? 0) - offsetUnits) return false;
        _obsidian[cell] = new ObsidianCell(cell, units + (previous?.Units ?? 0), face,
            units * LiquidSystem.ObsidianHpPerUnit + (previous?.Hp ?? 0), offsetUnits);
        Revision++;
        return true;
    }

    /// <summary>Returns true only when the crust is destroyed; its residual liquids remain.</summary>
    public bool DamageObsidian(Cell cell, float damage)
    {
        if (!float.IsFinite(damage) || damage <= 0 || GetObsidian(cell) is not { } crust) return false;
        if (damage < crust.Hp)
        {
            _obsidian[cell] = crust with { Hp = crust.Hp - damage };
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
