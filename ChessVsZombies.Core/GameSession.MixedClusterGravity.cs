using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private readonly HashSet<(Cell Cell, CrustFace Face)> _mixedClusterParts = [];
    private readonly HashSet<Cell> _mixedClusterFullBlocks = [];

    private bool IsMixedClusterPartHandled(ObsidianCell part) => _mixedClusterParts.Contains((part.Cell, part.Face));
    private bool IsMixedClusterFullBlockHandled(Cell cell) => _mixedClusterFullBlocks.Contains(cell);

    // The column compression solver owns simple thin supports. Wider loads and
    // connected leaves must first share one descent, including a bottom slab's
    // existing grid-column load across its fractional standing gap.
    private bool SettleUnsupportedMixedClusters(HashSet<Cell> movedSolids, List<ObsidianCell> movedCrusts)
    {
        _mixedClusterParts.Clear();
        _mixedClusterFullBlocks.Clear();
        var workspace = _structuralGravity!;
        var visited = new HashSet<int>();
        var frontier = new Queue<int>();
        var groups = new List<(List<StructuralNode> Members, bool Grounded)>();
        for (int start = 0; start < workspace.Nodes.Count; start++)
        {
            if (workspace.Nodes[start].Part == null || workspace.Grounded[start] || !visited.Add(start)) continue;
            var members = new List<StructuralNode>();
            bool grounded = false;
            frontier.Enqueue(start);
            while (frontier.TryDequeue(out int index))
            {
                var node = workspace.Nodes[index];
                members.Add(node);
                grounded |= workspace.Grounded[index];
                Connect(node.Cell);
                foreach (var direction in Adjacent) Connect(node.Cell + direction);
                void Connect(Cell cell)
                {
                    if (!WorldGrid.IsInside(cell)) return;
                    int row = workspace.Rows[StructuralIndex(cell)] - 1;
                    if (row < 0) return;
                    for (int next = row; next < workspace.Nodes.Count && workspace.Nodes[next].Cell == cell; next++)
                    {
                        if (visited.Contains(next)) continue;
                        var other = workspace.Nodes[next];
                        if (!CanJoinFallingStructure(node, other)) continue;
                        bool columnLoad = node.Part != null && other.Part == null && cell == node.Cell + new Cell(0, 1, 0) ||
                            node.Part == null && other.Part != null && node.Cell == cell + new Cell(0, 1, 0);
                        if (!columnLoad && !TouchesBounds(node.Min, node.Max, other.Min, other.Max)) continue;
                        visited.Add(next);
                        frontier.Enqueue(next);
                    }
                }
            }
            if (members.Select(node => (node.Cell.X, node.Cell.Z)).Distinct().Skip(1).Any()) groups.Add((members, grounded));
        }

        bool changed = false;
        var shatteredCells = new HashSet<Cell>();
        foreach (var group in groups.OrderBy(group => group.Members.Min(node => node.Cell.Y)))
        {
            var members = group.Members.OrderBy(node => node.Cell.Y).ThenBy(node => node.Cell.X).ThenBy(node => node.Cell.Z).ToArray();
            foreach (var node in members)
                if (node.Part is { } part) _mixedClusterParts.Add((part.Cell, part.Face));
            if (group.Grounded)
            {
                foreach (var node in members)
                    if (node.Part == null) _mixedClusterFullBlocks.Add(node.Cell);
                continue;
            }
            var fullCells = members.Where(node => node.Part == null).Select(node => node.Cell).ToHashSet();
            var parts = members.Where(node => node.Part != null).Select(node => (node.Cell, node.Part!.Face)).ToHashSet();
            var carriedTurrets = members.Where(node => node.Part == null).Select(node => TrySelectTurret(node.Cell))
                .Where(turret => turret != null).Cast<Turret>().Distinct().Select(turret => (Turret: turret, Origin: turret.Cell)).ToArray();
            int drop = 0;
            List<StructuralNode> impacts;
            while (CanDescend(drop, out impacts)) drop++;
            if (drop > 0) RegisterFallingMixedCluster(members);

            for (int step = 0; step < drop; step++)
                foreach (var node in members)
                {
                    var from = node.Cell - new Cell(0, step, 0);
                    var to = from - new Cell(0, 1, 0);
                    if (node.Part is { } part)
                    {
                        var live = World.GetObsidian(from)?.Parts.FirstOrDefault(candidate => candidate.Face == part.Face);
                        if (live == null || !MoveFallingObsidianPart(live, to))
                            throw new InvalidOperationException("A falling mixed cluster lost its slab geometry.");
                        Liquids.DisplaceForObsidianMove(from, to);
                    }
                    else
                    {
                        if (!MoveFallingBlock(from, to)) throw new InvalidOperationException("A falling mixed cluster lost its block geometry.");
                        Liquids.SwapDisplacedLiquid(from, to);
                        foreach (var carried in carriedTurrets)
                            if (carried.Turret.Cell == from) { carried.Turret.Cell = to; _topologyRevision++; }
                    }
                }

            // Compression belongs to the thin members that actually meet the
            // floor. An unhit leaf hanging from a landed neighbor remains intact.
            foreach (var node in impacts)
            {
                if (node.Part is not { } part ||
                    !fullCells.Contains(node.Cell + new Cell(0, 1, 0)) ||
                    drop == 0 && part.Min.Y <= part.Cell.Y + .00001f) continue;
                var cell = node.Cell - new Cell(0, drop, 0);
                var live = World.GetObsidian(cell)?.Parts.FirstOrDefault(candidate => candidate.Face == part.Face);
                if (live == null) continue;
                World.DamageObsidianPart(cell, live.Face, live.Hp);
                if (shatteredCells.Add(cell)) ShatterObsidian(cell);
                changed = true;
            }
            foreach (var node in members)
            {
                var destination = node.Cell - new Cell(0, drop, 0);
                if (node.Part is { } part)
                {
                    _mixedClusterParts.Add((destination, part.Face));
                    var live = World.GetObsidian(destination)?.Parts.FirstOrDefault(candidate => candidate.Face == part.Face);
                    if (live == null || drop == 0) continue;
                    Falls.RecordObsidianDrop(node.Cell, destination, live);
                    movedCrusts.Add(live);
                }
                else if (drop > 0)
                {
                    Falls.RecordBlockDrop(node.Cell, destination, World);
                    movedSolids.Add(destination);
                }
            }
            foreach (var carried in carriedTurrets) Falls.RecordTurretDrop(carried.Turret, carried.Origin);
            if (members.Any(node => node.Part is { } part && World.GetObsidian(node.Cell - new Cell(0, drop, 0))?.Parts
                .Any(live => live.Face == part.Face) == true))
                foreach (var cell in fullCells) _mixedClusterFullBlocks.Add(cell - new Cell(0, drop, 0));
            changed |= drop > 0;

            bool CanDescend(int distance, out List<StructuralNode> contact)
            {
                contact = [];
                bool clear = true;
                foreach (var node in members)
                {
                    var destination = node.Cell - new Cell(0, distance + 1, 0);
                    if (destination.Y < 0) { clear = false; continue; }
                    var displacement = Vector3.UnitY * distance;
                    var sweptMin = node.Min - displacement - Vector3.UnitY;
                    var sweptMax = node.Max - displacement;
                    bool hit = false;
                    for (int x = Math.Max(0, (int)MathF.Floor(sweptMin.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(sweptMax.X) - 1); x++)
                        for (int y = Math.Max(0, (int)MathF.Floor(sweptMin.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(sweptMax.Y) - 1); y++)
                            for (int z = Math.Max(0, (int)MathF.Floor(sweptMin.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(sweptMax.Z) - 1); z++)
                            {
                                var cell = new Cell(x, y, z);
                                if (fullCells.Contains(cell)) continue;
                                if (World.GetObsidian(cell) is { } crust)
                                {
                                    foreach (var other in crust.Parts)
                                        if (!parts.Contains((cell, other.Face)) && BoundsOverlap(sweptMin, sweptMax, other.Min, other.Max)) hit = true;
                                }
                                else if (World.GetMaterial(cell) != Material.Air || TrySelectTurret(cell) != null)
                                {
                                    var min = new Vector3(x, y, z);
                                    if (BoundsOverlap(sweptMin, sweptMax, min, min + Vector3.One)) hit = true;
                                }
                            }
                    if (hit) { clear = false; contact.Add(node); }
                    // The cell format can merge disjoint opposite side leaves;
                    // vertical/internal leaves retain their existing separate-cell limit.
                    if (node.Part is { } moving && !fullCells.Contains(destination) && World.GetObsidian(destination) is { } target)
                    {
                        var external = target.Parts.Where(other => !parts.Contains((destination, other.Face))).ToArray();
                        if (external.Length > 0 && (external.Length != 1 || moving.OffsetUnits != 0 || external[0].OffsetUnits != 0 ||
                            moving.Face is CrustFace.Bottom or CrustFace.Top || external[0].Face != ObsidianCell.OppositeFace(moving.Face) ||
                            moving.Units + external[0].Units > LiquidSystem.UnitsPerBlock)) clear = false;
                    }
                }
                return clear;
            }
        }
        return changed;
    }
}
