namespace ChessVsZombies.Core;

/// <summary>Shared route estimates in walking-tile units; these do not change combat timing.</summary>
public static class NavigationCosts
{
    public const float WalkTilesPerSecond = .85f;
    public const float AttackPeriodSeconds = 1f;
    public const float InitialAttackCommitmentSeconds = .25f;
    public const float BreachExposurePremium = 5f;

    /// <summary>
    /// The first strike is immediate, with a small commitment cost; further strikes
    /// require whole attack periods. Stationary digging time receives an additional
    /// exposure premium because a unit cannot advance while repeatedly striking.
    /// </summary>
    public static float Breach(float remainingHp, int referenceAttack)
    {
        if (!float.IsFinite(remainingHp) || remainingHp < 0) throw new ArgumentOutOfRangeException(nameof(remainingHp));
        if (referenceAttack <= 0) throw new ArgumentOutOfRangeException(nameof(referenceAttack));
        if (remainingHp == 0) return 0;
        var hits = Math.Ceiling((double)remainingHp / referenceAttack);
        var occupiedSeconds = InitialAttackCommitmentSeconds + (hits - 1) * AttackPeriodSeconds;
        return (float)Math.Min(float.MaxValue, occupiedSeconds * WalkTilesPerSecond * (1 + BreachExposurePremium));
    }
}
