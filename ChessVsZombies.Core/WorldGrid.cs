namespace ChessVsZombies.Core;

/// <summary>The immutable objective, editable terrain, and defensive blocks in the 32-cube battlefield.</summary>
public sealed class WorldGrid
{
    public const int Size = 32;
    private readonly Material[,,] _terrain = new Material[Size, Size, Size];
    private readonly Dictionary<Cell, Barrier> _barriers = [];
    public int Revision { get; private set; }
    public IEnumerable<Barrier> Barriers => _barriers.Values;

    public WorldGrid()
    {
        for (var x = 0; x < Size; x++)
            for (var y = 0; y < Size; y++)
                for (var z = 0; z < Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    _terrain[x, y, z] = IsCore(cell) ? Material.Core :
                        y < 12 ? Material.Stone : y < 16 ? Material.Dirt : Material.Air;
                }
    }

    public static bool IsInside(Cell cell) => cell.X is >= 0 and < Size && cell.Y is >= 0 and < Size && cell.Z is >= 0 and < Size;
    public static bool IsCore(Cell cell) => cell.X is >= 15 and <= 16 && cell.Y is >= 15 and <= 16 && cell.Z is >= 15 and <= 16;
    public static bool IsBuildZone(Cell cell) => cell.X is >= 12 and <= 19 && cell.Y is >= 12 and <= 19 && cell.Z is >= 12 and <= 19;
    public static bool IsTurretZone(Cell cell) => cell.X is >= 1 and <= 30 && cell.Y is >= 1 and <= 30 && cell.Z is >= 1 and <= 30 && !IsBuildZone(cell);
    public static bool IsSpawnZone(Cell cell) => IsInside(cell) &&
        (cell.X is 0 or 31 || cell.Y is 0 or 31 || cell.Z is 0 or 31);
    public Material GetMaterial(Cell cell) => !IsInside(cell) ? Material.Air :
        _barriers.ContainsKey(cell) ? Material.Barrier : _terrain[cell.X, cell.Y, cell.Z];
    public bool HasSupport(Cell cell) => cell.Y > 0 && IsInside(cell) && GetMaterial(cell + new Cell(0, -1, 0)) != Material.Air;
    public Barrier? GetBarrier(Cell cell) => _barriers.GetValueOrDefault(cell);

    internal bool AddBarrier(Cell cell, Material content = Material.Dirt, float? hp = null)
    {
        if (content is not (Material.Dirt or Material.Stone) || !IsInside(cell) || GetMaterial(cell) != Material.Air) return false;
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
        if (!IsInside(cell) || GetMaterial(cell) is not (Material.Dirt or Material.Stone)) return false;
        _terrain[cell.X, cell.Y, cell.Z] = Material.Air;
        Revision++;
        return true;
    }
}
