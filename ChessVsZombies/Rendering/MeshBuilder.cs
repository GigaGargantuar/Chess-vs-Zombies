using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessVsZombies.Rendering;

public sealed class MeshBuilder
{
    public readonly List<VertexPositionColor> Vertices = [];
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        Vertices.Add(new(a, color)); Vertices.Add(new(b, color)); Vertices.Add(new(c, color));
        Vertices.Add(new(a, color)); Vertices.Add(new(c, color)); Vertices.Add(new(d, color));
    }
    public void Box(Vector3 min, Vector3 max, Color color, bool top = true, bool bottom = true, bool left = true, bool right = true, bool front = true, bool back = true)
    {
        var a = min; var b = max;
        if (top) Quad(new(a.X, b.Y, a.Z), new(b.X, b.Y, a.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z), color);
        if (bottom) Quad(new(a.X, a.Y, a.Z), new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), new(b.X, a.Y, a.Z), color * .48f);
        if (left) Quad(new(a.X, a.Y, a.Z), new(a.X, b.Y, a.Z), new(a.X, b.Y, b.Z), new(a.X, a.Y, b.Z), color * .65f);
        if (right) Quad(new(b.X, a.Y, b.Z), new(b.X, b.Y, b.Z), new(b.X, b.Y, a.Z), new(b.X, a.Y, a.Z), color * .82f);
        if (front) Quad(new(a.X, a.Y, b.Z), new(a.X, b.Y, b.Z), new(b.X, b.Y, b.Z), new(b.X, a.Y, b.Z), color * .72f);
        if (back) Quad(new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z), new(a.X, a.Y, a.Z), color * .9f);
    }
    public void Cylinder(Vector3 center, float bottomRadius, float topRadius, float height, Color color, int segments = 12)
    {
        for (int i = 0; i < segments; i++)
        {
            float a = i * MathHelper.TwoPi / segments, b = (i + 1) * MathHelper.TwoPi / segments;
            var lowA = center + new Vector3(MathF.Cos(a) * bottomRadius, 0, MathF.Sin(a) * bottomRadius);
            var lowB = center + new Vector3(MathF.Cos(b) * bottomRadius, 0, MathF.Sin(b) * bottomRadius);
            var highA = center + new Vector3(MathF.Cos(a) * topRadius, height, MathF.Sin(a) * topRadius);
            var highB = center + new Vector3(MathF.Cos(b) * topRadius, height, MathF.Sin(b) * topRadius);
            Quad(lowA, lowB, highB, highA, color * (.68f + .25f * (MathF.Cos(a) * .5f + .5f)));
            Vertices.Add(new(highA, color)); Vertices.Add(new(highB, color)); Vertices.Add(new(center + Vector3.Up * height, color));
        }
    }
    public void Line(Vector3 a, Vector3 b, float width, Color color)
    {
        var delta = b - a;
        var side = Vector3.Cross(delta, Vector3.Up);
        if (side.LengthSquared() < .0001f) side = Vector3.Right;
        side.Normalize(); side *= width;
        Quad(a - side, a + side, b + side, b - side, color);
        var up = Vector3.Cross(delta, side); if (up.LengthSquared() > .0001f) { up.Normalize(); up *= width; Quad(a - up, a + up, b + up, b - up, color); }
    }
    public void Outline(Vector3 min, Vector3 max, Color color, float width = .012f)
    {
        Vector3[] p = [new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z), new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z)];
        for (int i = 0; i < 4; i++) { Line(p[i], p[(i + 1) % 4], width, color); Line(p[i + 4], p[(i + 1) % 4 + 4], width, color); Line(p[i], p[i + 4], width, color); }
    }
}
