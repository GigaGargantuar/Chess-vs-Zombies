using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    public const int MegaGuardLearningHits = 4;
    public const float MegaGuardWindupSeconds = .75f;
    private const float MegaGuardPredictionSeconds = .75f;

    private sealed class MegaGuardMemory(Zombie owner)
    {
        public Zombie Owner { get; } = owner;
        public int HitCount;
        public Vector3 IncomingSum;
        public float WindupRemaining = MegaGuardWindupSeconds;
    }

    private readonly Dictionary<int, MegaGuardMemory> _megaGuardMemory = [];
    private Dictionary<Zombie, List<MegaGuardThreat>>? _megaGuardPredictionBatch;
    private readonly record struct MegaGuardThreat(Vector3 From, Vector3 To, float BodyFraction);

    private MegaGuardMemory MegaGuardFor(Zombie mega)
    {
        if (!_megaGuardMemory.TryGetValue(mega.Id, out var memory) || memory.Owner != mega)
            _megaGuardMemory[mega.Id] = memory = new MegaGuardMemory(mega);
        return memory;
    }

    private Vector3 ObserveMegaIncomingFire(Zombie mega, Vector3 incoming)
    {
        if (mega.Size != ZombieSize.Mega || !ProjectilePointIsFinite(incoming)) return -incoming;
        var length = Math.Sqrt((double)incoming.X * incoming.X + (double)incoming.Y * incoming.Y + (double)incoming.Z * incoming.Z);
        if (length <= .000001) return -incoming;
        var memory = MegaGuardFor(mega);
        memory.IncomingSum -= new Vector3((float)(incoming.X / length), (float)(incoming.Y / length), (float)(incoming.Z / length));
        if (memory.HitCount < int.MaxValue) memory.HitCount++;
        // Each real hit contributes equally, regardless of projectile speed.
        return memory.HitCount >= MegaGuardLearningHits && memory.IncomingSum.LengthSquared() > .000001f
            ? Vector3.Normalize(memory.IncomingSum) : -incoming;
    }

    private void BeginMegaGuardWindup(Zombie mega) => MegaGuardFor(mega).WindupRemaining = MegaGuardWindupSeconds;

    private bool MegaGuardReadyToThrow(Zombie mega) => MegaGuardFor(mega).WindupRemaining <= .00001f;

    private void UpdateMegaGuardTimers(float dt)
    {
        foreach (var id in _megaGuardMemory.Keys.ToArray())
            if (!Zombies.Any(zombie => zombie.Id == id && zombie.Hp > 0 && _megaGuardMemory[id].Owner == zombie))
                _megaGuardMemory.Remove(id);
        foreach (var mega in Zombies)
            if (mega.Size == ZombieSize.Mega && mega.Hp > 0 && GetKiloConstructionBlocks(mega) == 4 && !IsKiloUsingBuckets(mega))
                MegaGuardFor(mega).WindupRemaining = Math.Max(0, MegaGuardFor(mega).WindupRemaining - dt);
    }

    private bool TryUpdateMegaPredictiveCover(Zombie mega, List<HeldKiloBlock> blocks)
    {
        if (IsKiloUsingBuckets(mega)) return true;
        if (!_megaGuardMemory.TryGetValue(mega.Id, out var memory) || memory.Owner != mega ||
            memory.HitCount < MegaGuardLearningHits) return false;

        var predictions = _megaGuardPredictionBatch ?? PredictMegaIncomingShots();
        IReadOnlyList<MegaGuardThreat> threats = predictions.TryGetValue(mega, out var predicted) ? predicted : [];
        var mean = memory.IncomingSum.LengthSquared() > .000001f ? Vector3.Normalize(memory.IncomingSum) : mega.Facing;
        Span<Vector3> candidate = stackalloc Vector3[4];
        Span<Vector3> best = stackalloc Vector3[4];
        bool found = false;
        int bestCount = -1;
        float bestAlignment = float.NegativeInfinity, bestMovement = float.PositiveInfinity;
        // Six faces and nine translations per face. Both hands move as connected
        // pairs; all four real cubes retain their identity and durability.
        for (int face = 0; face < 6; face++)
            for (int shiftA = -1; shiftA <= 1; shiftA++)
                for (int shiftB = -1; shiftB <= 1; shiftB++)
                {
                    MegaGuardBasis(face, out var normal, out var side, out var up);
                    for (int index = 0; index < 4; index++)
                        candidate[index] = normal + side * ((index % 2 == 0 ? -.5f : .5f) + shiftA * .5f) +
                            up * ((index / 2 == 0 ? -.5f : .5f) + shiftB * .5f);
                    int blocked = 0;
                    foreach (var threat in threats)
                    {
                        foreach (var block in blocks)
                        {
                            var center = mega.Position + candidate[block.BlockIndex];
                            if (ProjectileBoxIntersection(threat.From, threat.To - threat.From,
                                    center - new Vector3(.5f), center + new Vector3(.5f), out var hit) &&
                                hit <= threat.BodyFraction + .000001f)
                            { blocked++; break; }
                        }
                    }
                    float alignment = Vector3.Dot(normal, mean);
                    float movement = 0;
                    foreach (var block in blocks) movement += Vector3.DistanceSquared(block.Offset, candidate[block.BlockIndex]);
                    if (blocked < bestCount || blocked == bestCount &&
                        (alignment < bestAlignment - .000001f || Math.Abs(alignment - bestAlignment) <= .000001f && movement >= bestMovement)) continue;
                    if (!MegaGuardPlacementClear(mega, blocks, candidate)) continue;
                    found = true;
                    bestCount = blocked;
                    bestAlignment = alignment;
                    bestMovement = movement;
                    candidate.CopyTo(best);
                }
        if (found)
            foreach (var block in blocks) block.Offset = best[block.BlockIndex];
        // Keep the last physically valid learned pose if a new one is obstructed.
        return true;
    }

    private static void MegaGuardBasis(int face, out Vector3 normal, out Vector3 side, out Vector3 up)
    {
        normal = face switch
        {
            0 => Vector3.UnitX, 1 => -Vector3.UnitX,
            2 => Vector3.UnitZ, 3 => -Vector3.UnitZ,
            4 => Vector3.UnitY, _ => -Vector3.UnitY
        };
        side = face < 2 ? Vector3.UnitZ : Vector3.UnitX;
        up = face < 4 ? Vector3.UnitY : Vector3.UnitZ;
    }

    private bool MegaGuardPlacementClear(Zombie mega, List<HeldKiloBlock> blocks, ReadOnlySpan<Vector3> offsets)
    {
        foreach (var block in blocks)
        {
            var from = mega.Position + block.Offset;
            var to = mega.Position + offsets[block.BlockIndex];
            if (KiloBoxSweep(from, to, new Vector3(.49999f), out _)) return false;
            foreach (var ally in Zombies)
                if (ally.Id != mega.Id && ally.Hp > 0 && ProjectileBoxIntersection(from, to - from,
                    ally.Position - ally.BodyHalfExtents - new Vector3(.49999f),
                    ally.Position + ally.BodyHalfExtents + new Vector3(.49999f), out _)) return false;
        }
        return true;
    }

    private Dictionary<Zombie, List<MegaGuardThreat>> PredictMegaIncomingShots()
    {
        var threats = new Dictionary<Zombie, List<MegaGuardThreat>>();
        if (!Zombies.Any(mega => mega.Size == ZombieSize.Mega && mega.Hp > 0 && !IsKiloUsingBuckets(mega) &&
            GetKiloConstructionBlocks(mega) > 0 && _megaGuardMemory.TryGetValue(mega.Id, out var memory) &&
            memory.Owner == mega && memory.HitCount >= MegaGuardLearningHits)) return threats;
        foreach (var projectile in Projectiles)
        {
            if (projectile.Kind == ShotKind.Melee || projectile.Damage <= 0 || !float.IsFinite(projectile.Damage) ||
                !float.IsFinite(projectile.Age) || !float.IsFinite(projectile.Duration)) continue;
            int samples = projectile.Kind == ShotKind.Lob ? 15 : 1;
            var from = projectile.GetPositionAt(projectile.Age);
            for (int sample = 1; sample <= samples; sample++)
            {
                var to = projectile.GetPositionAt(projectile.Age + MegaGuardPredictionSeconds * sample / samples);
                if (!ProjectilePointIsFinite(from) || !ProjectilePointIsFinite(to)) break;
                // Use real trajectories and the same terrain/body ordering as
                // combat. A target ID alone never makes an occluded shot a threat.
                if (FindProjectileImpact(from, to, out var hit, out _, out var target, out _))
                {
                    if (target is { Size: ZombieSize.Mega } && (!TryFindKiloCoverImpact(from, to, out var coverHit, out var owner, out _) ||
                        owner == target.Id || coverHit > hit))
                    {
                        if (!threats.TryGetValue(target, out var incoming)) threats[target] = incoming = [];
                        incoming.Add(new MegaGuardThreat(from, to, hit));
                    }
                    break;
                }
                from = to;
            }
        }
        return threats;
    }
}
