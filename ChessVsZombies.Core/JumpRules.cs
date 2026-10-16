namespace ChessVsZombies.Core;

/// <summary>Creates independent editable jump patterns for new pieces and templates.</summary>
public static class JumpRules
{
    /// <summary>The farthest selected cell measured by the game's longest-axis jump radius.</summary>
    public static long FormationRadius(IEnumerable<Cell> offsets) => offsets.Select(offset =>
        Math.Max(Math.Abs((long)offset.X), Math.Max(Math.Abs((long)offset.Y), Math.Abs((long)offset.Z))))
        .DefaultIfEmpty(0).Max();

    public static List<Cell> DefaultPattern(PieceKind kind) => kind == PieceKind.Knight
        ? DefaultKnightPattern() : DefaultHorizontalPattern();

    public static List<Cell> DefaultKnightPattern()
    {
        var horizontal = DefaultHorizontalPattern();
        return horizontal.Concat(horizontal.Select(offset => new Cell(offset.X, offset.Z, 0)))
            .Concat(horizontal.Select(offset => new Cell(0, offset.X, offset.Z))).ToList();
    }

    private static List<Cell> DefaultHorizontalPattern() =>
        [new(2, 0, 1), new(2, 0, -1), new(-2, 0, 1), new(-2, 0, -1),
         new(1, 0, 2), new(-1, 0, 2), new(1, 0, -2), new(-1, 0, -2)];
}
