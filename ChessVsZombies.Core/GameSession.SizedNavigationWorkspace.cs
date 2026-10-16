namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private SizedNavigationWorkspace? _sizedNavigationWorkspace;

    // Only temporary graph state is shared across sizes. Direct field builds
    // still publish their own dictionaries, while cached rebuilds may refill
    // the two dictionaries belonging to that same size.
    private sealed class SizedNavigationWorkspace
    {
        private const int Count = WorldGrid.Size * WorldGrid.Size * WorldGrid.Size;
        public int[] Rows { get; } = new int[Count];
        public byte[] Occupied { get; } = new byte[Count];
        public byte[] ClearNodes { get; } = new byte[Count];
        public float[] Edges { get; } = new float[Count * 16];
        public float[] GridCosts { get; } = new float[Count];
        public float[] Distances { get; } = new float[Count];
        public int[] Next { get; } = new int[Count];
        public (int Horizontal, int Vertical, float Travel)[] Ranks { get; } = new (int, int, float)[Count];
        public int[] EdgeOffsets { get; } = new int[16];
        public List<Cell> Nodes { get; } = new(Count / 2);
        public List<Cell> Goals { get; } = [];
        public List<Cell> Empty { get; } = [];
        public Dictionary<Cell, float> BreachCosts { get; } = [];
        public HashSet<Cell> StepBreaches { get; } = [];
        public PriorityQueue<int, float> Frontier { get; } = new(Count / 2);
        public PriorityQueue<int, (int Horizontal, int Vertical, float Travel)> FallbackFrontier { get; } = new(Count / 2);
    }
}
