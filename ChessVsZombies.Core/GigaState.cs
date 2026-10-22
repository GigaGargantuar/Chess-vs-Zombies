namespace ChessVsZombies.Core;

/// <summary>Wave initialization and the three independent constant-based Giga progressions.</summary>
public static partial class GigaRules
{
    public static readonly double GoldenRatio = (1 + Math.Sqrt(5)) / 2;
    public const int BlastSize = 3;
    public const float BlastHalfExtent = BlastSize / 2f;

    public static double MaximumHp(int counter)
    {
        if (counter < 1) throw new ArgumentOutOfRangeException(nameof(counter));
        return Math.Pow(counter, Math.E);
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
    // Compatibility half-extent: the blast is a fixed cube, independent of ATK.
    public float BlastRadius => GigaRules.BlastHalfExtent;
    public long SpawnCost => GigaRules.SpawnCost(InitialCounter);

    public GigaState(int initialCounter = GigaRules.MinimumInitialCounter)
    {
        if (initialCounter < GigaRules.MinimumInitialCounter) throw new ArgumentOutOfRangeException(nameof(initialCounter));
        InitialCounter = HpCounter = ResCounter = AttackCounter = initialCounter;
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

    /// <summary>Call once for a successful attack; killing anything resets ATK to 1.</summary>
    public void RecordSuccessfulAttack(bool killedAnything)
    {
        AttackCounter = killedAnything ? 1 : checked(AttackCounter + 1);
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
