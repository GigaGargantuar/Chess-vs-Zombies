using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private static void MegaBallisticBounds(Vector3 from, Vector3 velocity, float duration, Vector3 half,
        out Vector3 min, out Vector3 max)
    {
        double time = duration, gravity = MegaWeaponGravity;
        double endX = from.X + (double)velocity.X * time;
        double endY = from.Y + (double)velocity.Y * time - gravity * time * time * .5;
        double endZ = from.Z + (double)velocity.Z * time;
        double top = Math.Max(from.Y, endY);
        double apex = velocity.Y / gravity;
        if (apex > 0 && apex < time)
            top = Math.Max(top, from.Y + (double)velocity.Y * apex - gravity * apex * apex * .5);

        // Round only the complete bounds, then move outward to retain contacts
        // at integer faces despite float rounding in expanded collision boxes.
        min = new Vector3(
            MathF.BitDecrement((float)(Math.Min(from.X, endX) - half.X)),
            MathF.BitDecrement((float)(Math.Min(from.Y, endY) - half.Y)),
            MathF.BitDecrement((float)(Math.Min(from.Z, endZ) - half.Z)));
        max = new Vector3(
            MathF.BitIncrement((float)(Math.Max(from.X, endX) + half.X)),
            MathF.BitIncrement((float)(top + half.Y)),
            MathF.BitIncrement((float)(Math.Max(from.Z, endZ) + half.Z)));
    }

    private static bool MegaBallisticBoxIntersection(Vector3 from, Vector3 velocity, float duration,
        Vector3 min, Vector3 max, out float fraction)
    {
        fraction = float.PositiveInfinity;
        if (!float.IsFinite(duration) || duration <= 0 || !ProjectilePointIsFinite(from) ||
            !ProjectilePointIsFinite(velocity) || !ProjectilePointIsFinite(min) || !ProjectilePointIsFinite(max) ||
            min.X > max.X || min.Y > max.Y || min.Z > max.Z) return false;

        double enter = 0, leave = duration;
        if (!LinearInterval(from.X, velocity.X, min.X, max.X, ref enter, ref leave) ||
            !LinearInterval(from.Z, velocity.Z, min.Z, max.Z, ref enter, ref leave)) return false;

        // Solve the vertical parabola in seconds, independently of the sweep's
        // duration. Shortening an event interval must not move its contact time.
        double gravity = MegaWeaponGravity;
        double verticalSpeed = velocity.Y;
        double lowerDiscriminant = verticalSpeed * verticalSpeed + 2 * gravity * ((double)from.Y - min.Y);
        if (lowerDiscriminant < 0) return false; // Even its apex is below the box.
        double upperDiscriminant = verticalSpeed * verticalSpeed + 2 * gravity * ((double)from.Y - max.Y);
        double lowerRoot = Math.Sqrt(lowerDiscriminant);
        double upperRoot = upperDiscriminant >= 0 ? Math.Sqrt(upperDiscriminant) : 0;
        double apex = verticalSpeed / gravity;

        // Each side of the apex is monotonic. On the ascending side the lower
        // face is entered first; on the descending side the upper face is.
        if (enter <= apex && VerticalInterval(enter, Math.Min(leave, apex), ascending: true, out var contact) ||
            leave >= apex && VerticalInterval(Math.Max(enter, apex), leave, ascending: false, out contact))
        {
            fraction = (float)Math.Clamp(contact / duration, 0, 1);
            return true;
        }
        return false;

        bool VerticalInterval(double first, double last, bool ascending, out double contact)
        {
            double firstCrossing = ascending
                ? (verticalSpeed - lowerRoot) / gravity
                : upperDiscriminant >= 0 ? (verticalSpeed + upperRoot) / gravity : double.NegativeInfinity;
            double lastCrossing = ascending
                ? upperDiscriminant >= 0 ? (verticalSpeed - upperRoot) / gravity : double.PositiveInfinity
                : (verticalSpeed + lowerRoot) / gravity;
            contact = Math.Max(first, firstCrossing);
            double end = Math.Min(last, lastCrossing);
            // A sole outgoing touch at t=0 has no remaining collision interval.
            // Incoming endpoint contacts and gravity entering from the top do.
            return contact <= end && end > 0;
        }

        static bool LinearInterval(double origin, double speed, double lower, double upper,
            ref double first, ref double last)
        {
            if (speed == 0) return origin >= lower && origin < upper;
            double a = (lower - origin) / speed, b = (upper - origin) / speed;
            first = Math.Max(first, Math.Min(a, b));
            last = Math.Min(last, Math.Max(a, b));
            return first <= last && last > 0;
        }
    }
}
