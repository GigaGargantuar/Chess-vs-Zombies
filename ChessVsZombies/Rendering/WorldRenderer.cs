using ChessVsZombies.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessVsZombies.Rendering;

public sealed class WorldRenderer : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly BasicEffect effect;
    private VertexBuffer? terrain;
    private int revision = -1;
    private WorldGrid? previousWorld;
    private readonly MeshBuilder dynamic = new();
    private static readonly Color Teal = new(97, 225, 193);
    public WorldRenderer(GraphicsDevice device)
    {
        this.device = device;
        effect = new BasicEffect(device) { VertexColorEnabled = true, LightingEnabled = false, FogEnabled = true, FogColor = new Vector3(.043f, .067f, .10f), FogStart = 48, FogEnd = 100 };
    }
    public void Draw(GameSession session, Matrix view, Matrix projection, Cell? hover, Cell? placement, bool placementValid, Turret? selected, bool showZones, double time)
    {
        if (revision != session.World.Revision || previousWorld != session.World) RebuildTerrain(session.World);
        device.DepthStencilState = DepthStencilState.Default;
        device.BlendState = BlendState.Opaque;
        device.RasterizerState = RasterizerState.CullNone;
        effect.View = view; effect.Projection = projection; effect.World = Matrix.Identity;
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            if (terrain is not null) { device.SetVertexBuffer(terrain); device.DrawPrimitives(PrimitiveType.TriangleList, 0, terrain.VertexCount / 3); }
        }
        dynamic.Vertices.Clear();
        // A raised crown marks the two-by-two-by-two objective.
        dynamic.Cylinder(new Vector3(16, 17.035f, 16), .54f, .54f, .10f, new Color(225, 244, 226), 8);
        dynamic.Cylinder(new Vector3(16, 17.13f, 16), .4f, .15f, .72f, Teal, 8);
        dynamic.Box(new Vector3(15.87f, 17.76f, 15.87f), new Vector3(16.13f, 18.17f, 16.13f), new Color(229, 255, 239));
        dynamic.Box(new Vector3(15.7f, 17.90f, 15.88f), new Vector3(16.3f, 18.02f, 16.12f), new Color(229, 255, 239));
        foreach (var turret in session.Turrets) DrawTurret(turret, turret == selected);
        foreach (var zombie in session.Zombies) DrawZombie(zombie, time);
        foreach (var projectile in session.Projectiles)
        {
            var position = ToXna(projectile.Position);
            Color color = projectile.Kind == ShotKind.Lob ? new(239, 190, 102) : new(163, 239, 255);
            float radius = projectile.Kind == ShotKind.Melee ? .09f : .065f;
            dynamic.Box(position - new Vector3(radius), position + new Vector3(radius), color);
            if (projectile.Kind != ShotKind.Lob) dynamic.Line(position, Vector3.Lerp(position, ToXna(projectile.Origin), .25f), .02f, color);
        }
        if (showZones)
        {
            dynamic.Outline(new Vector3(12, 16.015f, 12), new Vector3(20, 16.04f, 20), Teal, .025f);
            dynamic.Outline(new Vector3(1, 16.01f, 1), new Vector3(31, 16.03f, 31), new Color(91, 138, 163), .024f);
            dynamic.Outline(new Vector3(0, 16.01f, 0), new Vector3(32, 16.03f, 32), new Color(200, 94, 86), .035f);
            // Short pillars communicate the upper build boundary without obscuring the field.
            foreach (int x in new[] { 12, 20 }) foreach (int z in new[] { 12, 20 })
                    dynamic.Line(new Vector3(x, 16, z), new Vector3(x, 20, z), .012f, new Color(59, 115, 108));
        }
        if (selected is not null)
        {
            foreach (var offset in session.Economy.GetAttackPattern(selected))
            {
                var cell = selected.Cell + offset;
                if (!WorldGrid.IsInside(cell) || session.World.GetMaterial(cell) != Material.Air) continue;
                var min = new Vector3(cell.X + .12f, cell.Y + .02f, cell.Z + .12f);
                dynamic.Outline(min, new Vector3(cell.X + .88f, cell.Y + .045f, cell.Z + .88f), new Color(152, 138, 77), .014f);
            }
        }
        if (hover is { } target) dynamic.Outline(new Vector3(target.X, target.Y, target.Z) - new Vector3(.01f), new Vector3(target.X + 1, target.Y + 1, target.Z + 1) + new Vector3(.01f), new Color(196, 210, 214), .012f);
        if (placement is { } p)
        {
            var color = placementValid ? Teal : new Color(237, 106, 97);
            dynamic.Outline(new Vector3(p.X + .04f, p.Y + .025f, p.Z + .04f), new Vector3(p.X + .96f, p.Y + .8f, p.Z + .96f), color, .022f);
        }
        if (dynamic.Vertices.Count > 0)
        {
            var vertices = dynamic.Vertices.ToArray();
            foreach (var pass in effect.CurrentTechnique.Passes) { pass.Apply(); device.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, vertices.Length / 3); }
        }
    }
    private void RebuildTerrain(WorldGrid world)
    {
        var mesh = new MeshBuilder();
        for (int x = 0; x < 32; x++) for (int y = 0; y < 32; y++) for (int z = 0; z < 32; z++)
                {
                    var cell = new Cell(x, y, z); var material = world.GetMaterial(cell);
                    if (material == Material.Air) continue;
                    Color color = material switch
                    {
                        Material.Core => new Color(82, 205, 183),
                        Material.Barrier => world.GetBarrier(cell)?.Content == Material.Stone ? new Color(160, 174, 184) : new Color(155, 118, 78),
                        Material.Dirt => new Color(112 + (x + z) % 2 * 5, 105 + (x + z) % 2 * 5, 83 + (x + z) % 2 * 3),
                        _ => new Color(60 + y % 3 * 4, 72 + y % 3 * 4, 81 + y % 3 * 4)
                    };
                    if (material == Material.Dirt && y == 15)
                    {
                        if (x == 0 || z == 0 || x == 31 || z == 31) color = new Color(106, 79, 73);
                        else if (x >= 12 && x < 20 && z >= 12 && z < 20) color = Color.Lerp(color, new Color(91, 153, 126), .3f);
                    }
                    bool Empty(int dx, int dy, int dz) => world.GetMaterial(new Cell(x + dx, y + dy, z + dz)) == Material.Air;
                    bool top = Empty(0, 1, 0);
                    mesh.Box(new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1), color, top, Empty(0, -1, 0), Empty(-1, 0, 0), Empty(1, 0, 0), Empty(0, 0, 1), Empty(0, 0, -1));
                    if (top)
                    {
                        float gap = material == Material.Core ? .035f : .015f;
                        var lineColor = material == Material.Core ? new Color(172, 255, 220) : color * .76f;
                        mesh.Line(new Vector3(x + gap, y + 1.003f, z + gap), new Vector3(x + 1 - gap, y + 1.003f, z + gap), gap, lineColor);
                        mesh.Line(new Vector3(x + gap, y + 1.003f, z + gap), new Vector3(x + gap, y + 1.003f, z + 1 - gap), gap, lineColor);
                    }
                }
        terrain?.Dispose();
        terrain = new VertexBuffer(device, typeof(VertexPositionColor), mesh.Vertices.Count, BufferUsage.WriteOnly);
        terrain.SetData(mesh.Vertices.ToArray()); revision = world.Revision; previousWorld = world;
    }
    private void DrawTurret(Turret turret, bool selected)
    {
        var p = new Vector3(turret.Cell.X + .5f, turret.Cell.Y + .02f, turret.Cell.Z + .5f);
        var ivory = selected ? new Color(197, 255, 229) : new Color(226, 224, 205);
        var accent = new Color(99, 185, 164);
        dynamic.Cylinder(p, .42f, .42f, .12f, new Color(39, 54, 61));
        dynamic.Cylinder(p + Vector3.Up * .12f, .36f, .3f, .14f, ivory);
        dynamic.Cylinder(p + Vector3.Up * .26f, .3f, .17f, .24f, ivory);
        switch (turret.Kind)
        {
            case PieceKind.Pawn:
                dynamic.Cylinder(p + Vector3.Up * .5f, .17f, .22f, .13f, ivory);
                dynamic.Cylinder(p + Vector3.Up * .63f, .22f, .1f, .22f, ivory); break;
            case PieceKind.Rook:
                dynamic.Cylinder(p + Vector3.Up * .46f, .21f, .24f, .35f, ivory, 8);
                dynamic.Cylinder(p + Vector3.Up * .8f, .32f, .32f, .12f, accent, 8);
                foreach (int x in new[] { -1, 1 }) foreach (int z in new[] { -1, 1 }) dynamic.Box(p + new Vector3(x * .20f - .075f, .9f, z * .20f - .075f), p + new Vector3(x * .20f + .075f, 1.06f, z * .20f + .075f), ivory); break;
            case PieceKind.Bishop:
                dynamic.Cylinder(p + Vector3.Up * .5f, .17f, .28f, .25f, ivory, 8);
                dynamic.Cylinder(p + Vector3.Up * .75f, .28f, .035f, .36f, ivory, 8);
                dynamic.Box(p + new Vector3(-.025f, .86f, -.285f), p + new Vector3(.025f, 1.025f, -.05f), accent); break;
            case PieceKind.Knight:
                dynamic.Box(p + new Vector3(-.16f, .48f, -.11f), p + new Vector3(.16f, .94f, .15f), ivory);
                dynamic.Box(p + new Vector3(-.18f, .77f, -.37f), p + new Vector3(.18f, 1.02f, .12f), ivory);
                dynamic.Box(p + new Vector3(-.13f, 1.02f, .005f), p + new Vector3(.13f, 1.18f, .105f), accent);
                dynamic.Box(p + new Vector3(-.19f, .9f, -.26f), p + new Vector3(.19f, .95f, -.20f), new Color(22, 45, 48)); break;
        }
        if (turret.UpgradePoints.Values.Any(points => points > 0)) dynamic.Cylinder(p + Vector3.Up * .27f, .305f, .305f, .04f, new Color(235, 189, 105));
    }
    private void DrawZombie(Zombie zombie, double time)
    {
        var p = ToXna(zombie.Position) - new Vector3(0, .5f, 0);
        var skin = new Color(115, 144, 99); var dark = new Color(43, 63, 53);
        float stride = MathF.Sin((float)time * 7 + zombie.Id) * .08f;
        dynamic.Box(p + new Vector3(-.21f, .04f + stride, -.14f), p + new Vector3(-.045f, .39f + stride, .14f), dark);
        dynamic.Box(p + new Vector3(.045f, .04f - stride, -.14f), p + new Vector3(.21f, .39f - stride, .14f), dark);
        dynamic.Box(p + new Vector3(-.24f, .38f, -.17f), p + new Vector3(.24f, .84f, .17f), skin);
        dynamic.Box(p + new Vector3(-.20f, .84f, -.19f), p + new Vector3(.20f, 1.21f, .19f), skin);
        dynamic.Box(p + new Vector3(-.39f, .42f, -.11f), p + new Vector3(-.25f, .87f, .14f), skin);
        dynamic.Box(p + new Vector3(.25f, .42f, -.11f), p + new Vector3(.39f, .87f, .14f), skin);
        dynamic.Box(p + new Vector3(-.15f, 1.02f, .195f), p + new Vector3(.15f, 1.07f, .21f), new Color(240, 122, 91));
        var tier = zombie.Tier switch { GearTier.Wood => new Color(137, 100, 67), GearTier.Stone => new Color(137, 146, 155), GearTier.Copper => new Color(188, 123, 83), GearTier.Iron => new Color(189, 201, 207), GearTier.Gold => new Color(241, 186, 62), GearTier.Diamond => new Color(100, 207, 233), _ => new Color(107, 101, 139) };
        if (zombie.Kind is ZombieKind.Armored or ZombieKind.ArmedArmored)
        {
            dynamic.Box(p + new Vector3(-.255f, .45f, -.19f), p + new Vector3(.255f, .83f, .19f), tier);
            dynamic.Box(p + new Vector3(-.22f, 1.13f, -.22f), p + new Vector3(.22f, 1.28f, .22f), tier);
            dynamic.Box(p + new Vector3(-.49f, .36f, .12f), p + new Vector3(-.26f, .9f, .24f), tier);
        }
        if (zombie.Kind is ZombieKind.Armed or ZombieKind.ArmedArmored)
            dynamic.Box(p + new Vector3(.35f, .33f, -.09f), p + new Vector3(.46f, 1.10f, .04f), tier);
        if (zombie.Hp < zombie.MaxHp)
        {
            dynamic.Box(p + new Vector3(-.3f, 1.42f, -.025f), p + new Vector3(.3f, 1.47f, .025f), new Color(47, 45, 43));
            dynamic.Box(p + new Vector3(-.3f, 1.43f, -.029f), p + new Vector3(-.3f + .6f * zombie.Hp / zombie.MaxHp, 1.48f, .029f), new Color(233, 126, 106));
        }
    }
    public static Vector3 ToXna(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    public void Dispose() { terrain?.Dispose(); effect.Dispose(); }
}
