using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private void UpdateProjectiles(float dt)
    {
        for (var i = Projectiles.Count - 1; i >= 0; i--)
        {
            var projectile = Projectiles[i];
            var startAge = projectile.Age;
            projectile.Age += dt;
            if (projectile.Kind == ShotKind.Melee)
            {
                if (projectile.Age >= projectile.Duration) Projectiles.RemoveAt(i);
                continue;
            }
            if (!float.IsFinite(projectile.Age) || !float.IsFinite(projectile.Duration) ||
                !ProjectilePointIsFinite(projectile.Origin) || !ProjectilePointIsFinite(projectile.Target))
            {
                Projectiles.RemoveAt(i);
                continue;
            }

            // Pellets use one exact segment. Lobs sample their continuing parabola,
            // with short exact sweeps between samples so thin crust is never skipped.
            var speedBound = Math.Max(projectile.GetTravelDirection(startAge).Length(),
                projectile.GetTravelDirection(projectile.Age).Length()) / projectile.FlightDuration;
            if (!float.IsFinite(speedBound) || speedBound <= .000001f)
            {
                Projectiles.RemoveAt(i);
                continue;
            }
            int steps = projectile.Kind == ShotKind.Lob ? Math.Max(1, (int)MathF.Ceiling(speedBound * dt / .1f)) : 1;
            bool stopped = false;
            var from = projectile.GetPositionAt(startAge);
            for (int sample = 1; sample <= steps && !stopped; sample++)
            {
                var endAge = startAge + dt * sample / steps;
                var to = projectile.GetPositionAt(endAge);
                if (!ProjectilePointIsFinite(from) || !ProjectilePointIsFinite(to)) { stopped = true; break; }
                bool hitSomething = FindProjectileImpact(from, to, out var fraction, out var terrain, out var zombie, out var crustFace);
                if (zombie is { Size: ZombieSize.Kilo }) TryInterceptKiloShot(zombie, from, to, fraction);
                if (TryFindKiloCoverImpact(from, to, out var coverFraction, out var carrierId, out var blockIndex) && coverFraction <= fraction)
                {
                    DamageKiloCover(carrierId, blockIndex, projectile.Damage);
                    var carrier = Zombies.FirstOrDefault(candidate => candidate.Id == carrierId && candidate.Hp > 0);
                    if (carrier != null)
                    {
                        var previousAge = startAge + dt * (sample - 1) / steps;
                        var hitAge = previousAge + (endAge - previousAge) * coverFraction;
                        FaceKiloTowardShot(carrier, projectile.GetTravelDirection(hitAge));
                    }
                    stopped = true;
                }
                else if (hitSomething)
                {
                    var previousAge = startAge + dt * (sample - 1) / steps;
                    var hitAge = previousAge + (endAge - previousAge) * fraction;
                    if (zombie != null)
                        DamageZombie(zombie, projectile.Damage, projectile.DamageKind, projectile.Kind, projectile.GetTravelDirection(hitAge));
                    else if (terrain.HasValue)
                        DamageProjectileTerrain(terrain.Value, projectile.Damage, crustFace);
                    stopped = true;
                }
                from = to;
            }
            if (stopped) Projectiles.RemoveAt(i);
        }
    }

    private bool FindProjectileImpact(Vector3 from, Vector3 to, out float fraction, out Cell? terrain, out Zombie? zombie, out CrustFace? crustFace)
    {
        terrain = null;
        zombie = null;
        crustFace = null;
        fraction = float.PositiveInfinity;
        if (!ProjectilePointIsInside(from)) { fraction = 0; return true; }
        var delta = to - from;
        if (!ProjectilePointIsInside(to))
        {
            float enter = 0, leave = 1;
            ProjectileAxisInterval(from.X, delta.X, 0, WorldGrid.Size, ref enter, ref leave);
            ProjectileAxisInterval(from.Y, delta.Y, 0, WorldGrid.Size, ref enter, ref leave);
            ProjectileAxisInterval(from.Z, delta.Z, 0, WorldGrid.Size, ref enter, ref leave);
            fraction = leave;
        }

        // Bound the scan to the part of this segment inside the battlefield.
        var insideEnd = float.IsFinite(fraction) ? Vector3.Lerp(from, to, fraction) : to;
        var min = Vector3.Min(from, insideEnd);
        var max = Vector3.Max(from, insideEnd);
        for (int x = Math.Max(0, (int)MathF.Floor(min.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.X)); x++)
            for (int y = Math.Max(0, (int)MathF.Floor(min.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Y)); y++)
                for (int z = Math.Max(0, (int)MathF.Floor(min.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Z)); z++)
                {
                    var cell = new Cell(x, y, z);
                    if (World.GetMaterial(cell) == Material.Air) continue; // Friendly pieces have no terrain volume.
                    var crust = World.GetObsidian(cell);
                    if (crust is not null)
                    {
                        foreach (var part in crust.Parts)
                        {
                            if (!ProjectileBoxIntersection(from, delta, part.Min, part.Max, out var hit) || hit > fraction) continue;
                            fraction = hit;
                            terrain = cell;
                            crustFace = part.Face;
                        }
                    }
                    else if (ProjectileBoxIntersection(from, delta, new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1), out var hit) && hit <= fraction)
                    {
                        fraction = hit;
                        terrain = cell;
                        crustFace = null;
                    }
                }
        foreach (var candidate in Zombies)
        {
            if (candidate.Hp <= 0) continue;
            bool intersects = candidate.Size == ZombieSize.Kilo
                ? ProjectileBoxIntersection(from, delta, candidate.Position - candidate.BodyHalfExtents, candidate.Position + candidate.BodyHalfExtents, out var hit)
                : ProjectileBodyIntersection(from, delta, candidate.Position, out hit, candidate.BodyScale);
            if (!intersects || hit >= fraction) continue;
            fraction = hit;
            zombie = candidate;
            terrain = null;
            crustFace = null;
        }
        return float.IsFinite(fraction);
    }

    private void DamageProjectileTerrain(Cell cell, float damage, CrustFace? crustFace = null)
    {
        if (!float.IsFinite(damage) || damage <= 0) return;
        bool destroyed = false;
        if (World.GetBarrier(cell) is { } barrier)
        {
            barrier.Hp -= damage;
            if (barrier.Hp <= 0) destroyed = World.RemoveBarrier(cell);
        }
        else if (World.GetObsidian(cell) != null)
            destroyed = crustFace.HasValue ? World.DamageObsidianPart(cell, crustFace.Value, damage) : World.DamageObsidian(cell, damage);
        else if (Balance.IsTerrainMaterial(World.GetMaterial(cell)))
            destroyed = World.DamageTerrain(cell, damage);
        // Projectile destruction never calls mining, block-drop, or obsidian-shatter paths.
        if (destroyed) SettleDefenses();
    }

    private static bool ProjectileBodyIntersection(Vector3 from, Vector3 delta, Vector3 center, out float hit, float bodyScale = 1)
    {
        // Preserve the existing half-block hit radius, but resolve its first contact.
        var offset = from - center;
        var c = Vector3.Dot(offset, offset) - .25f * bodyScale * bodyScale;
        if (c <= 0) { hit = 0; return true; }
        var a = Vector3.Dot(delta, delta);
        var b = Vector3.Dot(offset, delta);
        var discriminant = b * b - a * c;
        if (a <= 0 || discriminant < 0) { hit = 0; return false; }
        hit = (-b - MathF.Sqrt(discriminant)) / a;
        return hit is >= 0 and <= 1;
    }

    private static bool ProjectileBoxIntersection(Vector3 from, Vector3 delta, Vector3 min, Vector3 max, out float hit)
    {
        float enter = 0, leave = 1;
        bool intersects = ProjectileAxisInterval(from.X, delta.X, min.X, max.X, ref enter, ref leave) &&
            ProjectileAxisInterval(from.Y, delta.Y, min.Y, max.Y, ref enter, ref leave) &&
            ProjectileAxisInterval(from.Z, delta.Z, min.Z, max.Z, ref enter, ref leave) && leave > 0;
        hit = enter;
        return intersects;
    }

    private static bool ProjectileAxisInterval(float origin, float delta, float min, float max, ref float enter, ref float leave)
    {
        if (delta == 0) return origin >= min && origin < max;
        var first = (min - origin) / delta;
        var last = (max - origin) / delta;
        enter = Math.Max(enter, Math.Min(first, last));
        leave = Math.Min(leave, Math.Max(first, last));
        return enter <= leave;
    }

    private static bool ProjectilePointIsFinite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    private static bool ProjectilePointIsInside(Vector3 point) => point.X is >= 0 and < WorldGrid.Size &&
        point.Y is >= 0 and < WorldGrid.Size && point.Z is >= 0 and < WorldGrid.Size;
}
