namespace ChessVsZombies.Core;

/// <summary>
/// Session-local combat counts. Rates use 600 fixed 100 ms simulation-time bins;
/// an event is assigned to the bin containing its simulation timestamp.
/// </summary>
public sealed class DeathTollDiagnostics
{
    private const int RateBins = 600;
    private const double BinsPerSecond = 10;
    private struct RateBin
    {
        public long Tick;
        public long Processed;
        public long Kills;
    }

    private readonly RateBin[] _bins = new RateBin[RateBins];
    private long _currentTick;
    private long _recentProcessed;
    private long _recentKills;

    public double CombatSeconds { get; private set; }
    public long PhysicalSpawns { get; private set; }
    public long FieldDeaths { get; private set; }
    public long PreSpawnQueueDeaths { get; private set; }
    public long TotalDeaths => FieldDeaths + PreSpawnQueueDeaths;
    public long ProcessedQueuedSlots { get; private set; }
    public long DeadScheduledSlotsProcessed { get; private set; }
    public long QueuedProjectileHits { get; private set; }
    public double QueuedDamage { get; private set; }
    public long BoundaryImpactsWithoutQueuedDamage { get; private set; }
    public double QueueProcessingPerSecond => CombatSeconds <= 0 ? 0 : _recentProcessed / Math.Min(60, CombatSeconds);
    public double KillsPerSecond => CombatSeconds <= 0 ? 0 : _recentKills / Math.Min(60, CombatSeconds);

    internal void Advance(float seconds)
    {
        CombatSeconds += seconds;
        long nextTick = (long)Math.Floor(CombatSeconds * BinsPerSecond);
        if (nextTick - _currentTick >= RateBins)
        {
            Array.Clear(_bins);
            _recentProcessed = _recentKills = 0;
        }
        else for (long tick = _currentTick + 1; tick <= nextTick; tick++)
        {
            ref var bin = ref _bins[(int)(tick % RateBins)];
            if (bin.Tick == tick - RateBins)
            {
                _recentProcessed -= bin.Processed;
                _recentKills -= bin.Kills;
            }
            bin = default;
        }
        _currentTick = nextTick;
    }

    private ref RateBin CurrentBin()
    {
        ref var bin = ref _bins[(int)(_currentTick % RateBins)];
        bin.Tick = _currentTick;
        return ref bin;
    }

    internal void RecordPhysicalSpawn() => PhysicalSpawns++;
    internal void RecordFieldDeath()
    {
        FieldDeaths++;
        CurrentBin().Kills++;
        _recentKills++;
    }
    internal void RecordQueueDeath()
    {
        PreSpawnQueueDeaths++;
        CurrentBin().Kills++;
        _recentKills++;
    }
    internal void RecordProcessedQueueSlots(long count, bool dead)
    {
        ProcessedQueuedSlots += count;
        if (dead) DeadScheduledSlotsProcessed += count;
        CurrentBin().Processed += count;
        _recentProcessed += count;
    }
    internal void RecordQueuedProjectileHit(double damage)
    {
        QueuedProjectileHits++;
        QueuedDamage += damage;
    }
    internal void RecordBoundaryImpactWithoutQueuedDamage() => BoundaryImpactsWithoutQueuedDamage++;
}
