using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    public ProjectileDiagnostics? ProjectileDiagnostics { get; set; }
    private readonly record struct MegaFlightSweep(float Start, float End, Vector3 From, Vector3 To, bool EndsAtImpact);
    private readonly Dictionary<MegaWeaponFlight, List<MegaFlightSweep>> _megaInterceptionSweeps = [];
    private float _megaInterceptionDuration;
    private int _megaInterceptionWorldRevision, _megaInterceptionTopologyRevision;

    private void UpdateProjectiles(float dt)
    {
        ProjectileDiagnostics?.BeginFrame(Projectiles);
        // Choose one simultaneous guard pose for this simulation interval.
        // Event subdivisions must not give a carrier a new pose for every shot.
        UpdateMegaHeldPoses();
        float remaining = dt;
        while (remaining > 0)
        {
            if (ProjectileDiagnostics is { } diagnostics) diagnostics.Slices++;
            BeginMegaInterceptionFrame(remaining);
            float slice = remaining;
            if (_megaInterceptionSweeps.Count > 0)
            {
                float next = remaining;
                foreach (var sweeps in _megaInterceptionSweeps.Values)
                    foreach (var sweep in sweeps)
                        if (sweep.EndsAtImpact) next = Math.Min(next, sweep.End);
                foreach (var shot in Projectiles) next = Math.Min(next, NextMegaInterceptionTime(shot, remaining));
                // Include a small positive tolerance at a predicted boundary:
                // resampling a parabola must not repeatedly stop just short.
                slice = Math.Min(remaining, Math.Max(.000001f, next + .000001f));
                if (slice < remaining) BeginMegaInterceptionFrame(slice);
            }
            UpdateProjectileShots(slice);
            EndMegaInterceptionFrame();
            // Commit this contact before forecasting later bullet contacts.
            // A tower already pierced cannot retroactively survive a later shot.
            UpdateMegaProjectiles(slice);
            remaining -= slice;
        }
    }

    private void BeginMegaInterceptionFrame(float dt)
    {
        _megaInterceptionDuration = dt;
        if (_megaFlights.Count == 0 || !Projectiles.Any(projectile => projectile.Kind != ShotKind.Melee))
        {
            _megaInterceptionSweeps.Clear();
            _megaInterceptionWorldRevision = World.Revision;
            _megaInterceptionTopologyRevision = _topologyRevision;
            return;
        }
        RefreshMegaInterceptionSweeps();
    }

    private void EndMegaInterceptionFrame() => _megaInterceptionSweeps.Clear();

    private float NextMegaInterceptionTime(Projectile projectile, float dt)
    {
        if (ProjectileDiagnostics is { } diagnostics)
        { diagnostics.ForecastRequests++; diagnostics.ForecastEvaluations++; }
        if (projectile.Kind == ShotKind.Melee || !float.IsFinite(projectile.Age) || !float.IsFinite(projectile.Duration) ||
            !ProjectilePointIsFinite(projectile.Origin) || !ProjectilePointIsFinite(projectile.Target)) return dt;
        float speedBound = Math.Max(projectile.GetTravelDirection(projectile.Age).Length(),
            projectile.GetTravelDirection(projectile.Age + dt).Length()) / projectile.FlightDuration;
        if (!float.IsFinite(speedBound) || speedBound <= .000001f) return dt;
        int samples = projectile.Kind == ShotKind.Lob ? Math.Max(1, (int)MathF.Ceiling(speedBound * dt / .1f)) : 1;
        var from = projectile.GetPositionAt(projectile.Age);
        for (int sample = 0; sample < samples; sample++)
        {
            float start = dt * sample / samples, end = dt * (sample + 1) / samples;
            var to = projectile.GetPositionAt(projectile.Age + end);
            if (!ProjectilePointIsFinite(from) || !ProjectilePointIsFinite(to)) return start;
            FindProjectileImpact(from, to, out float contact, out _, out _, out _);
            if (TryFindKiloCoverImpact(from, to, out var cover, out _, out _)) contact = Math.Min(contact, cover);
            if (TryFindMegaFlightImpact(from, to, start, end, out var thrown, out _, out _)) contact = Math.Min(contact, thrown);
            if (float.IsFinite(contact)) return start + (end - start) * contact;
            from = to;
        }
        return dt;
    }

    private void RefreshMegaInterceptionSweeps()
    {
        _megaInterceptionSweeps.Clear();
        _megaInterceptionWorldRevision = World.Revision;
        _megaInterceptionTopologyRevision = _topologyRevision;
        foreach (var flight in _megaFlights)
        {
            var sweeps = new List<MegaFlightSweep>();
            _megaInterceptionSweeps.Add(flight, sweeps);
            var position = flight.Position;
            var velocity = flight.Velocity;
            float travelBound = (velocity.Length() + MegaWeaponGravity * _megaInterceptionDuration) * _megaInterceptionDuration;
            int samples = Math.Max(1, (int)MathF.Ceiling(travelBound / .08f));
            float slice = _megaInterceptionDuration / samples;
            for (int sample = 0; sample < samples; sample++)
            {
                var next = position + velocity * slice - Vector3.UnitY * (MegaWeaponGravity * slice * slice * .5f);
                bool hit = FindMegaWeaponImpact(flight, position, velocity, slice, out float fraction, out _, out _);
                float stop = hit ? Math.Clamp(fraction, 0, 1) : 1;
                float elapsed = slice * stop;
                var endpoint = position + velocity * elapsed - Vector3.UnitY * (MegaWeaponGravity * elapsed * elapsed * .5f);
                sweeps.Add(new MegaFlightSweep(sample * slice, (sample + stop) * slice,
                    position, endpoint, hit));
                if (hit) break;
                position = next;
                velocity -= Vector3.UnitY * (MegaWeaponGravity * slice);
                if (flight.Age + (sample + 1) * slice >= 12 || position.Y <= -2 || position.Y >= WorldGrid.Size + 8 ||
                    position.X is <= -4 or >= WorldGrid.Size + 4 || position.Z is <= -4 or >= WorldGrid.Size + 4) break;
            }
        }
    }

    private bool TryFindMegaFlightImpact(Vector3 from, Vector3 to, float startTime, float endTime,
        out float fraction, out MegaWeaponFlight? flightHit, out HeldKiloBlock? blockHit)
    {
        if (ProjectileDiagnostics is { } diagnostics) diagnostics.FlightQueries++;
        fraction = float.PositiveInfinity;
        flightHit = null;
        blockHit = null;
        if (_megaInterceptionWorldRevision != World.Revision || _megaInterceptionTopologyRevision != _topologyRevision)
            RefreshMegaInterceptionSweeps();
        float duration = endTime - startTime;
        if (duration <= 0) return false;
        foreach (var (flight, sweeps) in _megaInterceptionSweeps)
            foreach (var sweep in sweeps)
            {
                float firstTime = Math.Max(startTime, sweep.Start);
                float lastTime = Math.Min(endTime, sweep.End);
                if (firstTime > lastTime) continue;
                float first = (firstTime - startTime) / duration;
                if (first > fraction) continue;
                float last = (lastTime - startTime) / duration;
                float sweepDuration = sweep.End - sweep.Start;
                var blockFrom = sweepDuration > 0 ? Vector3.Lerp(sweep.From, sweep.To, (firstTime - sweep.Start) / sweepDuration) : sweep.From;
                var blockTo = sweepDuration > 0 ? Vector3.Lerp(sweep.From, sweep.To, (lastTime - sweep.Start) / sweepDuration) : sweep.To;
                var shotFrom = Vector3.Lerp(from, to, first);
                var shotTo = Vector3.Lerp(from, to, last);
                foreach (var block in flight.Blocks)
                {
                    // In the moving block's frame this is a point segment against
                    // a stationary cube; fast crossing paths cannot tunnel past it.
                    var relative = shotFrom - blockFrom - block.Offset;
                    var delta = (shotTo - shotFrom) - (blockTo - blockFrom);
                    if (!ProjectileBoxIntersection(relative, delta, new Vector3(-.5f), new Vector3(.5f), out var hit)) continue;
                    float contact = first + (last - first) * hit;
                    if (contact >= fraction) continue;
                    fraction = contact;
                    flightHit = flight;
                    blockHit = block;
                }
            }
        return flightHit != null;
    }

    private void DamageMegaFlightBlock(MegaWeaponFlight flight, HeldKiloBlock block, float damage)
    {
        if (damage <= 0 || !float.IsFinite(damage)) return;
        block.Hp -= damage;
        if (block.Hp > 0) return;
        flight.Blocks.Remove(block);
        if (flight.Blocks.Count == 0) _megaFlights.Remove(flight);
        // Destroyed matter has no mining drop. Other parts retain their offsets,
        // motion and original quarter shares rather than gaining free damage.
        RefreshMegaInterceptionSweeps();
    }
}
