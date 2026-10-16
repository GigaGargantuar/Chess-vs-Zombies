using System.Numerics;

namespace ChessVsZombies.Core;

/// <summary>Camera controls and ray picking for the isolated jump formation editor.</summary>
public sealed class JumpEditorModel
{
    public const float MinimumZoom = 3f, MaximumZoom = 160f;
    public float Zoom { get; private set; } = 12f;
    public float Yaw { get; private set; } = .72f;
    public float Pitch { get; private set; } = .48f;
    public int Layer { get; private set; }
    public bool CrossSection { get; private set; }

    public Vector3 CameraPosition => Zoom * new Vector3(
        MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch));

    public void ZoomBy(int wheelSteps) => Zoom = Math.Clamp(Zoom * MathF.Pow(.85f, wheelSteps), MinimumZoom, MaximumZoom);
    public void Rotate(float horizontalPixels, float verticalPixels)
    {
        Yaw += horizontalPixels * .006f;
        Pitch = Math.Clamp(Pitch + verticalPixels * .006f, -.95f, .95f);
    }
    public void SetLayer(int layer, int radius) => Layer = Math.Clamp(layer, -radius, radius);
    public void ToggleCrossSection() => CrossSection = !CrossSection;
    public bool IsVisible(Cell cell) => !CrossSection || cell.Y == Layer;

    public bool TryAdd(ICollection<Cell> pattern, Cell cell, int radius)
    {
        long distance = JumpRules.FormationRadius([cell]);
        if (distance < 2 || distance > radius || cell.Y != Layer || pattern.Contains(cell)) return false;
        pattern.Add(cell);
        return true;
    }

    public Cell? PickExisting(IEnumerable<Cell> pattern, Vector3 origin, Vector3 direction)
    {
        if (!Finite(origin) || !Finite(direction) || direction.LengthSquared() < .000001f) return null;
        direction = Vector3.Normalize(direction);
        Cell? closest = null;
        float distance = float.PositiveInfinity;
        foreach (var cell in pattern)
        {
            if (!IsVisible(cell)) continue;
            if (!HitBox(origin, direction, new Vector3(cell.X, cell.Y, cell.Z), .38f, out float hit) || hit >= distance) continue;
            distance = hit;
            closest = cell;
        }
        return closest;
    }

    public Cell? PickPlacement(Vector3 origin, Vector3 direction, int radius)
    {
        if (!Finite(origin) || !Finite(direction) || !float.IsFinite(direction.Y) ||
            MathF.Abs(direction.Y) < .0001f || radius < 2) return null;
        float distance = (Layer - origin.Y) / direction.Y;
        if (!float.IsFinite(distance) || distance <= 0) return null;
        var point = origin + direction * distance;
        if (!Finite(point) || MathF.Abs(point.X) > radius + .5f || MathF.Abs(point.Z) > radius + .5f) return null;
        var cell = new Cell((int)MathF.Floor(point.X + .5f), Layer, (int)MathF.Floor(point.Z + .5f));
        return cell.Chebyshev is >= 2 && cell.Chebyshev <= radius ? cell : null;
    }

    private static bool HitBox(Vector3 origin, Vector3 direction, Vector3 center, float half, out float hit)
    {
        float near = 0, far = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float p = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float c = axis == 0 ? center.X : axis == 1 ? center.Y : center.Z;
            if (MathF.Abs(d) < .000001f)
            {
                if (p < c - half || p > c + half) { hit = 0; return false; }
                continue;
            }
            float a = (c - half - p) / d, b = (c + half - p) / d;
            near = MathF.Max(near, MathF.Min(a, b));
            far = MathF.Min(far, MathF.Max(a, b));
            if (near > far) { hit = 0; return false; }
        }
        hit = near;
        return far > 0;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
