using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private void TryInterceptKiloShot(Zombie protectedZombie, Vector3 from, Vector3 to, float bodyFraction)
    {
        var entry = Vector3.Lerp(from, to, bodyFraction);
        Zombie? selectedCarrier = null;
        HeldKiloBlock? selectedHand = null;
        Vector3 selectedOffset = default;
        float nearest = float.PositiveInfinity;
        foreach (var kilo in Zombies)
        {
            if (kilo.Hp <= 0 || kilo.Size != ZombieSize.Kilo || IsKiloUsingBuckets(kilo) ||
                !_kiloBlocks.TryGetValue(kilo.Id, out var blocks) || blocks.Count == 0) continue;
            // Clumped carriers share guard work, but each hand must still meet
            // the real ray inside its owner's reserved body volume. Distant
            // allies cannot lend an abstract shield or move a block across a gap.
            var separation = Vector3.Abs(kilo.Position - protectedZombie.Position);
            var combinedHalfExtents = kilo.BodyHalfExtents + new Vector3(protectedZombie.BodyScale * .5f);
            if (kilo != protectedZombie && (separation.X > combinedHalfExtents.X ||
                separation.Y > combinedHalfExtents.Y || separation.Z > combinedHalfExtents.Z)) continue;
            var desired = Vector3.Clamp(entry - kilo.Position, new Vector3(-.5f), new Vector3(.5f));
            foreach (var hand in blocks)
            {
                var center = kilo.Position + hand.Offset;
                var closest = Vector3.Clamp(entry, center - new Vector3(.5f), center + new Vector3(.5f));
                var distance = Vector3.DistanceSquared(entry, closest);
                if (distance > nearest + .000001f) continue;
                if (Math.Abs(distance - nearest) <= .000001f && selectedCarrier != null &&
                    (kilo.Id > selectedCarrier.Id || kilo.Id == selectedCarrier.Id && hand.BlockIndex >= selectedHand!.BlockIndex)) continue;
                var other = blocks.Count == 2 ? (blocks[0] == hand ? blocks[1] : blocks[0]) : null;
                if (!TryFindKiloHandPosition(kilo, hand, other, desired, from, to, bodyFraction, out var offset)) continue;
                nearest = distance;
                selectedCarrier = kilo;
                selectedHand = hand;
                selectedOffset = offset;
            }
        }
        // Resolve the whole group before moving exactly one physical hand.
        if (selectedHand != null) selectedHand.Offset = selectedOffset;
    }

    private static bool TryFindKiloHandPosition(Zombie kilo, HeldKiloBlock hand, HeldKiloBlock? other,
        Vector3 desired, Vector3 from, Vector3 to, float bodyFraction, out Vector3 offset)
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
        offset = hand.Offset;
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
        return float.IsFinite(best);
    }
}
