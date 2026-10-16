namespace ChessVsZombies.Core;

/// <summary>Live tower identities selected individually or by additive, inclusive 3D boxes.</summary>
public sealed class TowerBatchSelection
{
    private readonly HashSet<int> _selectedIds = [];

    public IReadOnlySet<int> SelectedIds => _selectedIds;
    public Cell? FirstCorner { get; private set; }
    public Cell? SecondCorner { get; private set; }
    public bool IsChoosingSecondCorner => FirstCorner.HasValue && !SecondCorner.HasValue;

    /// <summary>Starts a box, then adds the towers between its opposite corners.</summary>
    public bool SelectCorner(Cell corner, IEnumerable<Turret> turrets)
    {
        if (!WorldGrid.IsInside(corner)) return false;
        ArgumentNullException.ThrowIfNull(turrets);
        if (!IsChoosingSecondCorner)
        {
            FirstCorner = corner;
            SecondCorner = null;
            return true;
        }

        SecondCorner = corner;
        var first = FirstCorner!.Value;
        int minX = Math.Min(first.X, corner.X), maxX = Math.Max(first.X, corner.X);
        int minY = Math.Min(first.Y, corner.Y), maxY = Math.Max(first.Y, corner.Y);
        int minZ = Math.Min(first.Z, corner.Z), maxZ = Math.Max(first.Z, corner.Z);
        foreach (var turret in turrets)
        {
            var cell = turret.Cell;
            if (cell.X >= minX && cell.X <= maxX && cell.Y >= minY && cell.Y <= maxY &&
                cell.Z >= minZ && cell.Z <= maxZ)
                Add(turret);
        }
        return true;
    }

    public bool Add(Turret turret) => IsLive(turret) && _selectedIds.Add(turret.Id);
    public bool Remove(Turret turret) => Remove(turret.Id);
    public bool Remove(int turretId) => _selectedIds.Remove(turretId);

    public void Clear()
    {
        _selectedIds.Clear();
        FirstCorner = null;
        SecondCorner = null;
    }

    /// <summary>Destroyed and removed identities leave the selection; replacements are distinct.</summary>
    public void Prune(IEnumerable<Turret> turrets)
    {
        var livingIds = turrets.Where(IsLive).Select(turret => turret.Id).ToHashSet();
        _selectedIds.IntersectWith(livingIds);
    }

    /// <summary>Resolves selected identities to their current towers, in deterministic spending order.</summary>
    public IReadOnlyList<Turret> GetTargets(IEnumerable<Turret> turrets) => turrets
        .Where(turret => IsLive(turret) && _selectedIds.Contains(turret.Id))
        .OrderBy(turret => turret.Id).ToArray();

    private static bool IsLive(Turret turret) => turret.Id > 0 && turret.Hp > 0 &&
        float.IsFinite(turret.Hp) && WorldGrid.IsInside(turret.Cell);
}
