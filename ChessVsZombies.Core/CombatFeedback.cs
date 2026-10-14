using System.Numerics;

namespace ChessVsZombies.Core;

/// <summary>A transient damage label anchored where the hit was received.</summary>
public sealed record DamageHit(long Id, int ZombieId, Vector3 Position, float Amount, DamageKind Kind, float Age);

/// <summary>Rendering-independent hit flashes and short-lived, aggregated damage labels.</summary>
public sealed class CombatFeedback
{
    public const float FlashDuration = .15f;
    public const float NumberDuration = 1f;
    public const float ContinuousAggregationWindow = .2f;
    private readonly List<DamageHit> _hits = [];
    private readonly Dictionary<int, float> _flashes = [];
    private long _nextId = 1;

    public CombatFeedback() => Hits = _hits.AsReadOnly();

    public IReadOnlyList<DamageHit> Hits { get; }

    /// <summary>White-flash intensity, refreshed by each impact or new continuous-damage bucket.</summary>
    public float GetFlash(int zombieId) => _flashes.TryGetValue(zombieId, out var remaining)
        ? Math.Clamp(remaining / FlashDuration, 0, 1) : 0;

    internal void Record(int zombieId, Vector3 position, float amount, DamageKind kind)
    {
        if (!float.IsFinite(amount) || amount <= 0 || !Enum.IsDefined(kind) ||
            !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return;
        // Only damage-over-time families aggregate. Distinct physical impacts retain
        // their own labels, while a continuous source opens and flashes a fresh
        // bucket every .2s instead of holding the body white on every substep.
        if (kind is DamageKind.Chemical or DamageKind.Thermal)
        {
            var existing = _hits.FindLastIndex(hit => hit.ZombieId == zombieId && hit.Kind == kind &&
                hit.Age < ContinuousAggregationWindow - .00001f);
            if (existing >= 0)
            {
                var hit = _hits[existing];
                _hits[existing] = hit with { Amount = hit.Amount + amount };
                return;
            }
        }
        _flashes[zombieId] = FlashDuration;
        _hits.Add(new DamageHit(_nextId++, zombieId, position, amount, kind, 0));
    }

    internal void Update(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            var age = _hits[i].Age + elapsedSeconds;
            if (age >= NumberDuration - .00001f) _hits.RemoveAt(i);
            else _hits[i] = _hits[i] with { Age = age };
        }
        foreach (var id in _flashes.Keys.ToArray())
        {
            var remaining = _flashes[id] - elapsedSeconds;
            if (remaining <= .00001f) _flashes.Remove(id);
            else _flashes[id] = remaining;
        }
    }

    internal void Reset()
    {
        _hits.Clear();
        _flashes.Clear();
        _nextId = 1;
    }
}
