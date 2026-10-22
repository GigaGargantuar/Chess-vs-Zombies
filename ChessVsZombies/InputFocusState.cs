namespace ChessVsZombies;

/// <summary>Desktop input ownership is independent of the player's simulation pause.</summary>
internal sealed class InputFocusState
{
    private bool focused, synchronizeInput = true;

    public bool Captured { get; private set; } = true;
    public bool SuppressMouseUntilReleased { get; private set; }

    public void SynchronizeInput()
    {
        synchronizeInput = true;
        SuppressMouseUntilReleased = true;
    }

    public void LoseFocus()
    {
        focused = false;
        Captured = false;
        SynchronizeInput();
    }

    public void Capture(bool requested, bool hasInputFocus)
    {
        Captured = requested && hasInputFocus;
        if (Captured) SuppressMouseUntilReleased = true;
    }

    public InputFocusFrame Observe(bool hasInputFocus, bool anyMouseButtonDown)
    {
        bool changed = focused != hasInputFocus;
        if (changed) SynchronizeInput();
        if (!hasInputFocus) Captured = false;
        bool synchronizing = synchronizeInput || changed;
        synchronizeInput = false;
        focused = hasInputFocus;
        if (SuppressMouseUntilReleased && !anyMouseButtonDown) SuppressMouseUntilReleased = false;
        return new(hasInputFocus && !synchronizing, synchronizing);
    }

    public bool ShouldSimulate(bool manuallyPaused) => !manuallyPaused;
}

internal readonly record struct InputFocusFrame(bool CanProcessInput, bool SynchronizeInput);
