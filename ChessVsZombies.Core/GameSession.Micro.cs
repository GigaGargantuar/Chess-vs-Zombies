using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private const float MicroThreatHorizon = .65f;
    private const float MicroJumpSpeed = 4;
    private const float MicroJumpGravity = 12;
    private readonly Dictionary<int, MicroMovement> _microStates = [];

    private sealed class MicroMovement
    {
        public float DashCooldown = .75f;
        public float DodgeRemaining;
        public Vector3 DodgeDirection;
        public bool Jumping;
        public float VerticalSpeed;
        public int Dodges;
        public float NavigationRemaining;
    }

    private void ResetMicroBehavior()
    {
        _microStates.Clear();
        foreach (var zombie in Zombies) zombie.MicroDashRemaining = 0;
    }

    private void AdvanceMicroTimers(Zombie zombie, float dt)
    {
        if (zombie.Size != ZombieSize.Micro) return;
        if (!_microStates.TryGetValue(zombie.Id, out var state))
            _microStates[zombie.Id] = state = new MicroMovement();
        state.DashCooldown = Math.Max(0, state.DashCooldown - dt);
        state.DodgeRemaining = Math.Max(0, state.DodgeRemaining - dt);
        state.NavigationRemaining = Math.Max(0, state.NavigationRemaining - dt);
        zombie.MicroDashRemaining = Math.Max(0, zombie.MicroDashRemaining - dt);
    }

    private bool TryUpdateMicro(Zombie zombie, float dt)
    {
        if (zombie.Size != ZombieSize.Micro) return false;
        if (!_microStates.TryGetValue(zombie.Id, out var state))
            _microStates[zombie.Id] = state = new MicroMovement();
        bool grounded = HasSolidFooting(zombie.Position, zombie);
        bool retreating = IsZombieRetreating(zombie);
        var forward = MicroFlankDirection(zombie);

        // Dodge incidental fire too: TargetId does not define physical danger.
        // Refresh steering during a jump when another trajectory crosses it.
        if (TryFindMicroThreat(zombie, forward * zombie.MovementSpeed, out var incoming) &&
            state.DodgeRemaining <= .1f)
        {
            var sideways = new Vector3(-incoming.Z, 0, incoming.X);
            if (sideways.LengthSquared() < .000001f) sideways = new Vector3(-forward.Z, 0, forward.X);
            sideways = Vector3.Normalize(sideways);
            if ((zombie.Id & 1) == 0) sideways = -sideways;
            if (!MicroSweepClear(zombie, sideways * .35f)) sideways = -sideways;
            if (MicroSweepClear(zombie, sideways * .15f))
            {
                if (!grounded && !state.Jumping && _jumpHeights.TryGetValue(zombie.Id, out float jumpHeight))
                {
                    state.Jumping = true;
                    state.VerticalSpeed = 2 * jumpHeight * (1 - 2 * zombie.MoveProgress) * zombie.MovementSpeed;
                    _fallStartHeights.TryAdd(zombie.Id, zombie.Position.Y);
                }
                state.DodgeDirection = sideways;
                state.DodgeRemaining = .35f;
                state.NavigationRemaining = 0;
                if (grounded || zombie.NextCell.HasValue) ClearZombieMovement(zombie);
            }
            if (grounded && state.Dodges++ % 2 == 0 && MicroSweepClear(zombie, Vector3.UnitY * .75f))
            {
                ClearZombieMovement(zombie);
                state.Jumping = true;
                state.VerticalSpeed = MicroJumpSpeed;
                _fallStartHeights.TryAdd(zombie.Id, zombie.Position.Y);
            }
            if (state.DodgeRemaining > 0 || state.Jumping) StartMicroDash(zombie, state);
        }

        // Finish physical navigation jumps/climbs and use the ordinary retreat
        // route. Reactive steering may interrupt those steps at their real position.
        if (!state.Jumping && state.DodgeRemaining <= 0 &&
            (zombie.NextCell.HasValue || retreating || state.NavigationRemaining > 0)) return false;
        if (retreating) forward = -forward;
        if (!retreating && !state.Jumping && state.DodgeRemaining <= 0 && grounded && FaceNearbyCore(zombie))
        {
            if (zombie.Cooldown <= 0)
            {
                CoreHp -= zombie.Attack;
                if (zombie.Attack > 0) RecordZombieProgress(zombie);
                zombie.Cooldown = 1;
            }
            return true;
        }

        if (grounded && !retreating && CoreDistance(zombie.Position) > 4) StartMicroDash(zombie, state);
        var heading = state.DodgeRemaining > 0
            ? Vector3.Normalize(forward * .45f + state.DodgeDirection) : forward;
        var origin = zombie.Position;
        bool moved = MoveMicroHorizontally(zombie, heading, dt, grounded && !state.Jumping);
        if (!grounded && !state.Jumping) Falls.TranslateZombieFall(zombie, zombie.Position - origin);

        if (state.Jumping)
        {
            // Integrate the jump in short swept segments. A low ceiling stops
            // the ascent; a changed floor is landed on at its actual height.
            int steps = Math.Max(1, (int)MathF.Ceiling(dt / .015f));
            float slice = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                float deltaY = state.VerticalSpeed * slice - .5f * MicroJumpGravity * slice * slice;
                state.VerticalSpeed -= MicroJumpGravity * slice;
                var next = zombie.Position + Vector3.UnitY * deltaY;
                if (deltaY <= 0 && TrySolidFloor(zombie.Position, out float floor, zombie) && next.Y <= floor)
                {
                    next.Y = floor;
                    if (MicroSweepClear(zombie, next - zombie.Position)) zombie.Position = next;
                    state.Jumping = false;
                    state.VerticalSpeed = 0;
                    CompleteZombieFall(zombie, floor);
                    break;
                }
                if (!MicroSweepClear(zombie, next - zombie.Position))
                {
                    state.VerticalSpeed = Math.Min(0, state.VerticalSpeed);
                    break;
                }
                zombie.Position = next;
                if (_fallStartHeights.TryGetValue(zombie.Id, out var height))
                    _fallStartHeights[zombie.Id] = Math.Max(height, zombie.Position.Y);
            }
            return true;
        }
        // Keep the existing fall curve and accumulated landing damage. Horizontal
        // steering above does not suspend or reset a naturally falling body.
        if (!grounded) return false;
        if (moved) return true;
        // A blocked flank resumes the exact sized walking/digging/bridge rules.
        // Give that route time to recenter a fractional position and take a step.
        state.NavigationRemaining = .5f;
        return false;
    }

    private void StartMicroDash(Zombie zombie, MicroMovement state)
    {
        if (state.DashCooldown > 0 || zombie.MicroDashRemaining > 0) return;
        zombie.MicroDashRemaining = .2f + _random.NextSingle() * 1.8f;
        state.DashCooldown = zombie.MicroDashRemaining + .6f + _random.NextSingle() * .8f;
    }

    private static Vector3 MicroFlankDirection(Zombie zombie)
    {
        var toward = new Vector3(16 - zombie.Position.X, 0, 16 - zombie.Position.Z);
        float distance = toward.Length();
        if (distance < .00001f) return zombie.Facing;
        toward /= distance;
        var tangent = new Vector3(-toward.Z, 0, toward.X) * ((zombie.Id & 1) == 0 ? 1 : -1);
        return Vector3.Normalize(toward + tangent * Math.Clamp((distance - 3) / 8, 0, .85f));
    }

    private bool MoveMicroHorizontally(Zombie zombie, Vector3 heading, float dt, bool needsFooting)
    {
        // Deflect around a turret's sides rather than stopping to fight it.
        // Try both sides in stable order, retaining forward progress.
        ReadOnlySpan<float> angles = [0, .5f, -.5f, 1, -1, 1.5f, -1.5f];
        foreach (float angle in angles)
        {
            float side = (zombie.Id & 1) == 0 ? angle : -angle;
            var direction = new Vector3(heading.X * MathF.Cos(side) - heading.Z * MathF.Sin(side), 0,
                heading.X * MathF.Sin(side) + heading.Z * MathF.Cos(side));
            var delta = direction * (zombie.MovementSpeed * dt);
            if (!MicroSweepClear(zombie, delta)) continue;
            var next = zombie.Position + delta;
            if (needsFooting)
            {
                bool supported = true;
                int samples = Math.Max(1, (int)MathF.Ceiling(delta.Length() / .05f));
                for (int i = 1; i <= samples; i++)
                    if (!HasSolidFooting(Vector3.Lerp(zombie.Position, next, i / (float)samples), zombie))
                    { supported = false; break; }
                if (!supported) continue;
            }
            zombie.FaceToward(delta);
            zombie.Position = next;
            return delta.LengthSquared() > .00000001f;
        }
        return false;
    }

    private bool MicroSweepClear(Zombie zombie, Vector3 delta) => IsBodyClear(zombie.Position + delta, zombie) &&
        !TryFindBodyObstacle(zombie.Position, zombie.Position + delta, out _, zombie: zombie);

    private bool TryFindMicroThreat(Zombie zombie, Vector3 velocity, out Vector3 incoming)
    {
        incoming = default;
        float earliest = float.PositiveInfinity;
        foreach (var projectile in Projectiles)
        {
            if (projectile.Kind == ShotKind.Melee || projectile.Damage <= 0 || !float.IsFinite(projectile.Age) ||
                !float.IsFinite(projectile.Duration) || !ProjectilePointIsFinite(projectile.Origin) ||
                !ProjectilePointIsFinite(projectile.Target)) continue;
            // Compare each actual segment with the moving body's predicted
            // position. The trajectory continues beyond its original aim point.
            int samples = projectile.Kind == ShotKind.Lob ? 26 : 1;
            var from = projectile.GetPositionAt(projectile.Age) - zombie.Position;
            for (int i = 1; i <= samples; i++)
            {
                float time = MicroThreatHorizon * i / samples;
                var to = projectile.GetPositionAt(projectile.Age + time) - (zombie.Position + velocity * time);
                if (ProjectileBodyIntersection(from, to - from, Vector3.Zero, out float fraction, zombie.BodyScale + .16f))
                {
                    float hitTime = MicroThreatHorizon * (i - 1 + fraction) / samples;
                    var shotPosition = projectile.GetPositionAt(projectile.Age + hitTime);
                    if (hitTime < earliest && ProjectilePointIsInside(shotPosition) &&
                        HasLineOfSight(projectile.GetPositionAt(projectile.Age), shotPosition))
                    {
                        earliest = hitTime;
                        incoming = projectile.GetTravelDirection(projectile.Age + hitTime);
                    }
                    break;
                }
                from = to;
            }
        }
        return float.IsFinite(earliest);
    }

    private void MicroPassingAttack(Zombie zombie, Vector3 from, Vector3 to)
    {
        if (zombie.Cooldown > 0 || IsZombieRetreating(zombie)) return;
        Turret? target = null;
        float closest = float.PositiveInfinity;
        foreach (var turret in Turrets)
        {
            if (turret.Hp <= 0) continue;
            var min = new Vector3(turret.Cell.X, turret.Cell.Y, turret.Cell.Z);
            var max = min + Vector3.One;
            // A swept reach volume catches a swipe even during the fastest dash.
            if (!ProjectileBoxIntersection(from, to - from, min - new Vector3(.65f), max + new Vector3(.65f), out var hit)) continue;
            var point = Vector3.Lerp(from, to, hit);
            var contact = Vector3.Clamp(point, min, max);
            float distance = Vector3.DistanceSquared(point, contact);
            if (distance > .65f * .65f || distance >= closest || !HasLineOfSight(point, contact)) continue;
            target = turret;
            closest = distance;
        }
        if (target != null) AttackObstacle(zombie, target.Cell, to);
    }
}
