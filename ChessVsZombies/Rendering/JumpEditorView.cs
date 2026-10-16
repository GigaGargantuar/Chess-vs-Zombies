using ChessVsZombies.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NVector3 = System.Numerics.Vector3;

namespace ChessVsZombies.Rendering;

/// <summary>A separate 3D scene for editing relative lob destinations.</summary>
public sealed class JumpEditorView : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly BasicEffect effect;
    private readonly MeshBuilder mesh = new();
    private static readonly Color Grid = new(54, 91, 105), Active = new(107, 226, 193);

    public JumpEditorView(GraphicsDevice device)
    {
        this.device = device;
        effect = new BasicEffect(device) { VertexColorEnabled = true, LightingEnabled = false };
    }

    public void Draw(JumpEditorModel model, IReadOnlyCollection<Cell> pattern, int radius, Cell? preview)
    {
        var (view, projection) = Matrices(model);
        mesh.Vertices.Clear();
        float plane = model.Layer - .48f;
        for (int n = -radius; n <= radius + 1; n++)
        {
            float coordinate = n - .5f;
            Color color = n == 0 ? Active * .55f : Grid * (model.CrossSection ? .9f : .55f);
            mesh.Line(new Vector3(coordinate, plane, -radius - .5f), new Vector3(coordinate, plane, radius + .5f), .009f, color);
            mesh.Line(new Vector3(-radius - .5f, plane, coordinate), new Vector3(radius + .5f, plane, coordinate), .009f, color);
        }
        mesh.Line(new Vector3(-radius - .7f, 0, 0), new Vector3(radius + .7f, 0, 0), .018f, new Color(236, 122, 112));
        mesh.Line(new Vector3(0, -radius - .7f, 0), new Vector3(0, radius + .7f, 0), .018f, new Color(134, 188, 251));
        mesh.Line(new Vector3(0, 0, -radius - .7f), new Vector3(0, 0, radius + .7f), .018f, new Color(232, 190, 114));
        Box(Vector3.Zero, .43f, new Color(244, 185, 101));
        foreach (var cell in pattern)
        {
            if (!model.IsVisible(cell)) continue;
            Box(new Vector3(cell.X, cell.Y, cell.Z), .34f, Active);
        }
        if (preview is { } candidate && !pattern.Contains(candidate))
            mesh.Outline(new Vector3(candidate.X, candidate.Y, candidate.Z) - new Vector3(.42f),
                new Vector3(candidate.X, candidate.Y, candidate.Z) + new Vector3(.42f), new Color(255, 236, 159), .026f);

        device.DepthStencilState = DepthStencilState.Default;
        device.BlendState = BlendState.NonPremultiplied;
        device.RasterizerState = RasterizerState.CullNone;
        effect.World = Matrix.Identity; effect.View = view; effect.Projection = projection;
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            device.DrawUserPrimitives(PrimitiveType.TriangleList, mesh.Vertices.ToArray(), 0, mesh.Vertices.Count / 3);
        }
    }

    public (NVector3 Origin, NVector3 Direction) Ray(JumpEditorModel model, int mouseX, int mouseY)
    {
        var (view, projection) = Matrices(model);
        var viewport = device.Viewport;
        var near = viewport.Unproject(new Vector3(mouseX, mouseY, 0), projection, view, Matrix.Identity);
        var far = viewport.Unproject(new Vector3(mouseX, mouseY, 1), projection, view, Matrix.Identity);
        var direction = Vector3.Normalize(far - near);
        return (new NVector3(near.X, near.Y, near.Z), new NVector3(direction.X, direction.Y, direction.Z));
    }

    private (Matrix View, Matrix Projection) Matrices(JumpEditorModel model)
    {
        var position = model.CameraPosition;
        var view = Matrix.CreateLookAt(new Vector3(position.X, position.Y, position.Z), Vector3.Zero, Vector3.Up);
        var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(57), device.Viewport.AspectRatio, .05f, 250f);
        return (view, projection);
    }

    private void Box(Vector3 center, float half, Color color)
    {
        mesh.Box(center - new Vector3(half), center + new Vector3(half), color);
        mesh.Outline(center - new Vector3(half), center + new Vector3(half), color * .72f, .012f);
    }

    public void Dispose() => effect.Dispose();
}
