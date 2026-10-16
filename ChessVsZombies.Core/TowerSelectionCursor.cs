using System.Numerics;

namespace ChessVsZombies.Core;

/// <summary>Places a selection corner at a chosen distance along the camera ray, including empty space.</summary>
public static class TowerSelectionCursor
{
    public static Cell? Pick(Vector3 origin, Vector3 direction, float distance)
    {
        if (!Finite(origin) || !Finite(direction) || !float.IsFinite(distance) || distance <= 0) return null;
        double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y + (double)direction.Z * direction.Z);
        if (length == 0) return null;

        double x = origin.X + direction.X / length * distance;
        double y = origin.Y + direction.Y / length * distance;
        double z = origin.Z + direction.Z / length * distance;
        // Check before converting to integers: an out-of-world corner must never snap to an edge cell.
        if (x < 0 || x >= WorldGrid.Size || y < 0 || y >= WorldGrid.Size || z < 0 || z >= WorldGrid.Size) return null;
        return new Cell((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
