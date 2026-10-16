using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private readonly Dictionary<int, Vector3> _pillarDepartures = [];

    private static float ZombieFallDamage(float startHeight, float landingHeight)
    {
        float blocks = MathF.Floor(startHeight - landingHeight + .0001f);
        return blocks < Balance.FallDamageMinimumBlocks ? 0 :
            (blocks - Balance.FallDamageMinimumBlocks + 1) * Balance.FallDamagePerBlock;
    }

    private bool CanEscapePillar(Zombie zombie) =>
        TryFindPillarDeparture(zombie, out _, out bool survivable) &&
        (survivable || TryGetPillarDigTarget(zombie, out _));

    private bool TryEscapePillar(Zombie zombie)
    {
        if (!TryFindPillarDeparture(zombie, out var departure, out bool survivable)) return false;
        if (survivable)
        {
            ClearZombieMovement(zombie);
            _pillarDepartures[zombie.Id] = departure;
            zombie.FaceToward(departure - zombie.Position);
            return true;
        }
        // Lower the real support at the ordinary attack cadence, then let the
        // normal fall and route selection run again. No terrain or inventory is
        // created, and removing the last floor must itself be survivable.
        return TryGetPillarDigTarget(zombie, out var floor) && AttackObstacle(zombie, floor);
    }

    private bool TryFindPillarDeparture(Zombie zombie, out Vector3 departure, out bool survivable)
    {
        departure = default;
        survivable = false;
        if (!HasSolidFooting(zombie.Position, zombie) ||
            Vector3.DistanceSquared(zombie.Position, zombie.StandAt(zombie.Cell)) > .000001f) return false;
        bool found = false;
        float bestDistance = float.PositiveInfinity;
        bool retreating = IsZombieRetreating(zombie);
        foreach (var direction in Horizontal)
            for (int distance = 1; distance <= zombie.GridFootprint; distance++)
            {
                var candidate = zombie.Position + new Vector3(direction.X, 0, direction.Z) * distance;
                if (!TryPillarLanding(zombie, candidate, out var landing) || zombie.Position.Y - landing.Y < 1.999f ||
                    TryFindBodyObstacle(zombie.Position, candidate, out _, zombie: zombie)) continue;
                var cell = zombie.CellAt(landing);
                // A lower isolated shelf is not an exit. Keep its sound support
                // while waiting for another carrier rather than drilling forever.
                if (retreating)
                {
                    TryGetRetreatStep(zombie, cell, out var outward);
                    if (outward == cell && !AtSupplyBoundary(zombie, landing)) continue;
                }
                else if (!IsCoreApproach(cell, zombie) &&
                    !(zombie.Size == ZombieSize.Standard && _routes[zombie.Lane % 4].ContainsKey(cell)) &&
                    !TryGetSizedRoute(zombie, cell, out _, includeFallback: false)) continue;
                bool safe = ZombieFallDamage(zombie.Position.Y, landing.Y) < zombie.BodyHp;
                float rank = retreating ? RetreatEdgeDistance(zombie, landing) : Vector3.DistanceSquared(landing, new Vector3(16));
                if (found && (survivable && !safe || survivable == safe && rank >= bestDistance)) continue;
                found = true;
                survivable = safe;
                bestDistance = rank;
                departure = candidate;
            }
        return found;
    }

    private bool TryPillarLanding(Zombie zombie, Vector3 departure, out Vector3 landing)
    {
        landing = default;
        if (!IsBodyClear(departure, zombie) || !TrySolidFloor(departure, out float floor, zombie)) return false;
        landing = new Vector3(departure.X, floor, departure.Z);
        return IsBodyClear(landing, zombie) &&
            !TryFindBodyObstacle(departure, landing, out _, zombie: zombie);
    }

    private bool TryGetPillarDigTarget(Zombie zombie, out Cell floor)
    {
        floor = default;
        if (zombie.Attack <= 0 || !TryFindBodyObstacle(zombie.Position, zombie.Position - Vector3.UnitY, out floor, zombie: zombie) ||
            World.GetMaterial(floor) == Material.Core ||
            !TrySolidFloorIgnoringCell(zombie.Position, out float lowerHeight, zombie, floor)) return false;
        return ZombieFallDamage(zombie.Position.Y, lowerHeight) < zombie.BodyHp;
    }

    private bool TryUpdatePillarDeparture(Zombie zombie, float dt)
    {
        if (!_pillarDepartures.TryGetValue(zombie.Id, out var departure)) return false;
        // Health and geometry can change during the crossing. Recheck before
        // each physical move; an interrupted unsupported body falls where it is.
        if (!TryPillarLanding(zombie, departure, out var landing) ||
            ZombieFallDamage(zombie.Position.Y, landing.Y) >= zombie.BodyHp ||
            TryFindBodyObstacle(zombie.Position, departure, out _, zombie: zombie))
        {
            ClearZombieMovement(zombie);
            return false;
        }
        float remaining = dt * zombie.MovementSpeed;
        if (MoveToward(zombie, departure, ref remaining))
        {
            ClearZombieMovement(zombie);
            _fallStartHeights.TryAdd(zombie.Id, zombie.Position.Y);
        }
        return true;
    }
}
