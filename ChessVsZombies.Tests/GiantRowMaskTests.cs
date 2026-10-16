using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GiantRowMaskTests
{
    private delegate bool StepCost(Cell from, Cell to, int footprint, byte[] occupied, float[] costs, int attack, out float cost);
    private delegate bool MaskedStepCost(Cell from, Cell to, int footprint, byte[] occupied, uint[] rows, float[] costs, int attack, out float cost);
    private delegate bool Sweep(Cell from, Cell to, int footprint, byte[] occupied, Span<int> breached,
        ref int count, Cell excludedMin, Cell excludedMax);
    private delegate bool MaskedSweep(Cell from, Cell to, int footprint, byte[] occupied, uint[] rows,
        Span<int> breached, ref int count, Cell excludedMin, Cell excludedMax);

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    [InlineData(3, false, false)]
    [InlineData(3, true, false)]
    [InlineData(3, false, true)]
    [InlineData(3, true, true)]
    public void RowMasksPreserveExactSweepOrderCoreRejectionAndFloatCosts(int footprint, bool dense, bool includeCore)
    {
        var game = new GameSession();
        var scalarCost = Bind<StepCost>(game, "TryGiantGridStepCost");
        var maskedCost = Bind<MaskedStepCost>(game, "TryGiantGridStepCostWithRows");
        var scalarSweep = BindStatic<Sweep>("AppendGiantStepSweep");
        var maskedSweep = BindStatic<MaskedSweep>("AppendGiantStepSweepWithRows");
        var occupied = new byte[WorldGrid.Size * WorldGrid.Size * WorldGrid.Size];
        var rows = new uint[WorldGrid.Size * WorldGrid.Size];
        var costs = new float[occupied.Length];
        var random = new Random(16417);
        for (int index = 0; index < occupied.Length; index++)
        {
            occupied[index] = random.Next(dense ? 2 : 12) == 0 ? (byte)1 : (byte)0;
            if (includeCore && random.Next(23) == 0) occupied[index] = 2;
            if (occupied[index] != 0) rows[index / WorldGrid.Size] |= 1u << (index % WorldGrid.Size);
            // Include unequal magnitudes so even the floating-point accumulation
            // contract is checked alongside the exact breached-index sequence.
            costs[index] = (index % 5) switch
            {
                0 => 0, 1 => float.Epsilon, 2 => .125f, 3 => 1e12f, _ => index * .01327f
            };
        }
        var directions = new[] { new Cell(1, 0, 0), new Cell(-1, 0, 0), new Cell(0, 0, 1), new Cell(0, 0, -1) };
        Span<int> scalar = stackalloc int[128];
        Span<int> masked = stackalloc int[128];
        int edge = WorldGrid.Size - footprint;
        foreach (int x in new[] { 0, 1, 14, edge - 1, edge })
            foreach (int y in new[] { 1, 16, edge })
                foreach (int z in new[] { 0, 1, 14, edge - 1, edge })
                    foreach (var direction in directions)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var from = new Cell(x, y, z);
                            var to = from + direction + new Cell(0, dy, 0);
                            if (to.X < 0 || to.Y < 1 || to.Z < 0 || to.X > edge || to.Y > edge || to.Z > edge) continue;
                            var waypoint = dy > 0 ? from + new Cell(0, 1, 0) : dy < 0 ? to + new Cell(0, 1, 0) : to;
                            var firstMin = new Cell(Math.Min(from.X, waypoint.X), Math.Min(from.Y, waypoint.Y), Math.Min(from.Z, waypoint.Z));
                            var firstMax = new Cell(Math.Max(from.X, waypoint.X) + footprint,
                                Math.Max(from.Y, waypoint.Y) + footprint, Math.Max(from.Z, waypoint.Z) + footprint);
                            int scalarCount = 0, maskedCount = 0;
                            bool expected = scalarSweep(from, waypoint, footprint, occupied, scalar, ref scalarCount, firstMin, firstMin) &&
                                (waypoint == to || scalarSweep(waypoint, to, footprint, occupied, scalar, ref scalarCount, firstMin, firstMax));
                            bool actual = maskedSweep(from, waypoint, footprint, occupied, rows, masked, ref maskedCount, firstMin, firstMin) &&
                                (waypoint == to || maskedSweep(waypoint, to, footprint, occupied, rows, masked, ref maskedCount, firstMin, firstMax));
                            Assert.Equal(expected, actual);
                            Assert.True(scalar[..scalarCount].SequenceEqual(masked[..maskedCount]));
                            expected = scalarCost(from, to, footprint, occupied, costs, 9, out float expectedCost);
                            actual = maskedCost(from, to, footprint, occupied, rows, costs, 9, out float actualCost);
                            Assert.Equal(expected, actual);
                            Assert.Equal(BitConverter.SingleToInt32Bits(expectedCost), BitConverter.SingleToInt32Bits(actualCost));
                        }
    }

    private static T Bind<T>(GameSession game, string name) where T : Delegate => typeof(GameSession)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<T>(game);

    private static T BindStatic<T>(string name) where T : Delegate => typeof(GameSession)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<T>();
}
