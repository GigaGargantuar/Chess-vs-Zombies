namespace ChessVsZombies.Core;

/// <summary>Presentation-only fallback driven by real rendered-frame intervals.</summary>
public sealed class CombatOverlayPerformance
{
    public const double SlowFrameSeconds = 1d / 20;
    public const double SmoothFrameSeconds = 1d / 25;
    private double windowSeconds;
    private int windowFrames, slowWindows, smoothWindows;

    public bool ReducedDetail { get; private set; }

    public void ObserveFrame(double seconds, bool inCombat, bool activelyPlaying)
    {
        if (!inCombat) ReducedDetail = false;
        if (!inCombat || !activelyPlaying || !double.IsFinite(seconds) || seconds <= 0)
        {
            windowSeconds = 0;
            windowFrames = slowWindows = smoothWindows = 0;
            return;
        }
        windowSeconds += seconds;
        windowFrames++;
        if (windowSeconds < 1 - .000001) return;
        double average = windowSeconds / windowFrames;
        if (ReducedDetail)
        {
            smoothWindows = average <= SmoothFrameSeconds + .000000001 ? smoothWindows + 1 : 0;
            if (smoothWindows >= 5)
            {
                ReducedDetail = false;
                slowWindows = smoothWindows = 0;
            }
        }
        else
        {
            slowWindows = average > SlowFrameSeconds + .000000001 ? slowWindows + 1 : 0;
            if (slowWindows >= 2)
            {
                ReducedDetail = true;
                slowWindows = smoothWindows = 0;
            }
        }
        windowSeconds = 0;
        windowFrames = 0;
    }
}
