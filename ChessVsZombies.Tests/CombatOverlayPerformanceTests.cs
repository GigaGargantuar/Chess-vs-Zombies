using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class CombatOverlayPerformanceTests
{
    [Fact]
    public void SustainedSlowFramesEnableFallbackAndSustainedSmoothFramesRestoreDetail()
    {
        var policy = new CombatOverlayPerformance();
        Frames(policy, 30, 1d / 30);
        Assert.False(policy.ReducedDetail);
        Frames(policy, 30, 1d / 30);
        Assert.True(policy.ReducedDetail);
        Frames(policy, 55 * 4, 1d / 55);
        Assert.True(policy.ReducedDetail);
        Frames(policy, 55, 1d / 55);
        Assert.False(policy.ReducedDetail);
        Frames(policy, 60, 1d / 30);
        Assert.True(policy.ReducedDetail);
    }

    [Fact]
    public void HealthyFramesAndIsolatedStallsDoNotTriggerFallback()
    {
        var policy = new CombatOverlayPerformance();
        Frames(policy, 120, 1d / 60);
        policy.ObserveFrame(3, true, true);
        Assert.False(policy.ReducedDetail);
        Frames(policy, 120, 1d / 60);
        Frames(policy, 80, 1d / 40);
        Assert.False(policy.ReducedDetail);
    }

    [Fact]
    public void PauseFocusLossAndNonCombatFramesDoNotAccumulateSlowWindows()
    {
        var policy = new CombatOverlayPerformance();
        Frames(policy, 30, 1d / 30);
        policy.ObserveFrame(10, true, false);
        Frames(policy, 30, 1d / 30);
        Assert.False(policy.ReducedDetail);
        policy.ObserveFrame(10, false, true);
        Frames(policy, 30, 1d / 30);
        Assert.False(policy.ReducedDetail);
    }

    [Fact]
    public void IntermediateFrameRatesHoldTheCurrentMode()
    {
        var policy = new CombatOverlayPerformance();
        Frames(policy, 45 * 6, 1d / 45);
        Assert.False(policy.ReducedDetail);
        Frames(policy, 60, 1d / 30);
        Assert.True(policy.ReducedDetail);
        Frames(policy, 45 * 6, 1d / 45);
        Assert.True(policy.ReducedDetail);
    }

    [Fact]
    public void SlowerWindowInterruptsRecoveryAndShortSmoothBurstsDoNotToggle()
    {
        var policy = new CombatOverlayPerformance();
        Frames(policy, 60, 1d / 30);
        Frames(policy, 60 * 4, 1d / 60);
        Assert.True(policy.ReducedDetail);
        Frames(policy, 50, 1d / 50);
        Frames(policy, 60 * 4, 1d / 60);
        Assert.True(policy.ReducedDetail);
        Frames(policy, 60, 1d / 60);
        Assert.False(policy.ReducedDetail);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(double.NaN, true)]
    [InlineData(double.PositiveInfinity, true)]
    public void InactiveOrInvalidSamplesResetRecoveryWhileKeepingPerformanceMode(double seconds, bool active)
    {
        var policy = new CombatOverlayPerformance();
        Frames(policy, 60, 1d / 30);
        Frames(policy, 60 * 4, 1d / 60);
        policy.ObserveFrame(seconds, inCombat: true, activelyPlaying: active);
        Assert.True(policy.ReducedDetail);
        Frames(policy, 60 * 4, 1d / 60);
        Assert.True(policy.ReducedDetail);
        Frames(policy, 60, 1d / 60);
        Assert.False(policy.ReducedDetail);
    }

    [Fact]
    public void EndingCombatRestoresDetailAndResetsTransitionEvidence()
    {
        var policy = new CombatOverlayPerformance();
        Frames(policy, 60, 1d / 30);
        policy.ObserveFrame(1d / 60, inCombat: false, activelyPlaying: true);
        Assert.False(policy.ReducedDetail);
        Frames(policy, 30, 1d / 30);
        Assert.False(policy.ReducedDetail);
    }

    [Fact]
    public void DisablingFeedbackClearsExistingIndicatorsWithoutChangingDamage()
    {
        var normal = new GameSession();
        var reduced = new GameSession();
        var first = new Zombie { Id = 1, Kind = ZombieKind.ArmedArmored, Tier = GearTier.Osmium };
        var second = new Zombie { Id = 1, Kind = ZombieKind.ArmedArmored, Tier = GearTier.Osmium };
        normal.DamageZombie(first, 1, DamageKind.Chemical);
        reduced.DamageZombie(second, 1, DamageKind.Chemical);
        Assert.NotEmpty(reduced.Feedback.Hits);
        reduced.Feedback.Enabled = false;
        Assert.Empty(reduced.Feedback.Hits);
        Assert.Equal(0, reduced.Feedback.GetFlash(second.Id));
        for (int i = 0; i < 4; i++)
        {
            normal.DamageZombie(first, 1, DamageKind.Chemical);
            reduced.DamageZombie(second, 1, DamageKind.Chemical);
        }
        Assert.Equal(first.Hp, second.Hp);
        Assert.Equal(first.BodyHp, second.BodyHp);
        Assert.Empty(reduced.Feedback.Hits);
        Assert.Equal(0, reduced.Feedback.GetFlash(second.Id));
        reduced.Feedback.Enabled = true;
        reduced.DamageZombie(second, 1, DamageKind.Chemical);
        Assert.Single(reduced.Feedback.Hits);
        Assert.Equal(1, reduced.Feedback.GetFlash(second.Id));
    }

    private static void Frames(CombatOverlayPerformance policy, int count, double seconds)
    {
        for (int i = 0; i < count; i++) policy.ObserveFrame(seconds, true, true);
    }
}
