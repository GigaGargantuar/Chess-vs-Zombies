using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private void UpdateProjectileShots(float dt)
    {
        List<Projectile>? remaining = null;
        int cursor = 0, originalIndex = Projectiles.Count - 1;
        bool reorder = _megaFlights.Count > 0;
        while (remaining != null ? cursor < remaining.Count : originalIndex >= 0)
        {
            if (reorder)
            {
                if (ProjectileDiagnostics is { } diagnostics) diagnostics.PendingSorts++;
                // A later shot must not destroy a moving shield before an
                // earlier shot reaches it, regardless of append order.
                var pending = remaining == null ? Projectiles.AsEnumerable().Reverse() : remaining.Skip(cursor);
                remaining = pending.OrderBy(projectile => NextMegaInterceptionTime(projectile, dt)).ToList();
                cursor = 0;
                reorder = false;
            }
            int removalIndex = remaining == null ? originalIndex-- : -1;
            var projectile = remaining == null ? Projectiles[removalIndex] : remaining[cursor++];
            var sceneBeforeImpact = CurrentProjectileScene;
            var startAge = projectile.Age;
            projectile.Age += dt;
            if (projectile.Kind == ShotKind.Melee)
            {
                if (projectile.Age >= projectile.Duration) RemoveResolvedProjectile(projectile, removalIndex);
                continue;
            }
            if (!float.IsFinite(projectile.Age) || !float.IsFinite(projectile.Duration) ||
                !ProjectilePointIsFinite(projectile.Origin) || !ProjectilePointIsFinite(projectile.Target))
            {
                RemoveResolvedProjectile(projectile, removalIndex);
                continue;
            }

            // Pellets use one exact segment. Lobs sample their continuing parabola,
            // with short exact sweeps between samples so thin crust is never skipped.
            var speedBound = Math.Max(projectile.GetTravelDirection(startAge).Length(),
                projectile.GetTravelDirection(projectile.Age).Length()) / projectile.FlightDuration;
            if (!float.IsFinite(speedBound) || speedBound <= .000001f)
            {
                RemoveResolvedProjectile(projectile, removalIndex);
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
                var sampleStart = dt * (sample - 1) / steps;
                var sampleEnd = dt * sample / steps;
                bool hitThrown = TryFindMegaFlightImpact(from, to, sampleStart, sampleEnd,
                    out var thrownFraction, out var thrownFlight, out var thrownBlock);
                if (zombie != null && (!hitThrown || fraction < thrownFraction)) TryInterceptKiloShot(zombie, from, to, fraction);
                bool hitCover = TryFindKiloCoverImpact(from, to, out var coverFraction, out var carrierId, out var blockIndex);
                if (hitThrown && thrownFraction <= fraction && (!hitCover || thrownFraction < coverFraction))
                {
                    float hpBefore = thrownBlock!.Hp;
                    DamageMegaFlightBlock(thrownFlight!, thrownBlock!, projectile.Damage);
                    ProjectileDiagnostics?.Contact(projectile, startAge + sampleStart + (sampleEnd - sampleStart) * thrownFraction,
                        "flight", thrownFlight!.OwnerId, thrownBlock.BlockIndex, null, null, hpBefore, thrownBlock.Hp);
                    stopped = true;
                }
                else if (hitCover && coverFraction <= fraction)
                {
                    float hpBefore = 0;
                    if (ProjectileDiagnostics?.CaptureContacts == true)
                        hpBefore = _kiloBlocks[carrierId].First(block => block.BlockIndex == blockIndex).Hp;
                    DamageKiloCover(carrierId, blockIndex, projectile.Damage);
                    float hpAfter = 0;
                    if (ProjectileDiagnostics?.CaptureContacts == true && _kiloBlocks.TryGetValue(carrierId, out var remainingBlocks))
                        hpAfter = remainingBlocks.FirstOrDefault(block => block.BlockIndex == blockIndex)?.Hp ?? 0;
                    ProjectileDiagnostics?.Contact(projectile, startAge + sampleStart + (sampleEnd - sampleStart) * coverFraction,
                        "cover", carrierId, blockIndex, null, null, hpBefore, hpAfter);
                    var carrier = Zombies.FirstOrDefault(candidate => candidate.Id == carrierId && candidate.BodyHp > 0);
                    if (carrier != null && projectile.Damage > 0 && float.IsFinite(projectile.Damage))
                    {
                        var previousAge = startAge + dt * (sample - 1) / steps;
                        var hitAge = previousAge + (endAge - previousAge) * coverFraction;
                        FaceKiloTowardShot(carrier, projectile.GetTravelDirection(hitAge));
                    }
                    stopped = true;
                }
                else if (hitSomething)
                {
                    float hpBefore = ProjectileDiagnostics?.CaptureContacts == true ? zombie?.Hp ??
                        ProjectileTerrainHp(terrain, crustFace) : 0;
                    var previousAge = startAge + dt * (sample - 1) / steps;
                    var hitAge = previousAge + (endAge - previousAge) * fraction;
                    if (zombie != null)
                        DamageZombie(zombie, projectile.Damage, projectile.DamageKind, projectile.Kind, projectile.GetTravelDirection(hitAge));
                    else if (terrain.HasValue)
                        DamageProjectileTerrain(terrain.Value, projectile.Damage, crustFace);
                    ProjectileDiagnostics?.Contact(projectile, hitAge, zombie != null ? "body" : terrain.HasValue ? "terrain" : "boundary",
                        zombie?.Id ?? -1, -1, terrain, crustFace, hpBefore,
                        ProjectileDiagnostics.CaptureContacts ? zombie?.Hp ?? ProjectileTerrainHp(terrain, crustFace) : 0);
                    stopped = true;
                }
                from = to;
            }
            if (stopped)
            {
                RemoveResolvedProjectile(projectile, removalIndex);
                // An HP-only impact leaves every pending contact time unchanged.
                // Preserve the existing stable order until a body/part dies, a
                // hand moves, or terrain/structure geometry actually changes.
                reorder = _megaFlights.Count > 0 && CurrentProjectileScene != sceneBeforeImpact;
            }
        }
    }

    private float ProjectileTerrainHp(Cell? cell, CrustFace? face)
    {
        if (!cell.HasValue) return 0;
        if (World.GetBarrier(cell.Value) is { } barrier) return barrier.Hp;
        if (World.GetObsidian(cell.Value) is { } crust)
            return face.HasValue ? crust.Parts.FirstOrDefault(part => part.Face == face.Value)?.Hp ?? 0 : crust.Hp;
        return World.GetTerrainHp(cell.Value);
    }

    private void RemoveResolvedProjectile(Projectile projectile, int originalIndex)
    {
        if (originalIndex >= 0) Projectiles.RemoveAt(originalIndex);
        else Projectiles.Remove(projectile);
    }

    private bool FindProjectileImpact(Vector3 from, Vector3 to, out float fraction, out Cell? terrain, out Zombie? zombie, out CrustFace? crustFace)
    {
        if (ProjectileDiagnostics is { } diagnostics) diagnostics.BodyQueries++;
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
        var segmentMin = Vector3.Min(from, to);
        var segmentMax = Vector3.Max(from, to);
        foreach (var candidate in Zombies)
        {
            if (candidate.BodyHp <= 0) continue;
            // Most shots only pass a few nearby bodies. Reject separated
            // bounds before solving exact contacts, without changing list/tie
            // order. Small bodies retain their half-block projectile sphere;
            // their narrower movement bounds would miss valid hits here.
            var halfExtents = candidate.IsGiant ? candidate.BodyHalfExtents : new Vector3(.5f * candidate.BodyScale);
            // Keep tangencies conservative despite float rounding in the exact
            // sphere discriminant and endpoint calculations on the 32-cube board.
            var padding = new Vector3(.01f);
            var bodyMin = candidate.Position - halfExtents - padding;
            var bodyMax = candidate.Position + halfExtents + padding;
            if (segmentMax.X < bodyMin.X || segmentMin.X > bodyMax.X ||
                segmentMax.Y < bodyMin.Y || segmentMin.Y > bodyMax.Y ||
                segmentMax.Z < bodyMin.Z || segmentMin.Z > bodyMax.Z) continue;
            bool intersects = candidate.IsGiant
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
