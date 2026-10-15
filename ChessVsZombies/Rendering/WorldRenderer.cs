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
    private int fallRevision = -1;
    private WorldGrid? previousWorld;
    private readonly MeshBuilder dynamic = new();
    private readonly Dictionary<int, Dictionary<ArmorSlot, GearTier>> armorSeen = [];
    private readonly List<FallenArmor> fallenArmor = [];
    private const float ZombieHeightScale = .78f;
    private readonly record struct FallenArmor(Vector3 Origin, Vector3 Drift, float Floor, Vector3 HalfSize,
        Matrix Orientation, Vector3 Spin, GearTier Tier, double Started);
    private static readonly Color Teal = new(97, 225, 193);
    public WorldRenderer(GraphicsDevice device)
    {
        this.device = device;
        effect = new BasicEffect(device) { VertexColorEnabled = true, LightingEnabled = false, FogEnabled = true, FogColor = new Vector3(.043f, .067f, .10f), FogStart = 48, FogEnd = 100 };
    }
    public void Draw(GameSession session, Matrix view, Matrix projection, Cell? hover, Cell? placement, bool placementValid, Turret? selected, bool showZones, double time)
    {
        if (previousWorld != session.World) { armorSeen.Clear(); fallenArmor.Clear(); }
        var fallingBlocks = session.Falls.Blocks.ToArray();
        var fallingCells = fallingBlocks.Select(block => block.Destination).ToHashSet();
        var fallingCrusts = fallingBlocks.Where(block => block.Crust is not null)
            .SelectMany(block => block.Crust!.Parts.Select(part => (block.Destination, part.Face))).ToHashSet();
        if (revision != session.World.Revision || previousWorld != session.World || fallRevision != session.Falls.Revision)
        {
            RebuildTerrain(session.World, fallingCells);
            fallRevision = session.Falls.Revision;
        }
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
        foreach (var crust in session.World.ObsidianCells)
        {
            foreach (var part in crust.Parts)
            {
                if (fallingCrusts.Contains((part.Cell, part.Face))) continue;
                dynamic.Box(ToXna(part.Min), ToXna(part.Max), new Color(49, 35, 66));
                dynamic.Outline(ToXna(part.Min) + new Vector3(.003f), ToXna(part.Max) - new Vector3(.003f), new Color(104, 72, 127), .008f);
            }
        }
        foreach (var block in fallingBlocks)
        {
            var offset = ToXna(block.Position - block.Destination.Center);
            if (block.Crust is { } crust)
            {
                foreach (var part in crust.Parts)
                {
                    dynamic.Box(ToXna(part.Min) + offset, ToXna(part.Max) + offset, new Color(49, 35, 66));
                    dynamic.Outline(ToXna(part.Min) + offset + new Vector3(.003f), ToXna(part.Max) + offset - new Vector3(.003f), new Color(104, 72, 127), .008f);
                }
            }
            else
            {
                var min = ToXna(block.Position) - new Vector3(.5f);
                dynamic.Box(min, min + Vector3.One, BlockColor(block.Material, block.Content, block.Destination));
            }
        }
        // A raised crown marks the two-by-two-by-two objective.
        dynamic.Cylinder(new Vector3(16, 17.035f, 16), .54f, .54f, .10f, new Color(225, 244, 226), 8);
        dynamic.Cylinder(new Vector3(16, 17.13f, 16), .4f, .15f, .72f, Teal, 8);
        dynamic.Box(new Vector3(15.87f, 17.76f, 15.87f), new Vector3(16.13f, 18.17f, 16.13f), new Color(229, 255, 239));
        dynamic.Box(new Vector3(15.7f, 17.90f, 15.88f), new Vector3(16.3f, 18.02f, 16.12f), new Color(229, 255, 239));
        foreach (var turret in session.Turrets) DrawTurret(turret, turret == selected, ToXna(session.Falls.GetTurretPosition(turret)));
        foreach (var zombie in session.Zombies)
        {
            var current = zombie.ArmorPieces.ToDictionary(piece => piece.Slot, piece => piece.Tier);
            if (armorSeen.TryGetValue(zombie.Id, out var old))
                foreach (var piece in old.Where(piece => !current.ContainsKey(piece.Key)))
                {
                    var (min, max) = ZombieArmorBounds(zombie, piece.Key);
                    var scale = ZombieMeshScale(zombie);
                    var orientation = ZombieRotation(zombie);
                    var origin = ZombieFeet(zombie) + Vector3.Transform((min + max) * .5f * scale, orientation);
                    var drift = Vector3.Transform(new Vector3(piece.Key == ArmorSlot.Shield ? -.15f : .25f, 0,
                        piece.Key == ArmorSlot.Shield ? .4f : .2f) * scale, orientation);
                    float floor = origin.Y;
                    while (floor > .1f && !session.World.IsSolidAt(new System.Numerics.Vector3(origin.X, floor - .1f, origin.Z))) floor -= .1f;
                    var spin = new Vector3(piece.Key == ArmorSlot.Shield ? 2.7f : 1.8f, 0,
                        piece.Key == ArmorSlot.Helmet ? -2.2f : 1.6f);
                    fallenArmor.Add(new FallenArmor(origin, drift, floor, (max - min) * .5f * scale,
                        orientation, spin, piece.Value, time));
                }
            armorSeen[zombie.Id] = current;
            DrawZombie(zombie, time, session.Feedback.GetFlash(zombie.Id), session.IsKiloUsingBuckets(zombie));
        }
        foreach (var id in armorSeen.Keys.Where(id => !session.Zombies.Any(zombie => zombie.Id == id)).ToArray()) armorSeen.Remove(id);
        foreach (var cover in session.KiloCovers)
        {
            if (!cover.IsActive)
            {
                var carrier = session.Zombies.FirstOrDefault(zombie => zombie.Id == cover.CarrierId);
                if (carrier is not null) DrawStowedKiloCover(carrier, cover);
                continue;
            }
            var center = (cover.Min + cover.Max) * .5f;
            var cell = new Cell((int)MathF.Floor(center.X), (int)MathF.Floor(center.Y), (int)MathF.Floor(center.Z));
            var color = BlockColor(cover.Material, null, cell);
            dynamic.Box(ToXna(cover.Min), ToXna(cover.Max), color);
            dynamic.Outline(ToXna(cover.Min) + new Vector3(.016f), ToXna(cover.Max) - new Vector3(.016f), color * .72f, .012f);
        }
        fallenArmor.RemoveAll(piece => time - piece.Started > 2.5);
        foreach (var piece in fallenArmor)
        {
            float age = (float)(time - piece.Started);
            var position = piece.Origin + piece.Drift * Math.Min(age, .8f);
            var rotation = Matrix.CreateRotationX(piece.Spin.X * age) * Matrix.CreateRotationZ(piece.Spin.Z * age) * piece.Orientation;
            float halfHeight = MathF.Abs(rotation.M12) * piece.HalfSize.X + MathF.Abs(rotation.M22) * piece.HalfSize.Y + MathF.Abs(rotation.M32) * piece.HalfSize.Z;
            position.Y = Math.Max(piece.Floor + halfHeight, piece.Origin.Y + .7f * age - 4.9f * age * age);
            int firstVertex = dynamic.Vertices.Count;
            dynamic.Box(-piece.HalfSize, piece.HalfSize, ArmorColor(piece.Tier));
            TransformVertices(firstVertex, rotation * Matrix.CreateTranslation(position));
        }
        foreach (var projectile in session.Projectiles)
        {
            var position = ToXna(projectile.Position);
            Color color = projectile.Kind == ShotKind.Lob ? new(239, 190, 102) : new(163, 239, 255);
            float radius = projectile.Kind == ShotKind.Melee ? .09f : .065f;
            dynamic.Box(position - new Vector3(radius), position + new Vector3(radius), color);
            if (projectile.Kind != ShotKind.Lob)
            {
                var tail = ToXna(projectile.GetPositionAt(Math.Max(0, projectile.Age - .025f)));
                var offset = tail - position;
                if (offset.LengthSquared() > .35f * .35f) tail = position + Vector3.Normalize(offset) * .35f;
                dynamic.Line(position, tail, .02f, color);
            }
        }
        foreach (var flight in session.Buckets.Flights)
        {
            var color = LiquidColor(flight.Kind);
            var position = ToXna(flight.Position);
            float radius = .055f + .06f * MathF.Sqrt(flight.Units / (float)Balance.BucketCapacity);
            dynamic.Box(position - new Vector3(radius), position + new Vector3(radius), color);
            dynamic.Box(position - new Vector3(radius * .55f, radius * 1.6f, radius * .55f),
                position + new Vector3(radius * .55f, radius * 1.6f, radius * .55f), color);
            // A short trail follows the actual continuing parabola, including
            // its longer descent when the landing ground is below the thrower.
            float startAge = Math.Max(0, flight.Age - .18f);
            var previous = ToXna(flight.GetPositionAt(startAge));
            for (int segment = 1; segment <= 6; segment++)
            {
                float age = MathHelper.Lerp(startAge, flight.Age, segment / 6f);
                var point = ToXna(flight.GetPositionAt(age));
                float width = .008f + .015f * segment / 6f;
                dynamic.Line(previous, point, width, color);
                previous = point;
            }
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
        DrawLiquids(session, Matrix.Invert(view).Translation, time);
    }

    private void DrawLiquids(GameSession session, Vector3 camera, double time)
    {
        var translucent = new MeshBuilder();
        foreach (var volume in session.Liquids.Cells.SelectMany(cell => session.Liquids.GetVolumes(cell.Cell))
                     .OrderByDescending(volume => Vector3.DistanceSquared(camera, ToXna((volume.Min + volume.Max) * .5f))))
        {
            var color = volume.Kind switch
            {
                LiquidKind.Water => new Color(58, 142, 211, 145), LiquidKind.Mercury => new Color(184, 197, 215, 220),
                _ => new Color(248, 102, 32, 225)
            };
            var min = ToXna(volume.Min); var max = ToXna(volume.Max);
            translucent.Box(min, max, color);
            // Fine surface lines make one-unit changes and density layers legible.
            translucent.Line(new Vector3(min.X + .02f, max.Y + .004f, min.Z + .03f),
                new Vector3(max.X - .02f, max.Y + .004f, max.Z - .03f), .007f,
                volume.Kind == LiquidKind.Lava ? new Color(255, 206, 83, 185) : new Color(218, 236, 244, 100));
        }
        foreach (var cloud in session.Liquids.Hazards)
        {
            var center = ToXna(cloud.Center.Center);
            var size = new Vector3(cloud.Radius + .5f);
            var color = cloud.Kind == DamageKind.Chemical ? new Color(157, 189, 131, 24) : new Color(227, 214, 199, 28);
            translucent.Outline(center - size, center + size, color * 2, .009f);
            for (int i = 0; i < 5; i++)
            {
                float phase = (float)time * .6f + i * 1.7f;
                var offset = new Vector3(MathF.Sin(phase) * .6f, MathF.Sin(phase * .5f) * .3f, MathF.Cos(phase) * .6f);
                var puff = new Vector3((cloud.Radius + .3f) * .55f);
                translucent.Box(center + offset - puff, center + offset + puff, color);
            }
        }
        if (translucent.Vertices.Count == 0) return;
        device.BlendState = BlendState.NonPremultiplied;
        device.DepthStencilState = DepthStencilState.DepthRead;
        var vertices = translucent.Vertices.ToArray();
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply(); device.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, vertices.Length / 3);
        }
        device.DepthStencilState = DepthStencilState.Default;
        device.BlendState = BlendState.Opaque;
    }
    private void RebuildTerrain(WorldGrid world, HashSet<Cell> fallingCells)
    {
        var mesh = new MeshBuilder();
        for (int x = 0; x < 32; x++) for (int y = 0; y < 32; y++) for (int z = 0; z < 32; z++)
                {
                    var cell = new Cell(x, y, z); var material = world.GetMaterial(cell);
                    if (material is Material.Air or Material.Obsidian || fallingCells.Contains(cell)) continue;
                    Color color = BlockColor(material, world.GetBarrier(cell)?.Content, cell);
                    bool Empty(int dx, int dy, int dz) => fallingCells.Contains(new Cell(x + dx, y + dy, z + dz)) || world.GetMaterial(new Cell(x + dx, y + dy, z + dz)) is Material.Air or Material.Obsidian;
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
    private static Color BlockColor(Material material, Material? content, Cell cell)
    {
        var color = material switch
        {
            Material.Core => new Color(82, 205, 183),
            Material.Barrier => content is Material.Dirt ? new Color(155, 118, 78) :
                content is Material.Stone ? new Color(160, 174, 184) : BlockColor(content ?? Material.Dirt, null, cell),
            Material.Dirt => new Color(112 + (cell.X + cell.Z) % 2 * 5, 105 + (cell.X + cell.Z) % 2 * 5, 83 + (cell.X + cell.Z) % 2 * 3),
            Material.Wood => ArmorColor(GearTier.Wood),
            Material.Copper => ArmorColor(GearTier.Copper),
            Material.Iron => ArmorColor(GearTier.Iron),
            Material.Gold => ArmorColor(GearTier.Gold),
            Material.Diamond => ArmorColor(GearTier.Diamond),
            Material.Osmium => ArmorColor(GearTier.Osmium),
            _ => new Color(60 + cell.Y % 3 * 4, 72 + cell.Y % 3 * 4, 81 + cell.Y % 3 * 4)
        };
        if (material == Material.Dirt && cell.Y == 15)
        {
            if (cell.X is 0 or 31 || cell.Z is 0 or 31) color = new Color(106, 79, 73);
            else if (cell.X is >= 12 and < 20 && cell.Z is >= 12 and < 20) color = Color.Lerp(color, new Color(91, 153, 126), .3f);
        }
        return color;
    }
    private void DrawTurret(Turret turret, bool selected, Vector3 center)
    {
        var p = center - new Vector3(0, .48f, 0);
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
    private void DrawZombie(Zombie zombie, double time, float flash, bool kiloUsingBuckets)
    {
        int firstVertex = dynamic.Vertices.Count;
        var p = ZombieFeet(zombie);
        var skin = new Color(115, 144, 99); var dark = new Color(43, 63, 53);
        float stride = MathF.Sin((float)time * 7 + zombie.Id) * .08f;
        float sole = zombie.Size == ZombieSize.Kilo ? .09f : .04f;
        dynamic.Box(p + new Vector3(-.21f, sole + stride, -.14f), p + new Vector3(-.045f, .39f + stride, .14f), dark);
        dynamic.Box(p + new Vector3(.045f, sole - stride, -.14f), p + new Vector3(.21f, .39f - stride, .14f), dark);
        var torsoWidth = zombie.Size == ZombieSize.Kilo ? .36f : .24f;
        var torsoDepth = zombie.Size == ZombieSize.Kilo ? .20f : .17f;
        dynamic.Box(p + new Vector3(-torsoWidth, .38f, -torsoDepth), p + new Vector3(torsoWidth, .84f, torsoDepth), skin);
        dynamic.Box(p + new Vector3(-.20f, .84f, -.19f), p + new Vector3(.20f, 1.21f, .19f), skin);
        dynamic.Box(p + new Vector3(-.39f, .42f, -.11f), p + new Vector3(-.25f, .87f, .14f), skin);
        dynamic.Box(p + new Vector3(.25f, .42f, -.11f), p + new Vector3(.39f, .87f, .14f), skin);
        dynamic.Box(p + new Vector3(-.15f, 1.02f, .195f), p + new Vector3(.15f, 1.07f, .21f), new Color(240, 122, 91));
        var tier = ArmorColor(zombie.Tier);
        foreach (var piece in zombie.ArmorPieces)
        {
            var (min, max) = ZombieArmorBounds(zombie, piece.Slot);
            dynamic.Box(p + min, p + max, ArmorColor(piece.Tier));
        }
        if (zombie.Kind is ZombieKind.Armed or ZombieKind.ArmedArmored)
            dynamic.Box(p + new Vector3(.35f, .33f, -.09f), p + new Vector3(.46f, 1.10f, .04f), tier);
        if (zombie.Size == ZombieSize.Kilo || zombie.CarriesBucket)
        {
            // Each bucket has its own fill. Kilos keep both buckets on their
            // backs while guarding, and bring the pair forward while scooping.
            for (int bucketIndex = 0; bucketIndex < zombie.BucketCount; bucketIndex++)
            {
                int firstBucketVertex = dynamic.Vertices.Count;
                var load = zombie.BucketLoads[bucketIndex];
                var metal = new Color(156, 171, 178);
                var rim = new Color(208, 219, 222);
                dynamic.Box(p + new Vector3(-.20f, .43f, -.54f), p + new Vector3(.20f, .49f, -.20f), metal);
                dynamic.Box(p + new Vector3(-.20f, .49f, -.54f), p + new Vector3(-.165f, .91f, -.20f), metal);
                dynamic.Box(p + new Vector3(.165f, .49f, -.54f), p + new Vector3(.20f, .91f, -.20f), metal);
                dynamic.Box(p + new Vector3(-.165f, .49f, -.54f), p + new Vector3(.165f, .91f, -.505f), metal);
                dynamic.Box(p + new Vector3(-.165f, .49f, -.235f), p + new Vector3(.165f, .91f, -.20f), metal);
                dynamic.Box(p + new Vector3(-.165f, .49f, -.505f), p + new Vector3(.165f, .50f, -.235f), new Color(41, 52, 58));
                if (load.Units > 0 && load.Kind is { } liquid)
                {
                    float fill = .50f + .34f * Math.Clamp(load.Units / (float)Balance.BucketCapacity, 0, 1);
                    dynamic.Box(p + new Vector3(-.163f, .50f, -.503f), p + new Vector3(.163f, fill, -.237f), LiquidColor(liquid));
                }
                dynamic.Outline(p + new Vector3(-.20f, .895f, -.54f), p + new Vector3(.20f, .925f, -.20f), rim, .009f);
                dynamic.Line(p + new Vector3(-.205f, .85f, -.37f), p + new Vector3(-.205f, 1.07f, -.37f), .014f, rim);
                dynamic.Line(p + new Vector3(-.205f, 1.07f, -.37f), p + new Vector3(.205f, 1.07f, -.37f), .014f, rim);
                dynamic.Line(p + new Vector3(.205f, 1.07f, -.37f), p + new Vector3(.205f, .85f, -.37f), .014f, rim);
                if (zombie.Size != ZombieSize.Kilo || !kiloUsingBuckets)
                    foreach (float x in new[] { -.13f, .13f })
                        dynamic.Box(p + new Vector3(x - .025f, .79f, -.24f), p + new Vector3(x + .025f, .98f, -.18f), new Color(69, 50, 34));
                if (zombie.Size == ZombieSize.Kilo)
                    TransformVertices(firstBucketVertex, Matrix.CreateTranslation(-p) * Matrix.CreateScale(.65f, 1, .65f) *
                        Matrix.CreateTranslation(p + new Vector3(bucketIndex == 0 ? -.21f : .21f,
                            kiloUsingBuckets ? -.08f : .12f, kiloUsingBuckets ? .48f : 0)));
            }
        }
        else if (zombie.CarriedBlocks > 0)
        {
            // The block and its straps share the body's facing transform; local -Z is the back.
            var block = BlockColor(zombie.CarriedBlockMaterial, null, zombie.Cell);
            var strap = new Color(69, 50, 34);
            dynamic.Box(p + new Vector3(-.18f, .44f, -.49f), p + new Vector3(.18f, .9f, -.2f), block);
            dynamic.Box(p + new Vector3(-.19f, .61f, -.501f), p + new Vector3(.19f, .66f, -.19f), strap);
            dynamic.Box(p + new Vector3(-.025f, .43f, -.502f), p + new Vector3(.025f, .91f, -.19f), strap);
        }
        if (flash > 0)
            for (int i = firstVertex; i < dynamic.Vertices.Count; i++)
            {
                var vertex = dynamic.Vertices[i];
                vertex.Color = Color.Lerp(vertex.Color, Color.White, Math.Clamp(flash, 0, 1));
                dynamic.Vertices[i] = vertex;
            }
        // Local +Z is the face and shield front. One transform keeps every body part,
        // equipped item aligned at the actor's size, anchored to its actual feet.
        TransformVertices(firstVertex, Matrix.CreateTranslation(-p) * Matrix.CreateScale(ZombieMeshScale(zombie)) *
            ZombieRotation(zombie) * Matrix.CreateTranslation(p));
    }

    private void DrawStowedKiloCover(Zombie zombie, KiloCover cover)
    {
        int firstVertex = dynamic.Vertices.Count;
        var p = ZombieFeet(zombie);
        float side = cover.BlockIndex == 0 ? -.23f : .23f;
        var min = p + new Vector3(side - .11f, .46f, -.47f);
        var max = p + new Vector3(side + .11f, .84f, -.25f);
        var color = BlockColor(cover.Material, null, zombie.Cell);
        dynamic.Box(min, max, color);
        dynamic.Outline(min + new Vector3(.008f), max - new Vector3(.008f), color * .72f, .008f);
        var strap = new Color(69, 50, 34);
        dynamic.Box(p + new Vector3(side - .018f, .45f, -.472f), p + new Vector3(side + .018f, .85f, -.24f), strap);
        dynamic.Box(p + new Vector3(side - .112f, .62f, -.472f), p + new Vector3(side + .112f, .665f, -.24f), strap);
        // These are inventory markers, not full-sized blocking volumes. Their
        // compact back-mounted corners fit inside the body at every yaw.
        TransformVertices(firstVertex, Matrix.CreateTranslation(-p) * Matrix.CreateScale(ZombieMeshScale(zombie)) *
            ZombieRotation(zombie) * Matrix.CreateTranslation(p));
    }
    private static Vector3 ZombieMeshScale(Zombie zombie) => zombie.Size == ZombieSize.Kilo
        // Uniform horizontal scaling keeps the farthest equipped corner below
        // a one-block radius, so every yaw fits the same 2x2 footprint.
        ? new Vector3(1.7f, ZombieHeightScale * 2, 1.7f)
        : new Vector3(1, ZombieHeightScale, 1) * zombie.BodyScale;

    private static (Vector3 Min, Vector3 Max) ZombieArmorBounds(Zombie zombie, ArmorSlot slot)
    {
        var (min, max) = ArmorBounds(slot);
        if (zombie.Size == ZombieSize.Kilo && slot == ArmorSlot.Chestplate)
        {
            min.X *= 1.45f;
            max.X *= 1.45f;
            min.Z = -.22f;
            max.Z = .22f;
        }
        return (min, max);
    }

    private static Vector3 ZombieFeet(Zombie zombie) => ToXna(zombie.Position) - new Vector3(0, zombie.StandingHeight, 0);
    private static Matrix ZombieRotation(Zombie zombie)
    {
        var facing = ToXna(zombie.Facing);
        facing.Y = 0;
        if (!float.IsFinite(facing.X) || !float.IsFinite(facing.Z) || facing.LengthSquared() < .000001f) facing = Vector3.UnitZ;
        else facing.Normalize();
        return Matrix.CreateRotationY(MathF.Atan2(facing.X, facing.Z));
    }
    private static (Vector3 Min, Vector3 Max) ArmorBounds(ArmorSlot slot) => slot switch
    {
        ArmorSlot.Helmet => (new Vector3(-.22f, 1.13f, -.22f), new Vector3(.22f, 1.28f, .22f)),
        ArmorSlot.Chestplate => (new Vector3(-.255f, .45f, -.19f), new Vector3(.255f, .83f, .19f)),
        ArmorSlot.Shield => (new Vector3(-.28f, .36f, .235f), new Vector3(.28f, .9f, .32f)),
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };
    private void TransformVertices(int firstVertex, Matrix transform)
    {
        for (int i = firstVertex; i < dynamic.Vertices.Count; i++)
        {
            var vertex = dynamic.Vertices[i];
            vertex.Position = Vector3.Transform(vertex.Position, transform);
            dynamic.Vertices[i] = vertex;
        }
    }
    private static Color ArmorColor(GearTier tier) => tier switch
    {
        GearTier.Wood => new Color(137, 100, 67), GearTier.Stone => new Color(137, 146, 155), GearTier.Copper => new Color(188, 123, 83),
        GearTier.Iron => new Color(189, 201, 207), GearTier.Gold => new Color(241, 186, 62), GearTier.Diamond => new Color(100, 207, 233), _ => new Color(107, 101, 139)
    };
    private static Color LiquidColor(LiquidKind kind) => kind switch
    {
        LiquidKind.Water => new Color(58, 142, 211),
        LiquidKind.Mercury => new Color(184, 197, 215),
        _ => new Color(248, 102, 32)
    };
    public static Vector3 ToXna(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    public void Dispose() { terrain?.Dispose(); effect.Dispose(); }
}
