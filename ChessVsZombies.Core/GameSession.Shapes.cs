using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private readonly Dictionary<ZombieSize, (int WorldRevision, int TopologyRevision, List<Cell> Cells)> _sizedSpawnCells = [];

    private bool HasMilliGridSupport(Cell anchor)
    {
        if (!WorldGrid.IsInside(anchor) || anchor.Y <= 0) return false;
        var floor = anchor + new Cell(0, -1, 0);
        if (World.GetObsidian(floor) == null)
            return World.GetMaterial(floor) != Material.Air || HasLiveIndexedTurret(floor);
        // A Milli's canonical footprint lies entirely in this one column. A
        // lower slab cannot support it at this anchor, and a side leaf must
        // actually overlap its small feet. Falling bodies still scan floors.
        foreach (var bounds in SolidCollisionBounds(floor))
            if (Math.Abs(bounds.Max.Y - anchor.Y) <= .011f &&
                anchor.X + .6f > bounds.Min.X && anchor.X + .4f < bounds.Max.X &&
                anchor.Z + .6f > bounds.Min.Z && anchor.Z + .4f < bounds.Max.Z) return true;
        return false;
    }

    private bool HasKiloGridSupport(Cell anchor)
    {
        if (!WorldGrid.IsInside(anchor) || anchor.Y <= 0) return false;
        // A canonical Kilo occupies four entire floor cells. Its grid support
        // query needs only their upper faces; free falls still scan real floors.
        for (int x = 0; x < 2; x++)
            for (int z = 0; z < 2; z++)
            {
                var floor = anchor + new Cell(x, -1, z);
                if (World.GetObsidian(floor) is { } crust)
                {
                    if (crust.Parts.Any(part => Math.Abs(part.Max.Y - anchor.Y) <= .00001f)) return true;
                }
                else if (World.GetMaterial(floor) != Material.Air || HasLiveIndexedTurret(floor)) return true;
            }
        return false;
    }

    // The anchor is the lower corner of a Kilo's two-by-two-by-two grid volume.
    // Other sizes retain their existing single-cell navigation anchors.
    private static bool IsCoreApproach(Cell anchor, Zombie shape)
    {
        for (int x = 0; x < shape.GridFootprint; x++)
            for (int y = 0; y < shape.GridFootprint; y++)
                for (int z = 0; z < shape.GridFootprint; z++)
                    if (WorldGrid.IsCore(anchor + new Cell(x, y, z))) return false;
        for (int x = 0; x < shape.GridFootprint; x++)
            for (int y = 0; y < shape.GridFootprint; y++)
                for (int z = 0; z < shape.GridFootprint; z++)
                    foreach (var direction in Adjacent)
                        if (WorldGrid.IsCore(anchor + new Cell(x, y, z) + direction)) return true;
        return false;
    }

    private List<Cell> FindSizedSpawnCells(Zombie shape)
    {
        if (shape.Size != ZombieSize.Kilo) return _spawnCells;
        if (_sizedSpawnCells.TryGetValue(shape.Size, out var cached) &&
            cached.WorldRevision == World.Revision && cached.TopologyRevision == _topologyRevision) return cached.Cells;
        var cells = new List<Cell>();
        for (int x = 0; x <= WorldGrid.Size - shape.GridFootprint; x++)
            for (int y = 1; y <= WorldGrid.Size - shape.GridFootprint; y++)
                for (int z = 0; z <= WorldGrid.Size - shape.GridFootprint; z++)
                {
                    var cell = new Cell(x, y, z);
                    bool boundary = x == 0 || z == 0 || x + shape.GridFootprint == WorldGrid.Size ||
                        z + shape.GridFootprint == WorldGrid.Size || y + shape.GridFootprint == WorldGrid.Size;
                    if (boundary && HasZombieSupport(cell, shape) && IsBodyClear(shape.StandAt(cell), shape)) cells.Add(cell);
                }
        _sizedSpawnCells[shape.Size] = (World.Revision, _topologyRevision, cells);
        return cells;
    }
}
