using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private readonly record struct StructuralNode(Cell Cell, ObsidianCell? Part, Vector3 Min, Vector3 Max);

    private sealed class StructuralGravityWorkspace
    {
        public int[] Rows { get; } = new int[WorldGrid.Size * WorldGrid.Size * WorldGrid.Size];
        public bool[] Grounded { get; } = new bool[2 * WorldGrid.Size * WorldGrid.Size * WorldGrid.Size];
        public bool[] Visited { get; } = new bool[WorldGrid.Size * WorldGrid.Size * WorldGrid.Size];
        public List<StructuralNode> Nodes { get; } = [];
        public Queue<int> Frontier { get; } = [];
        public Queue<Cell> ComponentFrontier { get; } = [];
    }

    private StructuralGravityWorkspace? _structuralGravity;
    // Keep the original detached bodies separate while moving supports settle.
    // A later solver pass must not weld a falling body to a newly touched wall.
    private readonly Dictionary<Cell, int> _fallingBlockClusters = [];
    private readonly Dictionary<(Cell Cell, CrustFace Face), int> _fallingCrustParts = [];
    private int _nextFallingCluster;
    private static int StructuralIndex(Cell cell) => (cell.X * WorldGrid.Size + cell.Y) * WorldGrid.Size + cell.Z;

    private void ResetStructuralFalls()
    {
        _fallingBlockClusters.Clear();
        _fallingCrustParts.Clear();
        _mixedClusterParts.Clear();
        _mixedClusterFullBlocks.Clear();
        _nextFallingCluster = 0;
    }

    private void RegisterFallingBlockCluster(IEnumerable<Cell> cells)
    {
        var members = cells.ToArray();
        if (members.Length == 0) return;
        int group = members.Select(cell => _fallingBlockClusters.GetValueOrDefault(cell)).FirstOrDefault(value => value != 0);
        if (group == 0) group = ++_nextFallingCluster;
        foreach (var cell in members) _fallingBlockClusters[cell] = group;
    }

    private int FallingStructureGroup(StructuralNode node) => node.Part is { } part
        ? _fallingCrustParts.GetValueOrDefault((part.Cell, part.Face)) : _fallingBlockClusters.GetValueOrDefault(node.Cell);

    private bool CanJoinFallingStructure(StructuralNode first, StructuralNode second) =>
        FallingStructureGroup(first) == FallingStructureGroup(second);

    private void RegisterFallingMixedCluster(IEnumerable<StructuralNode> nodes)
    {
        var members = nodes.ToArray();
        int group = members.Select(FallingStructureGroup).FirstOrDefault(value => value != 0);
        if (group == 0) group = ++_nextFallingCluster;
        foreach (var node in members)
            if (node.Part is { } part) _fallingCrustParts[(part.Cell, part.Face)] = group;
            else _fallingBlockClusters[node.Cell] = group;
    }

    private bool MoveFallingBlock(Cell from, Cell to)
    {
        var parts = World.GetObsidian(from) is { Units: < LiquidSystem.UnitsPerBlock } crust ? crust.Parts.ToArray() : [];
        if (!World.MoveBlock(from, to)) return false;
        if (parts.Length > 0)
        {
            foreach (var part in parts)
            {
                if (!_fallingCrustParts.Remove((from, part.Face), out int group))
                    group = _fallingBlockClusters.GetValueOrDefault(from + new Cell(0, 1, 0));
                if (group == 0) group = ++_nextFallingCluster;
                _fallingCrustParts[(to, part.Face)] = group;
            }
        }
        else
        {
            if (!_fallingBlockClusters.Remove(from, out int group)) group = ++_nextFallingCluster;
            _fallingBlockClusters[to] = group;
        }
        return true;
    }

    private bool MoveFallingObsidianPart(ObsidianCell part, Cell to)
    {
        if (!World.MoveObsidianPart(part, to)) return false;
        if (!_fallingCrustParts.Remove((part.Cell, part.Face), out int group))
            group = _fallingBlockClusters.GetValueOrDefault(part.Cell + new Cell(0, 1, 0));
        if (group == 0) group = ++_nextFallingCluster;
        _fallingCrustParts[(to, part.Face)] = group;
        return true;
    }

    private void RefreshGroundedStructure()
    {
        var workspace = _structuralGravity ??= new StructuralGravityWorkspace();
        Array.Clear(workspace.Rows);
        Array.Clear(workspace.Grounded);
        workspace.Nodes.Clear();
        workspace.Frontier.Clear();
        int groundedCount = 0;
        foreach (var cell in World.MovableBlocks()) AddCell(cell);
        for (int x = 15; x <= 16; x++)
            for (int y = 15; y <= 16; y++)
                for (int z = 15; z <= 16; z++) AddCell(new Cell(x, y, z));

        void AddCell(Cell cell)
        {
            workspace.Rows[StructuralIndex(cell)] = workspace.Nodes.Count + 1;
            if (World.GetObsidian(cell) is { Units: < LiquidSystem.UnitsPerBlock } crust)
            {
                foreach (var part in crust.Parts) AddNode(new StructuralNode(cell, part, part.Min, part.Max));
            }
            else
            {
                var min = new Vector3(cell.X, cell.Y, cell.Z);
                AddNode(new StructuralNode(cell, null, min, min + Vector3.One));
            }
        }
        void AddNode(StructuralNode node)
        {
            int index = workspace.Nodes.Count;
            workspace.Nodes.Add(node);
            if (node.Min.Y <= .00001f || WorldGrid.IsCore(node.Cell)) Mark(index);
        }
        void Mark(int index)
        {
            var node = workspace.Nodes[index];
            if (FallingStructureGroup(node) != 0) return;
            if (workspace.Grounded[index]) return;
            workspace.Grounded[index] = true;
            groundedCount++;
            workspace.Frontier.Enqueue(index);
        }
        void Spread()
        {
            while (workspace.Frontier.TryDequeue(out int index))
            {
                var node = workspace.Nodes[index];
                Connect(node.Cell);
                foreach (var direction in Adjacent) Connect(node.Cell + direction);
                void Connect(Cell cell)
                {
                    if (!WorldGrid.IsInside(cell)) return;
                    int row = workspace.Rows[StructuralIndex(cell)] - 1;
                    if (row < 0) return;
                    for (int next = row; next < workspace.Nodes.Count && workspace.Nodes[next].Cell == cell; next++)
                    {
                        var neighbor = workspace.Nodes[next];
                        if (!workspace.Grounded[next] && TouchesBounds(node.Min, node.Max, neighbor.Min, neighbor.Max)) Mark(next);
                    }
                }
            }
        }
        Spread();
        // Towers provide support only through their vertical stack. Their side
        // faces cannot anchor an otherwise floating block structure.
        int previousGroundedCount;
        do
        {
            previousGroundedCount = groundedCount;
            foreach (var turret in Turrets)
            {
                if (turret.Hp <= 0) continue;
                var below = turret.Cell - new Cell(0, 1, 0);
                while (WorldGrid.IsInside(below) && TrySelectTurret(below) != null) below -= new Cell(0, 1, 0);
                if (below.Y >= 0 && !IsGroundedStructuralCell(below)) continue;
                MarkCell(turret.Cell);
                MarkCell(turret.Cell + new Cell(0, 1, 0));
            }
            Spread();
        } while (previousGroundedCount != groundedCount);
        void MarkCell(Cell cell)
        {
            if (!WorldGrid.IsInside(cell)) return;
            int row = workspace.Rows[StructuralIndex(cell)] - 1;
            if (row < 0) return;
            for (int index = row; index < workspace.Nodes.Count && workspace.Nodes[index].Cell == cell; index++) Mark(index);
        }
    }

    private bool IsGroundedStructuralCell(Cell cell)
    {
        if (!WorldGrid.IsInside(cell) || _structuralGravity is not { } workspace) return false;
        int row = workspace.Rows[StructuralIndex(cell)] - 1;
        if (row < 0) return false;
        for (int index = row; index < workspace.Nodes.Count && workspace.Nodes[index].Cell == cell; index++)
            if (workspace.Grounded[index]) return true;
        return false;
    }

    private bool IsGroundedObsidianPart(ObsidianCell part)
    {
        if (_structuralGravity is not { } workspace || !WorldGrid.IsInside(part.Cell)) return false;
        int row = workspace.Rows[StructuralIndex(part.Cell)] - 1;
        if (row < 0) return false;
        for (int index = row; index < workspace.Nodes.Count && workspace.Nodes[index].Cell == part.Cell; index++)
            if (workspace.Nodes[index].Part?.Face == part.Face && workspace.Grounded[index]) return true;
        return false;
    }

    private bool SettleUnsupportedBlockClusters(HashSet<Cell> movedSolids)
    {
        var workspace = _structuralGravity!;
        Array.Clear(workspace.Visited);
        var groups = new List<List<Cell>>();
        foreach (var origin in World.MovableBlocks())
        {
            if (!IsFullMovableBlock(origin) || TrySelectTurret(origin) != null || IsMixedClusterFullBlockHandled(origin) ||
                workspace.Visited[StructuralIndex(origin)]) continue;
            var members = new List<Cell>();
            bool grounded = false;
            int fallingGroup = _fallingBlockClusters.GetValueOrDefault(origin);
            workspace.Visited[StructuralIndex(origin)] = true;
            workspace.ComponentFrontier.Enqueue(origin);
            while (workspace.ComponentFrontier.TryDequeue(out var cell))
            {
                members.Add(cell);
                grounded |= IsGroundedStructuralCell(cell);
                foreach (var direction in Adjacent)
                {
                    var next = cell + direction;
                    if (!WorldGrid.IsInside(next) || workspace.Visited[StructuralIndex(next)] ||
                        !IsFullMovableBlock(next) || TrySelectTurret(next) != null || IsMixedClusterFullBlockHandled(next) ||
                        _fallingBlockClusters.GetValueOrDefault(next) != fallingGroup) continue;
                    workspace.Visited[StructuralIndex(next)] = true;
                    workspace.ComponentFrontier.Enqueue(next);
                }
            }
            if (!grounded) groups.Add(members);
        }
        bool moved = false;
        foreach (var members in groups.OrderBy(group => group.Min(cell => cell.Y)))
        {
            var ownCells = members.ToHashSet();
            int drop = 0;
            while (true)
            {
                int distance = drop + 1;
                if (members.Any(cell =>
                {
                    var destination = cell - new Cell(0, distance, 0);
                    return destination.Y < 0 || !ownCells.Contains(destination) &&
                        (World.GetMaterial(destination) != Material.Air || TrySelectTurret(destination) != null);
                })) break;
                drop++;
            }
            if (drop == 0) continue;
            RegisterFallingBlockCluster(members);
            var ordered = members.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ThenBy(cell => cell.Z).ToArray();
            for (int distance = 0; distance < drop; distance++)
                foreach (var origin in ordered)
                {
                    var from = origin - new Cell(0, distance, 0);
                    var to = from - new Cell(0, 1, 0);
                    if (!MoveFallingBlock(from, to)) throw new InvalidOperationException("A falling block cluster lost its geometry.");
                    Liquids.SwapDisplacedLiquid(from, to);
                }
            foreach (var origin in ordered)
            {
                var destination = origin - new Cell(0, drop, 0);
                Falls.RecordBlockDrop(origin, destination, World);
                movedSolids.Add(destination);
            }
            moved = true;
        }
        return moved;
    }
}
