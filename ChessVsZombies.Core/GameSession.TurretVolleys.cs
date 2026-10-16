using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private readonly record struct TurretShotAim(Cell Destination, ShotKind Kind, Vector3 Aim);
    private readonly record struct TurretVolleyAttack(Zombie Target, ShotKind Kind, Vector3 Aim, float DistanceSquared);

    private List<TurretVolleyAttack> SelectTurretVolley(Turret turret)
    {
        var destinations = new Dictionary<Cell, TurretVolleyAttack>();
        var origin = turret.Cell.Center;
        foreach (var zombie in Zombies)
        {
            if (zombie.BodyHp <= 0) continue;
            if (zombie.IsGiant)
            {
                foreach (var candidate in EnumerateKiloTurretAims(turret, zombie))
                    Consider(zombie, candidate);
            }
            else if (TryGetTurretShotAndAim(turret, zombie, out var kind, out var aim))
                Consider(zombie, new TurretShotAim(zombie.Cell, kind, aim));
        }

        // A directional ray contains separate attack destinations. Each occupied
        // destination contributes once, even when it is also a configured jump.
        // Explicit order keeps simultaneous melee and projectile launches stable.
        return destinations.OrderBy(entry => entry.Key.X).ThenBy(entry => entry.Key.Y).ThenBy(entry => entry.Key.Z)
            .Select(entry => entry.Value).ToList();

        void Consider(Zombie zombie, TurretShotAim candidate)
        {
            if (candidate.Kind == ShotKind.Melee && !HasLineOfSight(origin, candidate.Aim)) return;
            float distance = Vector3.DistanceSquared(origin, candidate.Aim);
            if (destinations.TryGetValue(candidate.Destination, out var current) && distance >= current.DistanceSquared) return;
            destinations[candidate.Destination] = new TurretVolleyAttack(zombie, candidate.Kind, candidate.Aim, distance);
        }
    }
}
