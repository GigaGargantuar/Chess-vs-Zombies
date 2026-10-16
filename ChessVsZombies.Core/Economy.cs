using System.Collections.ObjectModel;

namespace ChessVsZombies.Core;

/// <summary>
/// Every pull banks one point of its type. Collection ranks only raise the per-turret
/// allocation caps; a deployed turret changes only when points are explicitly spent on it.
/// </summary>
public sealed class Economy
{
    private static readonly UpgradeKind[] UpgradeKinds = Enum.GetValues<UpgradeKind>();
    private readonly Dictionary<UpgradeKind, UpgradeProgress> _progress = [];
    private readonly ulong _initialRandomState;
    private ulong _randomState;

    public Economy(int? seed = null)
    {
        _initialRandomState = seed.HasValue ? (uint)seed.Value : (ulong)Random.Shared.NextInt64();
        _randomState = _initialRandomState;
        foreach (var kind in UpgradeKinds) _progress.Add(kind, new UpgradeProgress());
        Progress = new ReadOnlyDictionary<UpgradeKind, UpgradeProgress>(_progress);
    }

    public int Currency { get; private set; } = Balance.StartingCurrency;
    public string LastMessage { get; private set; } = "Build your defense, or pull upgrade components.";
    public IReadOnlyDictionary<UpgradeKind, UpgradeProgress> Progress { get; }

    public UpgradeProgress GetProgress(UpgradeKind kind) => _progress[kind];

    public int GetAllocationCap(UpgradeKind kind) => GetProgress(kind).Rank;

    public int GetAllocated(Turret turret, UpgradeKind kind) => turret.UpgradePoints.GetValueOrDefault(kind);

    /// <summary>Limits explicit Jump purchases to the enabled formation's unpaid cost.</summary>
    public int GetTemplatePointRequest(Turret turret, UpgradeTemplate template, UpgradeKind kind)
    {
        int requested = template.Points.GetValueOrDefault(kind);
        if (kind == UpgradeKind.Jump && template.UseJumpPattern &&
            GetJumpPatternPointCost(turret, template.JumpPattern) is { } cost)
            return Math.Min(requested, cost);
        return requested;
    }

    /// <summary>Already allocated points pay toward the whole pattern; unchanged patterns need no purchase.</summary>
    public int? GetJumpPatternPointCost(Turret turret, IReadOnlyList<Cell>? pattern)
    {
        if (pattern is null || pattern.Count == 0 || pattern.Distinct().Count() != pattern.Count ||
            pattern.Any(offset => !IsWithinJumpRange(offset, int.MaxValue))) return null;
        if (HasJump(turret) && turret.JumpPattern.Count == pattern.Count && turret.JumpPattern.ToHashSet().SetEquals(pattern)) return 0;
        return (int)Math.Max(0, JumpRules.FormationRadius(pattern) - Math.Max(0, GetAllocated(turret, UpgradeKind.Jump)));
    }

    public int GetUpgradeCapacity(Turret turret, UpgradeKind kind)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(turret.Kind) || IsInnatelyInfinite(turret.Kind, kind)) return 0;
        int existing = GetAllocated(turret, kind);
        return Math.Max(0, Math.Min(GetAllocationCap(kind), SupportedAllocationLimit(turret.Kind, kind)) - existing);
    }

    /// <summary>Checks every type before deducting any points from a batch purchase.</summary>
    public bool TrySpendUpgradeBatch(IReadOnlyDictionary<UpgradeKind, long> costs)
    {
        ArgumentNullException.ThrowIfNull(costs);
        if (costs.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value < 0 ||
            pair.Value > _progress[pair.Key].Available)) return false;
        foreach (var (kind, amount) in costs) _progress[kind].Available -= (int)amount;
        return true;
    }

    public EconomyState ExportState() => new()
    {
        Currency = Currency,
        RandomState = _randomState,
        Upgrades = _progress.ToDictionary(pair => pair.Key, pair => new SavedUpgradeProgress
        {
            PullCount = pair.Value.PullCount,
            Available = pair.Value.Available
        })
    };

    /// <summary>Validates the entire collection before replacing any live economy state.</summary>
    public void ImportState(EconomyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Currency < 0 || state.Upgrades is null || state.Upgrades.Count != UpgradeKinds.Length)
            throw new InvalidDataException("The saved economy needs a nonnegative wallet and all six component types.");
        foreach (var kind in UpgradeKinds)
        {
            if (!state.Upgrades.TryGetValue(kind, out var progress) || progress is null ||
                progress.PullCount < 0 || progress.Available < 0 || progress.Available > progress.PullCount)
                throw new InvalidDataException($"The saved {kind} component counts are invalid.");
        }

        Currency = state.Currency;
        _randomState = state.RandomState;
        foreach (var kind in UpgradeKinds)
        {
            _progress[kind].PullCount = state.Upgrades[kind].PullCount;
            _progress[kind].Available = state.Upgrades[kind].Available;
        }
        LastMessage = "Restored your currency and permanent component collection.";
    }

    public bool TrySpend(int amount)
    {
        if (amount < 0)
        {
            LastMessage = "The cost cannot be negative.";
            return false;
        }
        if (Currency < amount)
        {
            LastMessage = $"Not enough currency: need {amount}, have {Currency}.";
            return false;
        }
        Currency -= amount;
        return true;
    }

    public void Credit(int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        Currency = checked(Currency + amount);
    }

    public void Reset()
    {
        Currency = Balance.StartingCurrency;
        foreach (var progress in _progress.Values)
        {
            progress.PullCount = 0;
            progress.Available = 0;
        }
        _randomState = _initialRandomState;
        LastMessage = "New defense: 1000 currency.";
    }

    public UpgradeKind? Pull(IEnumerable<Turret>? deployed = null)
    {
        if (!TrySpend(Balance.PullCost)) return null;

        ulong previousRandomState = _randomState;
        var kind = UpgradeKinds[NextUpgradeIndex()];
        var progress = _progress[kind];
        if (progress.PullCount == int.MaxValue || progress.Available == int.MaxValue)
        {
            _randomState = previousRandomState;
            Credit(Balance.PullCost);
            LastMessage = "The component collection has reached its supported count limit.";
            return null;
        }
        int previousRank = progress.Rank;
        progress.PullCount++;
        progress.Available++;

        // The optional argument remains for call-site compatibility. Pulling must never
        // enumerate or modify deployed turrets, including when the allocation cap rises.
        LastMessage = progress.Rank > previousRank
            ? $"Pulled 1 {kind} point! Each tower can now receive up to {progress.Rank} {kind} points."
            : $"Pulled 1 {kind} point. {progress.Available} available; {progress.Rank} per-tower cap.";
        return kind;
    }

    public bool Apply(Turret turret, UpgradeKind kind) =>
        TryApplyTemplate(turret, new UpgradeTemplate { Points = new() { [kind] = 1 } }, out _);

    public bool TryApplyTemplate(Turret turret, UpgradeTemplate template, out string error)
    {
        ArgumentNullException.ThrowIfNull(turret);
        ArgumentNullException.ThrowIfNull(template);
        if (!float.IsFinite(turret.Hp) || turret.Hp <= 0)
            return Reject("Destroyed pieces cannot be upgraded.", out error);
        if (!Enum.IsDefined(turret.Kind) || template.Points is null)
            return Reject("The piece or upgrade template is invalid.", out error);
        if (template.Points.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value < 0))
            return Reject("Upgrade counts must be nonnegative and use known component types.", out error);
        if (turret.UpgradePoints.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value < 0))
            return Reject("This piece has invalid allocated upgrade data.", out error);
        foreach (var kind in UpgradeKinds)
        {
            var progress = _progress[kind];
            if (progress.PullCount < 0 || progress.Available < 0 || progress.Available > progress.PullCount)
                return Reject($"The {kind} component collection has invalid counts.", out error);
            var existing = GetAllocated(turret, kind);
            if (existing > GetAllocationCap(kind) || existing > SupportedAllocationLimit(turret.Kind, kind) ||
                (existing > 0 && IsInnatelyInfinite(turret.Kind, kind)))
                return Reject("This piece has allocated upgrade data outside its supported limits.", out error);
        }

        var projected = new Turret { Id = turret.Id, Kind = turret.Kind, Cell = turret.Cell, Hp = turret.Hp };
        var additions = new Dictionary<UpgradeKind, int>();
        var applied = new List<string>();
        var skipped = new List<string>();
        long spent = 0;
        foreach (var kind in UpgradeKinds)
        {
            int requested = GetTemplatePointRequest(turret, template, kind);
            int existing = GetAllocated(turret, kind);
            if (existing > 0) projected.UpgradePoints[kind] = existing;
            if (requested == 0) continue;
            if (IsInnatelyInfinite(turret.Kind, kind))
            {
                skipped.Add($"{kind}: {requested} skipped (already unlimited)");
                continue;
            }
            int available = _progress[kind].Available;
            int capSpace = GetAllocationCap(kind) - existing;
            int statSpace = SupportedAllocationLimit(turret.Kind, kind) - existing;
            int added = Math.Min(requested, Math.Min(available, Math.Min(capSpace, statSpace)));
            if (added > 0)
            {
                projected.UpgradePoints[kind] = existing + added;
                additions[kind] = added;
                applied.Add($"{kind} +{added}");
            }
            if (added < requested)
            {
                var reasons = new List<string>();
                if (available < requested) reasons.Add("inventory");
                if (capSpace < requested) reasons.Add("tower cap");
                if (statSpace < requested) reasons.Add("stat limit");
                skipped.Add($"{kind}: {requested - added} skipped ({string.Join(", ", reasons)})");
            }
            spent += added;
        }

        List<Cell>? nextPattern = null;
        if (template.UseJumpPattern)
        {
            if (template.JumpPattern is null)
                skipped.Add("Jump pattern skipped: no pattern was provided");
            else if (template.JumpPattern.Count == 0 || template.JumpPattern.Distinct().Count() != template.JumpPattern.Count ||
                template.JumpPattern.Any(offset => !IsWithinJumpRange(offset, int.MaxValue)))
                skipped.Add("Jump pattern skipped: destinations must be unique and at least two tiles from the origin");
            else
            {
                long radius = JumpRules.FormationRadius(template.JumpPattern);
                bool unchanged = HasJump(turret) && turret.JumpPattern.Count == template.JumpPattern.Count &&
                    turret.JumpPattern.ToHashSet().SetEquals(template.JumpPattern);
                int allocated = GetAllocated(projected, UpgradeKind.Jump);
                // Allocated Jump points already pay toward the radius. A smaller edited
                // formation does not return previously spent points.
                long shortfall = unchanged ? 0 : Math.Max(0, radius - allocated);
                int freePoints = _progress[UpgradeKind.Jump].Available - additions.GetValueOrDefault(UpgradeKind.Jump);
                int capSpace = GetAllocationCap(UpgradeKind.Jump) - allocated;
                int statSpace = SupportedAllocationLimit(turret.Kind, UpgradeKind.Jump) - allocated;
                if (shortfall > freePoints || shortfall > capSpace || shortfall > statSpace)
                    skipped.Add($"Jump pattern skipped: radius {radius} needs {shortfall} more matching Jump points within this tower's collection cap");
                else
                {
                    int extra = (int)shortfall;
                    if (extra > 0) projected.UpgradePoints[UpgradeKind.Jump] = allocated + extra;
                    if (!ValidateJumpPattern(projected, template.JumpPattern, out var candidate, out var patternError))
                    {
                        if (extra > 0) projected.UpgradePoints[UpgradeKind.Jump] = allocated;
                        skipped.Add($"Jump pattern skipped: {patternError}");
                    }
                    else
                    {
                        nextPattern = candidate;
                        if (extra > 0)
                        {
                            additions[UpgradeKind.Jump] = additions.GetValueOrDefault(UpgradeKind.Jump) + extra;
                            spent += extra;
                            applied.Add($"Jump formation +{extra}");
                        }
                    }
                }
            }
        }
        int previousMaxHp, nextMaxHp;
        try
        {
            previousMaxHp = GetMaxHp(turret);
            nextMaxHp = GetMaxHp(projected);
            _ = GetAttack(projected);
            foreach (var kind in UpgradeKinds) _ = GetRange(projected, kind);
        }
        catch (OverflowException)
        {
            return Reject("This template exceeds the supported stat or range limits.", out error);
        }
        if (turret.Hp > previousMaxHp)
            return Reject("This piece's current HP exceeds its maximum.", out error);
        if (nextPattern is null && HasJump(projected) &&
            !ValidateJumpPattern(projected, turret.JumpPattern, out _, out error)) return false;
        if (spent == 0 && nextPattern is null)
            return Reject(skipped.Count > 0 ? $"No upgrades applied. {string.Join("; ", skipped)}."
                : "Choose at least one upgrade point or enable a jump pattern in the template.", out error);

        // Compute the achievable projection first, then commit only those allocations.
        // Missing components remain unspent; an unusable pattern leaves the old offsets intact.
        foreach (var (kind, added) in additions)
        {
            turret.UpgradePoints[kind] = projected.UpgradePoints[kind];
            _progress[kind].Available -= added;
        }
        turret.Hp += nextMaxHp - previousMaxHp;
        if (nextPattern is not null)
        {
            turret.JumpPattern.Clear();
            turret.JumpPattern.AddRange(nextPattern);
        }
        error = string.Empty;
        if (nextPattern is not null) applied.Add("jump pattern updated");
        LastMessage = $"Applied to {turret.Kind} #{turret.Id}: {string.Join(", ", applied)}. {spent} points spent." +
            (skipped.Count > 0 ? $" {string.Join("; ", skipped)}." : string.Empty);
        return true;
    }

    private static int SupportedAllocationLimit(PieceKind piece, UpgradeKind kind) => kind switch
    {
        UpgradeKind.HP => (int.MaxValue - Balance.TurretHp) / Balance.HpUpgradePerPoint,
        UpgradeKind.ATK => (int.MaxValue - Balance.BaseAttack) / Balance.AtkUpgradePerPoint,
        UpgradeKind.Jump => piece == PieceKind.Knight ? (int.MaxValue - 24) / 4 : (int.MaxValue - 8) / 4 + 1,
        UpgradeKind.Orthogonal when piece == PieceKind.Pawn => int.MaxValue - 1,
        _ => int.MaxValue
    };

    public int GetMaxHp(Turret turret) => checked(Balance.TurretHp + GetAllocated(turret, UpgradeKind.HP) * Balance.HpUpgradePerPoint);

    public int GetAttack(Turret turret) => checked(Balance.BaseAttack + GetAllocated(turret, UpgradeKind.ATK) * Balance.AtkUpgradePerPoint);

    /// <summary>Returns zero for directions this turret has not acquired.</summary>
    public int GetRange(Turret turret, UpgradeKind kind)
    {
        if (kind is UpgradeKind.HP or UpgradeKind.ATK) return 0;
        if (IsInnatelyInfinite(turret.Kind, kind)) return int.MaxValue;
        int points = GetAllocated(turret, kind);
        int innateRange = (turret.Kind, kind) switch
        {
            (PieceKind.Pawn, UpgradeKind.Orthogonal) => 1,
            (PieceKind.Knight, UpgradeKind.Jump) => 2,
            (_, UpgradeKind.Jump) when points > 0 => 1,
            _ => 0
        };
        return checked(innateRange + points);
    }

    public bool HasJump(Turret turret) => GetRange(turret, UpgradeKind.Jump) >= 2;

    private static bool IsInnatelyInfinite(PieceKind piece, UpgradeKind kind) =>
        (piece == PieceKind.Rook && kind == UpgradeKind.Orthogonal) ||
        (piece == PieceKind.Bishop && kind == UpgradeKind.PlaneDiagonal);

    /// <summary>
    /// Tests a particular destination rather than treating every attack from a jump-capable
    /// piece as a lob. A destination may belong to both a ray and a configured jump.
    /// </summary>
    public bool IsJumpOffset(Turret turret, Cell offset) =>
        IsJumpOffsetAtRange(turret, offset, GetRange(turret, UpgradeKind.Jump));

    internal bool IsJumpOffsetAtRange(Turret turret, Cell offset, int range) =>
        IsWithinJumpRange(offset, range) && turret.JumpPattern.Contains(offset);

    public bool IsDirectionalOffset(Turret turret, Cell offset) =>
        TryGetDirectionalOffset(offset, out var kind, out long distance) && distance <= GetRange(turret, kind);

    internal bool TryGetDirectionalOffset(Cell offset, out UpgradeKind kind, out long distance)
    {
        kind = UpgradeKind.Orthogonal;
        // Use long here so arbitrary editor or future save-file values cannot overflow Abs.
        long x = Math.Abs((long)offset.X), y = Math.Abs((long)offset.Y), z = Math.Abs((long)offset.Z);
        distance = Math.Max(x, Math.Max(y, z));
        if (distance == 0 || (x != 0 && x != distance) || (y != 0 && y != distance) ||
            (z != 0 && z != distance)) return false;
        int axes = (x == 0 ? 0 : 1) + (y == 0 ? 0 : 1) + (z == 0 ? 0 : 1);
        kind = axes switch
        {
            1 => UpgradeKind.Orthogonal,
            2 => UpgradeKind.PlaneDiagonal,
            _ => UpgradeKind.SpaceDiagonal
        };
        return true;
    }

    /// <summary>
    /// Jump offsets are explicit and do not consume a point for each selected destination.
    /// Intermediate cells do not belong to a jump's attack pattern.
    /// </summary>
    public bool TrySetJumpPattern(Turret turret, IEnumerable<Cell> offsets, out string error)
    {
        ArgumentNullException.ThrowIfNull(turret);
        ArgumentNullException.ThrowIfNull(offsets);
        return TryApplyTemplate(turret, new UpgradeTemplate { UseJumpPattern = true, JumpPattern = offsets.ToList() }, out error);
    }

    private bool ValidateJumpPattern(Turret turret, IEnumerable<Cell> offsets, out List<Cell> pattern, out string error)
    {
        pattern = [];
        if (!HasJump(turret))
            return Reject("Allocate a jump point before applying a jump pattern to this piece.", out error);

        int range = GetRange(turret, UpgradeKind.Jump);
        var unique = new HashSet<Cell>();
        foreach (var cell in offsets)
        {
            if (!IsWithinJumpRange(cell, range))
                return Reject($"Jump offsets must have a distance of 2 to {range} tiles on their longest axis.", out error);
            if (!unique.Add(cell))
                return Reject("Each jump destination must be unique.", out error);
            pattern.Add(cell);
        }
        if (pattern.Count == 0)
            return Reject("Keep at least one jump destination in the pattern.", out error);

        error = string.Empty;
        return true;
    }

    public IReadOnlyList<Cell> GetAttackPattern(Turret turret)
    {
        var pattern = new HashSet<Cell>();
        AddRays(pattern, GetRange(turret, UpgradeKind.Orthogonal), nonzeroAxes: 1);
        AddRays(pattern, GetRange(turret, UpgradeKind.PlaneDiagonal), nonzeroAxes: 2);
        AddRays(pattern, GetRange(turret, UpgradeKind.SpaceDiagonal), nonzeroAxes: 3);

        int jumpRange = GetRange(turret, UpgradeKind.Jump);
        if (jumpRange > 0)
            foreach (var offset in turret.JumpPattern)
                if (IsWithinJumpRange(offset, jumpRange)) pattern.Add(offset);

        return pattern.OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToArray();
    }

    private static bool IsWithinJumpRange(Cell offset, int range) => range >= 2 &&
        offset.X >= -range && offset.X <= range && offset.Y >= -range && offset.Y <= range &&
        offset.Z >= -range && offset.Z <= range && offset.Chebyshev >= 2;

    private static void AddRays(HashSet<Cell> pattern, int range, int nonzeroAxes)
    {
        // Infinite sliders need only the offsets that can occur within the 32-cube board.
        range = Math.Min(range, WorldGrid.Size - 1);
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                {
                    if ((x == 0 ? 0 : 1) + (y == 0 ? 0 : 1) + (z == 0 ? 0 : 1) != nonzeroAxes) continue;
                    for (int distance = 1; distance <= range; distance++)
                        pattern.Add(new Cell(x * distance, y * distance, z * distance));
                }
    }

    private bool Reject(string message, out string error)
    {
        error = LastMessage = message;
        return false;
    }

    private int NextUpgradeIndex()
    {
        // SplitMix64 provides a compact, portable state for exact save/resume behavior.
        // Rejection removes modulo bias when selecting among the six component types.
        ulong count = (ulong)UpgradeKinds.Length;
        ulong threshold = unchecked(0UL - count) % count;
        while (true)
        {
            _randomState = unchecked(_randomState + 0x9E3779B97F4A7C15UL);
            ulong value = _randomState;
            value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
            value ^= value >> 31;
            if (value >= threshold) return (int)(value % count);
        }
    }
}
