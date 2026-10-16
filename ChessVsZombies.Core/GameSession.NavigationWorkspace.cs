namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private NavigationWorkspace? _navigationWorkspace;

    // Geometry edits can rebuild the complete field several times in a battle.
    // Retain temporary storage rather than collecting multi-megabyte arrays on
    // those frames. Every builder resets its active arrays and queues before use.
    private sealed class NavigationWorkspace
    {
        private const int Cells = WorldGrid.Size * WorldGrid.Size * WorldGrid.Size;
        public int[] Rows { get; } = new int[Cells];
        public bool[] ClearNodes { get; } = new bool[Cells];
        public float[] Edges { get; } = new float[Cells * 16];
        public int[] EdgeOffsets { get; } = new int[16];
        public float[] Breaches { get; } = new float[Cells];
        public List<Cell> Nodes { get; } = new(Cells / 2);
        public List<Cell> Goals { get; } = [];
        public LaneWorkspace[] Lanes { get; } = [new(), new(), new(), new()];
        public int[] Horizontal { get; } = new int[Cells];
        public int[] Vertical { get; } = new int[Cells];
        public float[] Travel { get; } = new float[Cells];
        public int[] Next { get; } = new int[Cells];
        public PriorityQueue<int, (int Horizontal, int Vertical, float Travel)> SpentFrontier { get; } = new(Cells / 2);
    }

    private sealed class LaneWorkspace
    {
        private const int Cells = WorldGrid.Size * WorldGrid.Size * WorldGrid.Size;
        public float[] Costs { get; } = new float[Cells];
        public int[] Next { get; } = new int[Cells];
        public List<int> Order { get; } = new(Cells / 2);
        public PriorityQueue<int, float> Frontier { get; } = new(Cells / 2);
    }
}
