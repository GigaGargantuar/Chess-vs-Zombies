namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private int[] _sourceLevelNumbers = [];
    private LevelDefinition? _nextEncounter;
    private int[] _nextSourceLevelNumbers = [];
    private WaveSpawnPlan? _allInOnePlan;

    /// <summary>Combine the next five levels for one battle; cleared when that battle ends.</summary>
    public bool MergeFiveLevels { get; private set; }
    /// <summary>Compress the selected five-level range into one wave with final-wave equipment.</summary>
    public bool AllInOne { get; private set; }
    public int LevelSpan => MergeFiveLevels ? 5 : 1;
    public int EndLevelNumber => checked(LevelNumber + (LevelSpan - 1));
    public int SurvivorRewardPerTower => AllInOne ? 75 : MergeFiveLevels ? 25 : Balance.SurvivorIncome;

    public int GetSourceLevelNumber(int waveIndex) => waveIndex >= 0 && waveIndex < _sourceLevelNumbers.Length
        ? _sourceLevelNumbers[waveIndex] : LevelNumber;

    public bool SetMergeFiveLevels(bool enabled) => SetEncounterMode(enabled, allInOne: false);

    public bool SetAllInOne(bool enabled) => SetEncounterMode(enabled || MergeFiveLevels, enabled);

    private bool SetEncounterMode(bool merged, bool allInOne)
    {
        if (Phase != GamePhase.Building) return Reject("Choose whether to merge levels during preparation.");
        if (MergeFiveLevels == merged && AllInOne == allInOne) return true;
        int reward = allInOne ? 75 : merged ? 25 : Balance.SurvivorIncome;
        if (!CanHoldSurvivorReward(Economy.Currency, Turrets.Count(turret => turret.Hp > 0), reward))
            return Reject("The wallet cannot hold the full surviving-tower reward.");
        try
        {
            int span = merged ? 5 : 1;
            int next = checked(LevelNumber + span);
            var encounter = PrepareEncounter(LevelNumber, merged, allInOne);
            // Validate the following preparation snapshot before selecting an option
            // that could otherwise fail while committing a victory.
            _ = PrepareEncounter(next, merged: false);
            MergeFiveLevels = merged;
            AllInOne = allInOne;
            Level = encounter.Level;
            _sourceLevelNumbers = encounter.SourceLevels;
            _allInOnePlan = encounter.CombinedPlan;
            _previewLevel = null;
            _nextEncounter = null;
            _nextSourceLevelNumbers = [];
        }
        catch (Exception error) when (error is OverflowException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return Reject($"Cannot prepare this level selection: {error.Message}");
        }
        Message = allInOne
            ? $"All in one: levels {LevelNumber}–{EndLevelNumber}, 5x spawn rate and final-wave equipment. Victory pays 75 currency per surviving tower (7.5x)."
            : merged ? $"Levels {LevelNumber}–{EndLevelNumber} merged. Victory pays 25 currency per surviving tower."
            : $"Prepare level {LevelNumber}. Victory pays {Balance.SurvivorIncome} currency per surviving tower.";
        return true;
    }

    private (LevelDefinition Level, int[] SourceLevels, WaveSpawnPlan? CombinedPlan) PrepareEncounter(int firstLevel, bool merged, bool allInOne = false)
    {
        int span = merged ? 5 : 1;
        int lastLevel = checked(firstLevel + (span - 1));
        if (!merged)
        {
            var normal = _levels.Get(firstLevel);
            return (normal, Enumerable.Repeat(firstLevel, normal.Waves.Count).ToArray(), null);
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
        if (!allInOne) return (combined, sourceLevels.ToArray(), null);
        var plan = WaveSpawnPlanner.Combine(combined.Waves);
        var finalWave = combined.Waves[^1];
        combined.Name = $"All in one: levels {firstLevel}–{lastLevel}";
        combined.Waves = [new WaveDefinition
        {
            SpawnInterval = finalWave.SpawnInterval / 5,
            Groups = plan.Groups.Select(group => new ZombieGroup
            {
                Kind = group.Kind, Tier = group.Tier, Count = group.Count, Size = group.Size, IsBoss = group.IsBoss
            }).ToList()
        }];
        LevelCatalog.Validate(combined);
        return (combined, [lastLevel], plan);
    }

    private static bool CanHoldSurvivorReward(long currency, int towerCount, int rewardPerTower) =>
        currency >= 0 && currency + (long)towerCount * rewardPerTower <= int.MaxValue;

    private void ResetLevelMerging()
    {
        MergeFiveLevels = false;
        AllInOne = false;
        _allInOnePlan = null;
        _sourceLevelNumbers = [];
        _nextEncounter = null;
        _nextSourceLevelNumbers = [];
        _previewLevel = null;
        _previewPlans = [];
    }
}
