using System.Text.Json.Serialization;

namespace ChessVsZombies.Core;

public sealed class UpgradeBatchTarget
{
    public int TurretId { get; set; }
    public Dictionary<UpgradeKind, int> Expected { get; set; } = [];
    public Dictionary<UpgradeKind, int> Additions { get; set; } = [];
}

public sealed class UpgradeBatchState
{
    public List<UpgradeBatchTarget> Targets { get; set; } = [];
    public Dictionary<UpgradeKind, long> Costs { get; set; } = [];
    [JsonIgnore]
    public long Cost => Costs.Values.Sum();
    // Null means waiting for every required component type. No points are reserved.
    public float? GraceRemaining { get; set; }
}

public sealed partial class GameSession
{
    public const float UpgradeBatchGraceSeconds = 3f;
    public UpgradeBatchState? QueuedUpgradeBatch { get; private set; }

    private static UpgradeBatchState? CopyUpgradeBatch(UpgradeBatchState? source) => source == null ? null : new()
    {
        Costs = new Dictionary<UpgradeKind, long>(source.Costs),
        GraceRemaining = source.GraceRemaining,
        Targets = source.Targets.Select(target => new UpgradeBatchTarget
        {
            TurretId = target.TurretId,
            Expected = new Dictionary<UpgradeKind, int>(target.Expected),
            Additions = new Dictionary<UpgradeKind, int>(target.Additions)
        }).ToList()
    };

    public IReadOnlyDictionary<UpgradeKind, long> GetUpgradeBatchCosts(IEnumerable<int> turretIds, UpgradeTemplate template)
    {
        var costs = new Dictionary<UpgradeKind, long>();
        if (turretIds is null || template?.Points is null) return costs;
        var byId = Turrets.Where(turret => turret.Hp > 0).ToDictionary(turret => turret.Id);
        foreach (int id in turretIds.Distinct())
        {
            if (!byId.TryGetValue(id, out var turret)) continue;
            foreach (var (kind, requested) in template.Points)
                if (Enum.IsDefined(kind) && requested > 0)
                {
                    int added = Math.Min(requested, Economy.GetUpgradeCapacity(turret, kind));
                    if (added > 0) costs[kind] = costs.GetValueOrDefault(kind) + added;
                }
        }
        return costs;
    }

    public bool SpendUpgradeBatchNow(IEnumerable<int> turretIds, UpgradeTemplate template)
    {
        if (!TryPlanUpgradeBatch(turretIds, template, spendNow: true, out var batch)) return false;
        CommitUpgradeBatch(batch!);
        Message = $"Bought {batch!.Cost} upgrades with matching component points.";
        CancelChangedUpgradeBatch();
        return true;
    }

    public bool QueueUpgradeBatch(IEnumerable<int> turretIds, UpgradeTemplate template)
    {
        if (QueuedUpgradeBatch != null) return Reject("Cancel the current upgrade batch before queuing another.");
        if (!TryPlanUpgradeBatch(turretIds, template, spendNow: false, out var batch)) return false;
        batch!.GraceRemaining = CanAffordUpgradeBatch(batch) ? UpgradeBatchGraceSeconds : null;
        QueuedUpgradeBatch = batch;
        Message = batch.GraceRemaining.HasValue
            ? $"Batch affordable: {UpgradeBatchGraceSeconds:0} seconds to cancel before spending its component points."
            : "Queued upgrades; waiting for every required component type. No points deducted.";
        return true;
    }

    public bool CancelUpgradeBatch()
    {
        if (QueuedUpgradeBatch == null) return Reject("No upgrade batch is queued.");
        QueuedUpgradeBatch = null;
        Message = "Upgrade batch cancelled. No points deducted.";
        return true;
    }

    private bool TryPlanUpgradeBatch(IEnumerable<int> turretIds, UpgradeTemplate template, bool spendNow, out UpgradeBatchState? batch)
    {
        batch = null;
        if (!CanUseTowers()) return false;
        if (turretIds is null || template?.Points is null ||
            template.Points.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value < 0))
            return Reject("Choose valid towers and nonnegative upgrade counts.");
        int[] requestedIds = turretIds.ToArray();
        int[] ids = requestedIds.Distinct().Order().ToArray();
        if (ids.Length == 0) return Reject("Choose at least one tower for the batch.");
        if (ids.Length != requestedIds.Length) return Reject("A batch cannot repeat a tower.");
        var planned = new UpgradeBatchState();
        var remaining = Enum.GetValues<UpgradeKind>().ToDictionary(kind => kind,
            kind => (long)Economy.GetProgress(kind).Available);
        var byId = Turrets.Where(turret => turret.Hp > 0).ToDictionary(turret => turret.Id);
        foreach (int id in ids)
        {
            if (!byId.TryGetValue(id, out var turret)) return Reject("A chosen tower is no longer alive or deployed.");
            if (!float.IsFinite(turret.Hp) || turret.Hp > Economy.GetMaxHp(turret))
                return Reject("A chosen tower has invalid health.");
            var target = new UpgradeBatchTarget { TurretId = id };
            foreach (var kind in Enum.GetValues<UpgradeKind>())
            {
                target.Expected[kind] = Economy.GetAllocated(turret, kind);
                int requested = template.Points.GetValueOrDefault(kind);
                int capacity = Economy.GetUpgradeCapacity(turret, kind);
                int affordable = spendNow ? (int)Math.Min(int.MaxValue, remaining[kind]) : int.MaxValue;
                int added = Math.Min(requested, Math.Min(capacity, affordable));
                if (added <= 0) continue;
                target.Additions[kind] = added;
                planned.Costs[kind] = planned.Costs.GetValueOrDefault(kind) + added;
                if (spendNow) remaining[kind] -= added;
            }
            if (target.Additions.Count > 0) planned.Targets.Add(target);
        }
        if (planned.Cost == 0) return Reject("No requested upgrades fit the chosen towers and component balances.");
        batch = planned;
        return true;
    }

    private bool CanAffordUpgradeBatch(UpgradeBatchState batch) =>
        batch.Costs.All(pair => Economy.GetProgress(pair.Key).Available >= pair.Value);

    private bool IsUpgradeBatchValid(UpgradeBatchState batch)
    {
        var byId = Turrets.Where(turret => turret.Hp > 0).ToDictionary(turret => turret.Id);
        foreach (var target in batch.Targets)
        {
            if (!byId.TryGetValue(target.TurretId, out var turret) ||
                !float.IsFinite(turret.Hp) || turret.Hp > Economy.GetMaxHp(turret)) return false;
            foreach (var kind in Enum.GetValues<UpgradeKind>())
                if (Economy.GetAllocated(turret, kind) != target.Expected.GetValueOrDefault(kind)) return false;
            foreach (var (kind, addition) in target.Additions)
                if (addition <= 0 || Economy.GetUpgradeCapacity(turret, kind) < addition) return false;
        }
        return true;
    }

    private void CancelChangedUpgradeBatch()
    {
        if (QueuedUpgradeBatch is not { } batch ||
            (CanInteractWithTowers && IsUpgradeBatchValid(batch) &&
             (!batch.GraceRemaining.HasValue || CanAffordUpgradeBatch(batch)))) return;
        QueuedUpgradeBatch = null;
        Message += " Queued batch cancelled without charge because its target or component balance changed.";
    }

    private void CommitUpgradeBatch(UpgradeBatchState batch)
    {
        var byId = Turrets.ToDictionary(turret => turret.Id);
        if (!Economy.TrySpendUpgradeBatch(batch.Costs))
            throw new InvalidOperationException("Every required component type must be affordable before commitment.");
        foreach (var target in batch.Targets)
        {
            var turret = byId[target.TurretId];
            foreach (var (kind, addition) in target.Additions)
            {
                turret.UpgradePoints[kind] = target.Expected[kind] + addition;
                if (kind == UpgradeKind.HP) turret.Hp += (long)addition * Balance.HpUpgradePerPoint;
            }
        }
    }

    private void UpdateUpgradeBatch(float deltaSeconds)
    {
        var batch = QueuedUpgradeBatch;
        if (batch == null) return;
        if (!CanInteractWithTowers || !IsUpgradeBatchValid(batch))
        {
            QueuedUpgradeBatch = null;
            Message = "Queued upgrades cancelled because a target changed. No points deducted.";
            return;
        }
        if (!CanAffordUpgradeBatch(batch))
        {
            if (batch.GraceRemaining.HasValue)
            {
                QueuedUpgradeBatch = null;
                Message = "Queued upgrades cancelled because a required component became unaffordable. No batch points deducted.";
            }
            return;
        }
        if (!batch.GraceRemaining.HasValue)
        {
            batch.GraceRemaining = UpgradeBatchGraceSeconds;
            Message = "Upgrade batch affordable: 3 seconds to cancel.";
            return;
        }
        batch.GraceRemaining -= deltaSeconds;
        if (batch.GraceRemaining > 0) return;
        CommitUpgradeBatch(batch);
        QueuedUpgradeBatch = null;
        Message = $"Bought the complete upgrade batch with {batch.Cost} matching component points.";
    }
}
