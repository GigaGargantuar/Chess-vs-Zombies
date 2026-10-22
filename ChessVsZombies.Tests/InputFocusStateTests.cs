using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class InputFocusStateTests
{
    [Fact]
    public void LosingFocusReleasesImmediatelyWhileUnpausedCombatContinues()
    {
        var input = FocusedAndCaptured();
        var game = Combat();
        Advance(input, game, .2f, paused: false);
        int before = game.Waves[0].Spawned;

        input.LoseFocus();

        Assert.False(input.Captured);
        var background = input.Observe(hasInputFocus: false, anyMouseButtonDown: true);
        Assert.False(background.CanProcessInput);
        Assert.True(background.SynchronizeInput);
        Advance(input, game, .8f, paused: false);
        Assert.Equal(1, game.DeathToll.CombatSeconds, 5);
        Assert.True(game.Waves[0].Spawned > before);
    }

    [Fact]
    public void ReturningFocusReseedsInputAndWaitsForHeldMouseButtonsWithoutRecapturing()
    {
        var input = FocusedAndCaptured();
        input.LoseFocus();
        input.Observe(hasInputFocus: false, anyMouseButtonDown: true);
        input.SynchronizeInput();

        var returned = input.Observe(hasInputFocus: true, anyMouseButtonDown: true);

        Assert.False(input.Captured);
        Assert.True(returned.SynchronizeInput);
        Assert.False(returned.CanProcessInput);
        Assert.True(input.SuppressMouseUntilReleased);
        Assert.True(input.Observe(hasInputFocus: true, anyMouseButtonDown: true).CanProcessInput);
        Assert.True(input.SuppressMouseUntilReleased);
        input.Observe(hasInputFocus: true, anyMouseButtonDown: false);
        Assert.False(input.SuppressMouseUntilReleased);
        Assert.False(input.Captured);

        input.Capture(requested: true, hasInputFocus: true);
        Assert.True(input.Captured);
        Assert.True(input.SuppressMouseUntilReleased);
    }

    [Fact]
    public void NativeFocusObservationReleasesCaptureEvenIfTheDeactivationEventHasNotArrived()
    {
        var input = FocusedAndCaptured();

        var frame = input.Observe(hasInputFocus: false, anyMouseButtonDown: false);

        Assert.False(frame.CanProcessInput);
        Assert.True(frame.SynchronizeInput);
        Assert.False(input.Captured);
        input.Capture(requested: true, hasInputFocus: false);
        Assert.False(input.Captured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualPauseIsRetainedAcrossFocusChangesAndUnpausingResumesRealElapsedTime(bool initiallyFocused)
    {
        var input = new InputFocusState();
        input.Observe(initiallyFocused, anyMouseButtonDown: false);
        var game = Combat();
        Advance(input, game, .3f, paused: false);
        int spawned = game.Waves[0].Spawned;

        input.Observe(!initiallyFocused, anyMouseButtonDown: false);
        Advance(input, game, 10, paused: true);
        input.Observe(initiallyFocused, anyMouseButtonDown: false);
        Advance(input, game, 10, paused: true);

        Assert.Equal(.3, game.DeathToll.CombatSeconds, 5);
        Assert.Equal(spawned, game.Waves[0].Spawned);
        input.LoseFocus();
        Advance(input, game, .4f, paused: false);
        Assert.Equal(.7, game.DeathToll.CombatSeconds, 5);
        Assert.True(game.Waves[0].Spawned > spawned);
    }

    private static InputFocusState FocusedAndCaptured()
    {
        var input = new InputFocusState();
        Assert.True(input.Observe(hasInputFocus: true, anyMouseButtonDown: false).SynchronizeInput);
        Assert.True(input.Observe(hasInputFocus: true, anyMouseButtonDown: false).CanProcessInput);
        input.Capture(requested: true, hasInputFocus: true);
        return input;
    }

    private static void Advance(InputFocusState input, GameSession game, float seconds, bool paused)
    {
        if (input.ShouldSimulate(paused)) game.Update(seconds);
    }

    private static GameSession Combat()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        { Waves = [new WaveDefinition { SpawnInterval = .2f, Groups = [new ZombieGroup { Count = 20 }] }] });
        var game = new GameSession(levels: levels);
        Assert.True(game.StartLevel());
        return game;
    }
}
