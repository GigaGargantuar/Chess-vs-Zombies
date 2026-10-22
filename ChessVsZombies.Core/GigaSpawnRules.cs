namespace ChessVsZombies.Core;

// The Super design is currently named Giga in the runtime and authored levels.
// Spawn initialization is independent of subsequent HP, RES, and ATK progression.
public static partial class GigaRules
{
    public const int MinimumInitialCounter = 10;

    /// <summary>
    /// Choose the closest HP to half the supplied spawn pool. Each live purchase
    /// supplies its wave's remaining budget; RES inherits the same counter.
    /// Exact ties choose the lower counter.
    /// </summary>
    public static int SelectInitialCounter(double originalWaveHp)
    {
        if (!double.IsFinite(originalWaveHp) || originalWaveHp < 0) throw new ArgumentOutOfRangeException(nameof(originalWaveHp));
        double target = originalWaveHp / 2;
        double root = Math.Pow(target, 1 / Math.E);
        if (root >= int.MaxValue) throw new ArgumentOutOfRangeException(nameof(originalWaveHp));
        int lower = Math.Max(MinimumInitialCounter, (int)Math.Floor(root));
        if (lower == int.MaxValue) return lower;
        int upper = lower + 1;
        double midpoint = (MaximumHp(lower) + MaximumHp(upper)) / 2;
        return target > midpoint ? upper : lower;
    }

    /// <summary>Integral wave budgets charge the ceiling once, without rounding the combat HP.</summary>
    public static long SpawnCost(int initialCounter)
    {
        if (initialCounter < MinimumInitialCounter) throw new ArgumentOutOfRangeException(nameof(initialCounter));
        double cost = Math.Ceiling(MaximumHp(initialCounter));
        if (cost >= long.MaxValue) throw new ArgumentOutOfRangeException(nameof(initialCounter));
        return (long)cost;
    }
}
