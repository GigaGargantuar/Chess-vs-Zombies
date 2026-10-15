namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private int[] _sourceLevelNumbers = [];
    private LevelDefinition? _nextEncounter;
    private int[] _nextSourceLevelNumbers = [];

    /// <summary>Combine the next five levels for one battle; cleared when that battle ends.</summary>
    public bool MergeFiveLevels { get; private set; }
    public int LevelSpan => MergeFiveLevels ? 5 : 1;
    public int EndLevelNumber => checked(LevelNumber + (LevelSpan - 1));
    public int SurvivorRewardPerTower => MergeFiveLevels ? 25 : Balance.SurvivorIncome;

    public int GetSourceLevelNumber(int waveIndex) => waveIndex >= 0 && waveIndex < _sourceLevelNumbers.Length
        ? _sourceLevelNumbers[waveIndex] : LevelNumber;

    public bool SetMergeFiveLevels(bool enabled)
    {
        if (Phase != GamePhase.Building) return Reject("Choose whether to merge levels during preparation.");
        if (MergeFiveLevels == enabled) return true;
        int reward = enabled ? 25 : Balance.SurvivorIncome;
        if (!CanHoldSurvivorReward(Economy.Currency, Turrets.Count(turret => turret.Hp > 0), reward))
            return Reject("The wallet cannot hold the full surviving-tower reward.");
        try
        {
            int span = enabled ? 5 : 1;
            int next = checked(LevelNumber + span);
            var encounter = PrepareEncounter(LevelNumber, enabled);
            // Validate the following preparation snapshot before selecting an option
            // that could otherwise fail while committing a victory.
            _ = PrepareEncounter(next, merged: false);
            MergeFiveLevels = enabled;
            Level = encounter.Level;
            _sourceLevelNumbers = encounter.SourceLevels;
            _previewLevel = null;
            _nextEncounter = null;
            _nextSourceLevelNumbers = [];
        }
        catch (Exception error) when (error is OverflowException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return Reject($"Cannot prepare this level selection: {error.Message}");
        }
        Message = enabled
            ? $"Levels {LevelNumber}–{EndLevelNumber} merged. Victory pays 25 currency per surviving tower."
            : $"Prepare level {LevelNumber}. Victory pays {Balance.SurvivorIncome} currency per surviving tower.";
        return true;
    }

    private (LevelDefinition Level, int[] SourceLevels) PrepareEncounter(int firstLevel, bool merged)
    {
        int span = merged ? 5 : 1;
        int lastLevel = checked(firstLevel + (span - 1));
        if (!merged)
        {
            var normal = _levels.Get(firstLevel);
            return (normal, Enumerable.Repeat(firstLevel, normal.Waves.Count).ToArray());
        }
        var combined = new LevelDefinition
        {
            Number = firstLevel,
            Name = $"Merged levels {firstLevel}–{lastLevel}"
        };
        var sourceLevels = new List<int>();
        for (int offset = 0; offset < span; offset++)
        {
            int sourceLevel = checked(firstLevel + offset);
            var level = _levels.Get(sourceLevel);
            combined.Waves.AddRange(level.Waves);
            sourceLevels.AddRange(Enumerable.Repeat(sourceLevel, level.Waves.Count));
        }
        return (combined, sourceLevels.ToArray());
    }

    private static bool CanHoldSurvivorReward(long currency, int towerCount, int rewardPerTower) =>
        currency >= 0 && currency + (long)towerCount * rewardPerTower <= int.MaxValue;

    private void ResetLevelMerging()
    {
        MergeFiveLevels = false;
        _sourceLevelNumbers = [];
        _nextEncounter = null;
        _nextSourceLevelNumbers = [];
        _previewLevel = null;
        _previewPlans = [];
    }
}
