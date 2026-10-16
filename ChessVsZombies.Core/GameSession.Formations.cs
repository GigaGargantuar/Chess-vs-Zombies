using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace ChessVsZombies.Core;

/// <summary>An independent snapshot of a tower's location and purchased configuration.</summary>
public sealed class FormationTower
{
    public PieceKind Kind { get; }
    public Cell Cell { get; }
    public IReadOnlyDictionary<UpgradeKind, int> UpgradePoints { get; }
    public IReadOnlyList<Cell> JumpPattern { get; }

    internal FormationTower(PieceKind kind, Cell cell, IDictionary<UpgradeKind, int> points, IEnumerable<Cell> jumpPattern)
    {
        Kind = kind;
        Cell = cell;
        UpgradePoints = new ReadOnlyDictionary<UpgradeKind, int>(new Dictionary<UpgradeKind, int>(points));
        JumpPattern = Array.AsReadOnly(jumpPattern.ToArray());
    }

    internal Turret CreateTurret(int id = 0)
    {
        var turret = new Turret { Id = id, Kind = Kind, Cell = Cell };
        foreach (var (kind, points) in UpgradePoints) turret.UpgradePoints.Add(kind, points);
        turret.JumpPattern.Clear();
        turret.JumpPattern.AddRange(JumpPattern);
        return turret;
    }
}

/// <summary>A saved formation survives removal, destruction, and movement of its original towers.</summary>
public sealed class FormationTemplate
{
    public IReadOnlyList<FormationTower> Towers { get; }

    private FormationTemplate(IEnumerable<FormationTower> towers) => Towers = Array.AsReadOnly(towers
        .OrderBy(tower => tower.Cell.Y).ThenBy(tower => tower.Cell.X).ThenBy(tower => tower.Cell.Z).ToArray());

    internal static FormationTemplate FromSaved(SavedFormationTemplate saved)
    {
        if (saved.Towers == null || saved.Towers.Count == 0 || saved.Towers.Count > 30 * 31 * 30)
            throw new InvalidDataException("A saved formation needs towers within the battlefield's capacity.");
        var cells = new HashSet<Cell>();
        var economy = new Economy();
        var towers = new List<FormationTower>();
        foreach (var tower in saved.Towers)
        {
            if (tower == null || !Enum.IsDefined(tower.Kind) || tower.Cell.X is < 1 or > 30 ||
                tower.Cell.Y is < 0 or > 30 || tower.Cell.Z is < 1 or > 30 || WorldGrid.IsCore(tower.Cell) ||
                !cells.Add(tower.Cell) || tower.UpgradePoints == null || tower.JumpPattern == null ||
                tower.UpgradePoints.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value <= 0 ||
                    tower.Kind == PieceKind.Rook && pair.Key == UpgradeKind.Orthogonal ||
                    tower.Kind == PieceKind.Bishop && pair.Key == UpgradeKind.PlaneDiagonal))
                throw new InvalidDataException("A formation tower has invalid or duplicate placement or upgrade data.");
            var snapshot = new FormationTower(tower.Kind, tower.Cell, tower.UpgradePoints, tower.JumpPattern);
            var projected = snapshot.CreateTurret();
            // The template is independent of today's economy and terrain. Check only
            // representable stats and the saved tower's own configured jump range.
            _ = economy.GetMaxHp(projected);
            _ = economy.GetAttack(projected);
            foreach (var kind in new[] { UpgradeKind.Orthogonal, UpgradeKind.PlaneDiagonal, UpgradeKind.SpaceDiagonal })
                _ = economy.GetRange(projected, kind);
            int jumpRange = economy.HasJump(projected) ? economy.GetRange(projected, UpgradeKind.Jump) : 2;
            if (tower.JumpPattern.Count == 0 || tower.JumpPattern.Distinct().Count() != tower.JumpPattern.Count ||
                tower.JumpPattern.Any(offset => JumpDistance(offset) < 2 || JumpDistance(offset) > jumpRange))
                throw new InvalidDataException("A formation tower has an invalid jump pattern.");
            towers.Add(snapshot);
        }
        return new FormationTemplate(towers);
    }

    private static long JumpDistance(Cell offset) => Math.Max(Math.Abs((long)offset.X),
        Math.Max(Math.Abs((long)offset.Y), Math.Abs((long)offset.Z)));
}

public sealed class SavedFormationTower
{
    [JsonRequired] public PieceKind Kind { get; set; }
    [JsonRequired] public Cell Cell { get; set; }
    [JsonRequired] public Dictionary<UpgradeKind, int> UpgradePoints { get; set; } = [];
    [JsonRequired] public List<Cell> JumpPattern { get; set; } = [];
}

public sealed class SavedFormationTemplate
{
    [JsonRequired] public List<SavedFormationTower> Towers { get; set; } = [];

    internal static SavedFormationTemplate FromTemplate(FormationTemplate template) => new()
    {
        Towers = template.Towers.Select(tower => new SavedFormationTower
        {
            Kind = tower.Kind,
            Cell = tower.Cell,
            UpgradePoints = new Dictionary<UpgradeKind, int>(tower.UpgradePoints),
            JumpPattern = tower.JumpPattern.ToList()
        }).ToList()
    };
}

public sealed partial class GameSession
{
    public const float FormationRestoreIntervalSeconds = .35f;
    private float _formationRestoreElapsed;
    private Func<Cell, bool>? _formationPlacementAllowed;

    public FormationTemplate? SavedFormation { get; private set; }
    public bool IsAutoRestoringFormation { get; private set; }
    public string FormationRestoreStatus { get; private set; } = "Save a completed formation to show its missing towers.";

    /// <summary>Existing living pieces of the saved kind are retained, including their current damage and upgrades.</summary>
    public IReadOnlyList<FormationTower> MissingFormationTurrets
    {
        get
        {
            if (SavedFormation == null) return Array.Empty<FormationTower>();
            var present = Turrets.Where(tower => tower.Hp > 0).Select(tower => (tower.Cell, tower.Kind)).ToHashSet();
            return SavedFormation.Towers.Where(tower => !present.Contains((tower.Cell, tower.Kind))).ToArray();
        }
    }

    public int MissingFormationCount => MissingFormationTurrets.Count;
    public long MissingFormationCurrencyCost => MissingFormationTurrets.Sum(tower => (long)Balance.PieceCost(tower.Kind));
    public IReadOnlyDictionary<UpgradeKind, long> MissingFormationUpgradeCosts
    {
        get
        {
            var costs = new Dictionary<UpgradeKind, long>();
            foreach (var tower in MissingFormationTurrets)
                foreach (var (kind, points) in tower.UpgradePoints)
                    costs[kind] = costs.GetValueOrDefault(kind) + points;
            return costs;
        }
    }

    public bool SaveFormation()
    {
        if (!CanUseTowers()) return false;
        var saved = new SavedFormationTemplate
        {
            Towers = Turrets.Where(tower => tower.Hp > 0).Select(tower => new SavedFormationTower
            {
                Kind = tower.Kind,
                Cell = tower.Cell,
                UpgradePoints = new Dictionary<UpgradeKind, int>(tower.UpgradePoints),
                JumpPattern = tower.JumpPattern.ToList()
            }).ToList()
        };
        if (saved.Towers.Count == 0) return Reject("Deploy at least one living tower before saving a formation.");
        try { SavedFormation = FormationTemplate.FromSaved(saved); }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        { return Reject($"Could not save formation: {error.Message}"); }
        IsAutoRestoringFormation = false;
        _formationRestoreElapsed = 0;
        _formationPlacementAllowed = null;
        FormationRestoreStatus = $"Saved formation: {SavedFormation.Towers.Count} towers.";
        Message = FormationRestoreStatus;
        return true;
    }

    public bool StartAutoRestoreFormation(Func<Cell, bool>? placementAllowed = null)
    {
        if (!CanUseTowers()) return false;
        if (SavedFormation == null) return Reject("Save a turret formation before starting auto restore.");
        if (MissingFormationCount == 0) return Reject("All saved formation towers are already present.");
        IsAutoRestoringFormation = true;
        _formationRestoreElapsed = 0;
        _formationPlacementAllowed = placementAllowed;
        FormationRestoreStatus = "Auto restore started; constructing saved towers one at a time.";
        Message = FormationRestoreStatus;
        return true;
    }

    public bool CancelAutoRestoreFormation()
    {
        if (!IsAutoRestoringFormation) return Reject("Auto restore is not running.");
        IsAutoRestoringFormation = false;
        _formationRestoreElapsed = 0;
        _formationPlacementAllowed = null;
        FormationRestoreStatus = "Auto restore stopped. Remaining highlights are saved.";
        Message = FormationRestoreStatus;
        return true;
    }

    internal void RestoreFormationTemplate(FormationTemplate? template)
    {
        SavedFormation = template;
        IsAutoRestoringFormation = false;
        _formationRestoreElapsed = 0;
        _formationPlacementAllowed = null;
        FormationRestoreStatus = template == null ? "Save a completed formation to show its missing towers."
            : $"Saved formation: {template.Towers.Count} towers. Auto restore is stopped.";
    }

    private void UpdateFormationRestore(float deltaSeconds)
    {
        if (!IsAutoRestoringFormation) return;
        if (!CanInteractWithTowers)
        {
            IsAutoRestoringFormation = false;
            _formationPlacementAllowed = null;
            FormationRestoreStatus = "Auto restore stopped after defeat. The formation remains saved.";
            return;
        }
        var missing = MissingFormationTurrets;
        if (missing.Count == 0)
        {
            CompleteFormationRestore();
            return;
        }
        _formationRestoreElapsed = Math.Min(FormationRestoreIntervalSeconds, _formationRestoreElapsed + deltaSeconds);
        if (_formationRestoreElapsed < FormationRestoreIntervalSeconds) return;
        _formationRestoreElapsed = 0;
        string? waitReason = null;
        foreach (var tower in missing)
        {
            string? error = DeployPlacementError(tower.Kind, tower.Cell, prepareTerrain: true);
            if (error == null && _formationPlacementAllowed?.Invoke(tower.Cell) == false)
                error = "Move away from this saved placement cell first.";
            if (error == null && tower.UpgradePoints.Count > 0)
            {
                var unupgraded = new Turret { Kind = tower.Kind };
                foreach (var (kind, points) in tower.UpgradePoints)
                {
                    if (Economy.GetUpgradeCapacity(unupgraded, kind) < points)
                    { error = $"The saved {tower.Kind} needs a higher {kind} collection cap."; break; }
                    if (Economy.GetProgress(kind).Available < points)
                    { error = $"Need {points} {kind} component points for the saved {tower.Kind}."; break; }
                }
            }
            if (error != null)
            {
                waitReason ??= $"Waiting at {tower.Cell}: {error}";
                continue;
            }
            if (!TryDeployFormationTower(tower, out error))
            {
                waitReason ??= $"Waiting at {tower.Cell}: {error}";
                continue;
            }
            var restored = Turrets[^1];
            if (!Economy.TrySpendUpgradeBatch(tower.UpgradePoints.ToDictionary(pair => pair.Key, pair => (long)pair.Value)))
                throw new InvalidOperationException("Formation components changed after affordability was checked.");
            foreach (var (kind, points) in tower.UpgradePoints) restored.UpgradePoints.Add(kind, points);
            restored.JumpPattern.Clear();
            restored.JumpPattern.AddRange(tower.JumpPattern);
            restored.Hp = Economy.GetMaxHp(restored);
            if (_simulationTurretLookupActive) _simulationTurretsByCell.TryAdd(restored.Cell, restored);
            SettleDefenses();
            CancelChangedUpgradeBatch();
            if (missing.Count == 1) CompleteFormationRestore();
            else FormationRestoreStatus = $"Restored {tower.Kind} at {tower.Cell}; {missing.Count - 1} towers remaining.";
            return;
        }
        FormationRestoreStatus = waitReason ?? "Waiting for a saved tower location to become available.";
    }

    private void CompleteFormationRestore()
    {
        IsAutoRestoringFormation = false;
        _formationRestoreElapsed = 0;
        _formationPlacementAllowed = null;
        FormationRestoreStatus = "Formation restored. All saved towers are present.";
        Message = FormationRestoreStatus;
    }
}
