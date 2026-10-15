namespace ChessVsZombies.Core;

/// <summary>Creates independent editable jump patterns for new pieces and templates.</summary>
public static class JumpRules
{
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
