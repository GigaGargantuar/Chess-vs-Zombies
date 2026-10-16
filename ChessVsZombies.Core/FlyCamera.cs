using System.Numerics;

namespace ChessVsZombies.Core;

/// <summary>A collision-aware free-flight camera. Local Z input always moves horizontally.</summary>
public sealed class FlyCamera
{
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public Quaternion Orientation { get; private set; }
    public const float Radius = .2f;
    private const float MaximumPitch = MathF.PI / 2;
    private const float LookSensitivity = .0025f;
    public FlyCamera(Vector3? position = null, float yaw = -2.66f, float pitch = -.41f)
    {
        if (!float.IsFinite(yaw)) throw new ArgumentOutOfRangeException(nameof(yaw));
        if (!float.IsFinite(pitch)) throw new ArgumentOutOfRangeException(nameof(pitch));
        Position = position ?? new Vector3(27, 25, 37);
        Yaw = NormalizeYaw(yaw); Pitch = Math.Clamp(pitch, -MaximumPitch, MaximumPitch);
        UpdateOrientation();
    }
    // Quaternion rotation can leave tiny horizontal rounding errors at a pole.
    // Preserve an exact vertical ray while retaining the quaternion's stable up direction.
    public Vector3 Forward => Pitch == MaximumPitch ? Vector3.UnitY : Pitch == -MaximumPitch ? -Vector3.UnitY
        : Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, Orientation));
    public Vector3 Up => Vector3.Normalize(Vector3.Transform(Vector3.UnitY, Orientation));
    public void Look(float horizontalPixels, float verticalPixels)
    {
        if (!float.IsFinite(horizontalPixels) || !float.IsFinite(verticalPixels)) return;
        Yaw = NormalizeYaw((double)Yaw - (double)horizontalPixels * LookSensitivity);
        Pitch = (float)Math.Clamp((double)Pitch - (double)verticalPixels * LookSensitivity, -MaximumPitch, MaximumPitch);
        UpdateOrientation();
    }
    private static float NormalizeYaw(double yaw) => (float)Math.IEEERemainder(yaw, Math.Tau);
    private void UpdateOrientation()
    {
        // Keep traditional world-up yaw and local-right pitch input. The quaternion
        // represents that final view without introducing roll from mouse panning.
        Orientation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(Yaw, -Pitch, 0));
    }
    public void Move(Vector3 localInput, float distance, Func<Cell, bool> solid)
        => MovePrecise(localInput, distance, point => solid(new Cell((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z))));

    public void MovePrecise(Vector3 localInput, float distance, Func<Vector3, bool> solid)
    {
        if (localInput.LengthSquared() <= 0 || distance <= 0) return;
        localInput = Vector3.Normalize(localInput);
        Vector3 horizontalForward = new(MathF.Sin(Yaw), 0, MathF.Cos(Yaw));
        Vector3 right = new(-horizontalForward.Z, 0, horizontalForward.X);
        var movement = (right * localInput.X + Vector3.UnitY * localInput.Y + horizontalForward * localInput.Z) * distance;
        // Split long movements so delayed frames cannot tunnel through one-cell walls.
        int steps = Math.Max(1, (int)MathF.Ceiling(movement.Length() / .15f));
        movement /= steps;
        for (int step = 0; step < steps; step++) for (int axis = 0; axis < 3; axis++)
            {
                var trial = Position;
                if (axis == 0) trial.X += movement.X; else if (axis == 1) trial.Y += movement.Y; else trial.Z += movement.Z;
                bool blocked = false;
                for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
                        {
                            var p = trial + new Vector3(x, y, z) * Radius;
                            if (solid(p)) blocked = true;
                        }
                if (!blocked) Position = trial;
            }
        Position = Vector3.Clamp(Position, new Vector3(-8, .3f, -8), new Vector3(40, 45, 44));
    }
    public bool Intersects(Cell cell) => Position.X + Radius > cell.X && Position.X - Radius < cell.X + 1 &&
        Position.Y + Radius > cell.Y && Position.Y - Radius < cell.Y + 1 && Position.Z + Radius > cell.Z && Position.Z - Radius < cell.Z + 1;
}
