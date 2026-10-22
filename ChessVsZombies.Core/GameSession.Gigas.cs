using System.Numerics;

namespace ChessVsZombies.Core;

/// <summary>A hostile shot snapshots its damage and locked target; every impact covers the same three-cell cube.</summary>
public sealed class GigaProjectile
{
    public int OwnerId { get; init; }
    public int TargetId { get; init; }
    public Vector3 Origin { get; init; }
    public Vector3 Target { get; init; }
    public float Damage { get; init; }
    public int BlastSize => GigaRules.BlastSize;
    public float Radius => GigaRules.BlastHalfExtent;
    public float Age { get; set; }
    public float Duration { get; init; }
    public Vector3 GetPositionAt(float age) => Vector3.Lerp(Origin, Target, Math.Clamp(age / Math.Max(.001f, Duration), 0, 1));
    public Vector3 Position => GetPositionAt(Age);
}

public sealed partial class GameSession
{
    public const float GigaRange = 16;
    public const float GigaProjectileSpeed = 12;
    public List<GigaProjectile> GigaProjectiles { get; } = [];
    private readonly Dictionary<int, int> _gigaTargets = [];

    private void ResetGigas()
    {
        GigaProjectiles.Clear();
        _gigaTargets.Clear();
    }

    private bool TryGigaRangedAttack(Zombie zombie)
    {
        if (!zombie.IsGiga || IsZombieRetreating(zombie)) return false;
        var target = Turrets.FirstOrDefault(turret => turret.Hp > 0 && turret.Id == _gigaTargets.GetValueOrDefault(zombie.Id));
        if (target == null)
        {
            float nearest = float.PositiveInfinity;
            foreach (var turret in Turrets)
            {
                if (turret.Hp <= 0) continue;
                float distance = Vector3.DistanceSquared(turret.Cell.Center,
                    Vector3.Clamp(turret.Cell.Center, zombie.Position - zombie.BodyHalfExtents, zombie.Position + zombie.BodyHalfExtents));
                if (distance > GigaRange * GigaRange || distance >= nearest) continue;
                nearest = distance;
                target = turret;
            }
            if (target == null) { _gigaTargets.Remove(zombie.Id); return false; }
            _gigaTargets[zombie.Id] = target.Id;
        }
        if (Vector3.DistanceSquared(target.Cell.Center,
            Vector3.Clamp(target.Cell.Center, zombie.Position - zombie.BodyHalfExtents, zombie.Position + zombie.BodyHalfExtents)) > GigaRange * GigaRange)
            return false;
        zombie.FaceToward(target.Cell.Center - zombie.Position);
        if (zombie.Cooldown > 0) return true;
        var direction = Vector3.Normalize(target.Cell.Center - zombie.Position);
        float exit = float.PositiveInfinity;
        if (Math.Abs(direction.X) > .00001f) exit = Math.Min(exit, zombie.BodyHalfExtents.X / Math.Abs(direction.X));
        if (Math.Abs(direction.Y) > .00001f) exit = Math.Min(exit, zombie.BodyHalfExtents.Y / Math.Abs(direction.Y));
        if (Math.Abs(direction.Z) > .00001f) exit = Math.Min(exit, zombie.BodyHalfExtents.Z / Math.Abs(direction.Z));
        var origin = zombie.Position + direction * (exit + .001f);
        GigaProjectiles.Add(new GigaProjectile
        {
            OwnerId = zombie.Id, TargetId = target.Id, Origin = origin, Target = target.Cell.Center,
            Damage = zombie.Giga!.AttackDamage,
            Duration = Math.Max(.001f, Vector3.Distance(origin, target.Cell.Center) / GigaProjectileSpeed)
        });
        zombie.Cooldown = 1;
        return true;
    }

    private void UpdateGigaProjectiles(float dt)
    {
        for (int index = 0; index < GigaProjectiles.Count; index++)
        {
            var shot = GigaProjectiles[index];
            var from = shot.Position;
            shot.Age += dt;
            var to = shot.Position;
            var fraction = GigaImpactFraction(from, to, out var impactCell);
            if (!float.IsFinite(fraction) && shot.Age < shot.Duration) continue;
            var impact = float.IsFinite(fraction) ? Vector3.Lerp(from, to, fraction) : to;
            GigaProjectiles.RemoveAt(index--);
            if (impactCell is { } cell) ExplodeGigaProjectileInCell(shot, cell);
            else ExplodeGigaProjectile(shot, impact);
        }
    }

    private float NextGigaImpactTime(GigaProjectile shot, float dt)
    {
        float travel = Math.Min(dt, Math.Max(0, shot.Duration - shot.Age));
        var fraction = GigaImpactFraction(shot.Position, shot.GetPositionAt(shot.Age + travel));
        return float.IsFinite(fraction) ? travel * fraction : travel;
    }

    private float GigaImpactFraction(Vector3 from, Vector3 to) => GigaImpactFraction(from, to, out _);

    private float GigaImpactFraction(Vector3 from, Vector3 to, out Cell? impactCell)
    {
        impactCell = null;
        var delta = to - from;
        float fraction = float.PositiveInfinity;
        var min = Vector3.Min(from, to);
        var max = Vector3.Max(from, to);
        for (int x = Math.Max(0, (int)MathF.Floor(min.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.X)); x++)
            for (int y = Math.Max(0, (int)MathF.Floor(min.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Y)); y++)
                for (int z = Math.Max(0, (int)MathF.Floor(min.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Z)); z++)
                {
                    var cell = new Cell(x, y, z);
                    if (World.GetMaterial(cell) == Material.Air) continue;
                    foreach (var bounds in SolidCollisionBounds(cell))
                        if (ProjectileBoxIntersection(from, delta, bounds.Min, bounds.Max, out float hit) && hit < fraction)
                        {
                            fraction = hit;
                            impactCell = cell;
                        }
                }
        foreach (var turret in Turrets)
            if (turret.Hp > 0 && ProjectileBoxIntersection(from, delta,
                new Vector3(turret.Cell.X, turret.Cell.Y, turret.Cell.Z),
                new Vector3(turret.Cell.X + 1, turret.Cell.Y + 1, turret.Cell.Z + 1), out float hit) && hit < fraction)
            {
                fraction = hit;
                impactCell = turret.Cell;
            }
        return fraction;
    }

    private void ExplodeGigaProjectile(GigaProjectile shot, Vector3 center)
    {
        if (!ProjectilePointIsFinite(center) || center.X < -1 || center.Y < -1 || center.Z < -1 ||
            center.X >= WorldGrid.Size + 1 || center.Y >= WorldGrid.Size + 1 || center.Z >= WorldGrid.Size + 1) return;
        ExplodeGigaProjectileInCell(shot, new Cell((int)MathF.Floor(center.X), (int)MathF.Floor(center.Y), (int)MathF.Floor(center.Z)));
    }

    private void ExplodeGigaProjectileInCell(GigaProjectile shot, Cell impactCell)
    {
        // Positive infinity is a valid result of the uncapped exponential ATK
        // formula. Apply only each target's remaining finite durability, rather
        // than passing infinity into terrain APIs that reject invalid damage.
        if (float.IsNaN(shot.Damage) || shot.Damage <= 0 ||
            impactCell.X < -1 || impactCell.X > WorldGrid.Size || impactCell.Y < -1 || impactCell.Y > WorldGrid.Size ||
            impactCell.Z < -1 || impactCell.Z > WorldGrid.Size) return;
        float AppliedDamage(float hp) => Math.Min(shot.Damage, Math.Max(0, hp));
        const int neighbors = GigaRules.BlastSize / 2;
        int minX = Math.Max(0, impactCell.X - neighbors), maxX = Math.Min(WorldGrid.Size - 1, impactCell.X + neighbors);
        int minY = Math.Max(0, impactCell.Y - neighbors), maxY = Math.Min(WorldGrid.Size - 1, impactCell.Y + neighbors);
        int minZ = Math.Max(0, impactCell.Z - neighbors), maxZ = Math.Min(WorldGrid.Size - 1, impactCell.Z + neighbors);
        bool Contains(Cell cell) => cell.X >= minX && cell.X <= maxX && cell.Y >= minY && cell.Y <= maxY && cell.Z >= minZ && cell.Z <= maxZ;
        bool successful = false, killedAnything = false, killedLockedTarget = false, worked = false;
        int currentLockedTargetId = _gigaTargets.GetValueOrDefault(shot.OwnerId);
        long fieldDeathsBefore = DeathToll.FieldDeaths;
        foreach (var turret in Turrets.ToArray())
        {
            var cell = turret.Cell;
            if (turret.Hp <= 0 || World.GetObsidian(cell) != null || !Contains(cell)) continue;
            turret.Hp = Math.Max(0, turret.Hp - AppliedDamage(turret.Hp));
            successful = worked = true;
            if (turret.Hp > 0) continue;
            killedAnything = true;
            killedLockedTarget |= turret.Id == currentLockedTargetId;
            Turrets.Remove(turret);
            _turretsByCell.Remove(cell);
            _topologyRevision++;
        }
        bool coreHit = false;
        for (int x = minX; x <= maxX; x++)
            for (int y = minY; y <= maxY; y++)
                for (int z = minZ; z <= maxZ; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (WorldGrid.IsCore(cell)) { coreHit = true; continue; }
                    if (World.GetObsidian(cell) is { } crust)
                    {
                        foreach (var part in crust.Parts.ToArray())
                        {
                            successful = worked = true;
                            if (World.DamageObsidianPart(cell, part.Face, AppliedDamage(part.Hp)))
                            {
                                killedAnything = true;
                                ShatterObsidian(cell);
                            }
                        }
                    }
                    else if (World.GetBarrier(cell) is { } barrier)
                    {
                        successful = worked = true;
                        barrier.Hp = Math.Max(0, barrier.Hp - AppliedDamage(barrier.Hp));
                        if (barrier.Hp <= 0) killedAnything |= World.RemoveBarrier(cell);
                    }
                    else if (Balance.IsTerrainMaterial(World.GetMaterial(cell)))
                    {
                        successful = worked = true;
                        killedAnything |= World.DamageTerrain(cell, AppliedDamage(World.GetTerrainHp(cell)));
                    }
                }
        if (coreHit && CoreHp > 0)
        {
            CoreHp = Math.Max(0, CoreHp - AppliedDamage(CoreHp));
            successful = worked = true;
            killedAnything |= CoreHp <= 0;
        }
        killedAnything |= DeathToll.FieldDeaths > fieldDeathsBefore;
        var owner = Zombies.FirstOrDefault(zombie => zombie.Id == shot.OwnerId && zombie.BodyHp > 0 && zombie.IsGiga);
        if (owner != null)
        {
            if (worked) RecordZombieProgress(owner);
            if (successful || killedAnything) owner.Giga!.RecordSuccessfulAttack(killedAnything);
            if (killedLockedTarget) _gigaTargets.Remove(owner.Id);
        }
        SettleDefenses();
    }
}
