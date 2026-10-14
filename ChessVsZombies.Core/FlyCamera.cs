using System.Numerics;

namespace ChessVsZombies.Core;

/// <summary>A collision-aware free-flight camera. Local Z input always moves horizontally.</summary>
public sealed class FlyCamera
{
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public const float Radius = .2f;
    public FlyCamera(Vector3? position = null, float yaw = -2.66f, float pitch = -.41f)
    {
        Position = position ?? new Vector3(27, 25, 37);
        Yaw = yaw; Pitch = pitch;
    }
    public Vector3 Forward => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch));
    public void Look(float horizontalPixels, float verticalPixels)
    {
        Yaw -= horizontalPixels * .0025f;
        Pitch = Math.Clamp(Pitch - verticalPixels * .0025f, -1.48f, 1.48f);
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
