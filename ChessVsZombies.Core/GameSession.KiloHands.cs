using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private void TryInterceptKiloShot(Zombie kilo, Vector3 from, Vector3 to, float bodyFraction)
    {
        if (IsKiloUsingBuckets(kilo) || !_kiloBlocks.TryGetValue(kilo.Id, out var blocks) || blocks.Count == 0) return;
        var entry = Vector3.Lerp(from, to, bodyFraction);
        float nearest = float.PositiveInfinity;
        foreach (var block in blocks)
        {
            var center = kilo.Position + block.Offset;
            // Resolve proximity when the bullet reaches the body, independent of
            // how far this frame's segment began from it.
            var closest = Vector3.Clamp(entry, center - new Vector3(.5f), center + new Vector3(.5f));
            var distance = Vector3.DistanceSquared(entry, closest);
            nearest = Math.Min(nearest, distance);
        }
        var desired = Vector3.Clamp(entry - kilo.Position, new Vector3(-.5f), new Vector3(.5f));
        foreach (var hand in blocks)
        {
            var center = kilo.Position + hand.Offset;
            var closest = Vector3.Clamp(entry, center - new Vector3(.5f), center + new Vector3(.5f));
            if (Vector3.DistanceSquared(entry, closest) > nearest + .000001f) continue;
            // Either equally close hand may catch a seam shot, provided its real
            // cube can reach the bullet without overlapping the other hand.
            var other = blocks.Count == 2 ? (blocks[0] == hand ? blocks[1] : blocks[0]) : null;
            if (TryPositionKiloHand(kilo, hand, other, desired, from, to, bodyFraction)) return;
        }
    }

    private static bool TryPositionKiloHand(Zombie kilo, HeldKiloBlock hand, HeldKiloBlock? other,
        Vector3 desired, Vector3 from, Vector3 to, float bodyFraction)
    {
        // Only this hand responds. The other hand retains its guard position;
        // candidate faces keep the two real full cubes from passing into each other.
        Span<Vector3> candidates = stackalloc Vector3[4];
        int count = 1;
        candidates[0] = desired;
        if (other != null)
        {
            if (Math.Abs(other.Offset.X) == .5f) candidates[count++] = desired with { X = -other.Offset.X };
            if (Math.Abs(other.Offset.Y) == .5f) candidates[count++] = desired with { Y = -other.Offset.Y };
            if (Math.Abs(other.Offset.Z) == .5f) candidates[count++] = desired with { Z = -other.Offset.Z };
        }
        float best = float.PositiveInfinity;
        var offset = hand.Offset;
        for (int i = 0; i < count; i++)
        {
            var candidate = candidates[i];
            if (other != null && KiloHandOffsetsOverlap(candidate, other.Offset)) continue;
            var center = kilo.Position + candidate;
            if (!ProjectileBoxIntersection(from, to - from, center - new Vector3(.5f), center + new Vector3(.5f), out var hit) ||
                hit > bodyFraction + .00001f) continue;
            var movement = Vector3.DistanceSquared(hand.Offset, candidate);
            if (movement >= best) continue;
            best = movement;
            offset = candidate;
        }
        hand.Offset = offset;
        return float.IsFinite(best);
    }
}
