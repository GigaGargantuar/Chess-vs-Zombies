namespace ChessVsZombies.Core;

/// <summary>Wave initialization and the three independent constant-based Giga progressions.</summary>
public static class GigaRules
{
    public const int MinimumInitialCounter = 10;
    public static readonly double GoldenRatio = (1 + Math.Sqrt(5)) / 2;

    /// <summary>Choose the closest HP to half the original wave pool; exact ties choose the lower counter.</summary>
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

    public static double MaximumHp(int counter)
    {
        if (counter < 1) throw new ArgumentOutOfRangeException(nameof(counter));
        return Math.Pow(counter, Math.E);
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

public sealed class GigaState
{
    private static readonly double LogPi = Math.Log(Math.PI);
    private double _resistance;
    public int InitialCounter { get; }
    public int HpCounter { get; private set; }
    public int ResCounter { get; private set; }
    public int AttackCounter { get; private set; }
    public double RecoverySeconds { get; private set; }
    public float MaxHp => (float)GigaRules.MaximumHp(HpCounter);
    public float Resistance => (float)_resistance;
    public float AttackDamage => (float)Math.Pow(GigaRules.GoldenRatio, AttackCounter);
    // Algebraically equal to log10(ATK), and remains usable even when the float ATK overflows.
    public float BlastRadius => (float)(AttackCounter * Math.Log10(GigaRules.GoldenRatio));
    public long SpawnCost => GigaRules.SpawnCost(InitialCounter);

    public GigaState(int initialCounter = GigaRules.MinimumInitialCounter)
    {
        if (initialCounter < GigaRules.MinimumInitialCounter) throw new ArgumentOutOfRangeException(nameof(initialCounter));
        InitialCounter = HpCounter = ResCounter = initialCounter;
        AttackCounter = 1;
        RecomputeResistance();
    }

    public static GigaState Restore(int initialCounter, int hpCounter, int resCounter, int attackCounter, double recoverySeconds)
    {
        if (hpCounter < initialCounter || resCounter < 1 || attackCounter < 1 ||
            !double.IsFinite(recoverySeconds) || recoverySeconds < 0 || recoverySeconds >= 1)
            throw new ArgumentOutOfRangeException(nameof(hpCounter), "Giga counters and its partial-second recovery timer must be valid.");
        var state = new GigaState(initialCounter)
        {
            HpCounter = hpCounter,
            ResCounter = resCounter,
            AttackCounter = attackCounter,
            RecoverySeconds = recoverySeconds
        };
        state.RecomputeResistance();
        return state;
    }

    /// <summary>Every hit, including zero damage, resets recovery and wears RES down before any later recovery.</summary>
    internal void ApplyHit(float damage, ref float currentHp)
    {
        bool growHp = ResCounter == 1;
        float oldMaximum = MaxHp;
        if (damage > Resistance) currentHp = Math.Max(0, currentHp - damage);
        RecoverySeconds = 0;
        if (ResCounter > 1)
        {
            _resistance -= Math.Log(ResCounter) / LogPi;
            ResCounter--;
            if (ResCounter == 1) _resistance = 0;
        }
        if (!growHp || currentHp <= 0) return;
        HpCounter = checked(HpCounter + 1);
        currentHp = (float)((double)currentHp * MaxHp / oldMaximum);
    }

    public void AdvanceRecovery(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        RecoverySeconds += seconds;
        while (RecoverySeconds >= 1)
        {
            ResCounter = checked(ResCounter + 1);
            _resistance += Math.Log(ResCounter) / LogPi;
            RecoverySeconds -= 1;
        }
    }

    /// <summary>Call once for a damaging projectile impact; a kill of its locked target resets ATK.</summary>
    public void RecordSuccessfulAttack(bool killedLockedTarget)
    {
        AttackCounter = killedLockedTarget ? 1 : checked(AttackCounter + 1);
    }

    private void RecomputeResistance()
    {
        if (ResCounter > 4096)
        {
            // Euler–Maclaurin evaluates the same logarithm sum without a loop
            // proportional to untrusted saved counters. At this threshold its
            // error is far below one float ULP. No factorial is constructed.
            double n = ResCounter;
            double inverse = 1 / n;
            double inverseSquared = inverse * inverse;
            _resistance = (n * Math.Log(n) - n + .5 * Math.Log(2 * Math.PI * n) +
                inverse / 12 - inverse * inverseSquared / 360 + inverse * inverseSquared * inverseSquared / 1260) / LogPi;
            return;
        }
        _resistance = 0;
        // Sum logarithms directly: no intermediate factorial can overflow.
        for (int counter = 2; counter <= ResCounter; counter++) _resistance += Math.Log(counter) / LogPi;
    }
}
