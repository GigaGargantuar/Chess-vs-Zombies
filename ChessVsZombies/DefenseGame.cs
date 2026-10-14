using ChessVsZombies.Core;
using ChessVsZombies.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessVsZombies;

public sealed class DefenseGame : Game
{
    private readonly GraphicsDeviceManager graphics;
    private GameSession session = new();
    private SpriteBatch batch = null!;
    private Texture2D pixel = null!;
    private UiFont font = null!;
    private WorldRenderer renderer = null!;
    private KeyboardState previousKeys;
    private MouseState previousMouse;
    private readonly FlyCamera cameraControl = new();
    private Vector3 camera => WorldRenderer.ToXna(cameraControl.Position);
    private bool captured = true, paused, showZones = true, saveBlocked;
    private int tool, panel, jumpY, jumpX, jumpZ, pourUnits = 10;
    private Turret? selected;
    private Cell? hit, placement;
    private string notice = "Place your first pieces. Press Tab to use the command panel.";
    private float noticeTime = 8;
    private float uiScale = 1, width = 1440, height = 900;
    private readonly List<(Rectangle Area, Action Action)> buttons = [];
    private readonly List<Cell> draftPattern = [];
    private readonly UpgradeTemplate upgradeTemplate = new();
    private readonly bool smoke, smokeCombat;
    private readonly string smokePanel;
    private readonly string? capturePath;
    private Zombie? smokeArmorDemo;
    private int frameCount;
    private double elapsed;
    private readonly string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChessVsZombies", "campaign.json");
    private static readonly Color Background = new(11, 17, 26), Panel = new(17, 26, 37), Raised = new(26, 39, 52), TextColor = new(230, 235, 232), Muted = new(138, 157, 170), Teal = new(105, 225, 191), Gold = new(232, 190, 114), Red = new(235, 125, 112);

    public DefenseGame(string[] args)
    {
        graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 1440, PreferredBackBufferHeight = 900, GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = true };
        Window.Title = "Chess vs Zombies";
        Window.AllowUserResizing = true;
        IsMouseVisible = false;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1d / 60);
        smoke = args.Contains("--smoke") || args.Contains("--smoke-combat") || args.Contains("--smoke-gacha") || args.Contains("--smoke-jump") || args.Contains("--smoke-template") || args.Contains("--smoke-terrain") || args.Contains("--smoke-liquids") || args.Contains("--smoke-armor");
        smokeCombat = args.Contains("--smoke-combat");
        smokePanel = args.Contains("--smoke-gacha") ? "gacha" : args.Contains("--smoke-jump") ? "jump" : args.Contains("--smoke-template") ? "template" : args.Contains("--smoke-terrain") ? "terrain" : args.Contains("--smoke-liquids") ? "liquids" : args.Contains("--smoke-armor") ? "armor" : "";
        if (smokePanel == "armor") cameraControl = new FlyCamera(new System.Numerics.Vector3(20, 19, 31), MathF.PI, -.31f);
        int captureIndex = Array.IndexOf(args, "--capture");
        capturePath = captureIndex >= 0 && captureIndex + 1 < args.Length ? Path.GetFullPath(args[captureIndex + 1]) : null;
        int saveIndex = Array.IndexOf(args, "--save-path");
        if (saveIndex >= 0 && saveIndex + 1 < args.Length) savePath = Path.GetFullPath(args[saveIndex + 1]);
        Activated += (_, _) => { if (captured) CenterMouse(); };
        Exiting += (_, _) => SaveCheckpoint();
    }
    protected override void LoadContent()
    {
        batch = new SpriteBatch(GraphicsDevice); pixel = new Texture2D(GraphicsDevice, 1, 1); pixel.SetData(new[] { Color.White });
        font = new UiFont(GraphicsDevice); renderer = new WorldRenderer(GraphicsDevice);
        if (smoke)
        {
            foreach (var c in new[] { new Cell(11, 16, 15), new Cell(11, 16, 16), new Cell(20, 16, 15), new Cell(20, 16, 16), new Cell(15, 16, 11), new Cell(16, 16, 11), new Cell(15, 16, 20), new Cell(16, 16, 20) })
                session.Deploy((PieceKind)(session.Turrets.Count % 4), c);
            for (int i = 0; i < 24; i++) session.Economy.Pull(session.Turrets);
            selected = session.Turrets[0];
            session.Economy.Apply(selected, UpgradeKind.ATK);
            session.Inventory.Add(Material.Dirt, 3); session.Inventory.Add(Material.Stone, 2);
            session.BuildBarrier(new Cell(13, 16, 13), Material.Stone); session.BuildBarrier(new Cell(13, 17, 13), Material.Dirt);
            if (smokeCombat) { session.StartLevel(); session.Update(12f); }
            if (smokePanel == "gacha") panel = 1;
            if (smokePanel == "template")
            {
                for (int y = 16; y <= 18; y++) session.Deploy((PieceKind)(y - 16), new Cell(20, y, 20));
                upgradeTemplate.Points[UpgradeKind.HP] = 3; upgradeTemplate.Points[UpgradeKind.Jump] = 1; panel = 2;
            }
            if (smokePanel == "terrain") { session.BuyTool(ToolKind.Shovel); tool = 4; panel = 5; }
            if (smokePanel == "liquids")
            {
                session.Economy.Credit(6000);
                session.BuyTool(ToolKind.Shovel);
                foreach (var kind in Enum.GetValues<LiquidKind>())
                {
                    var cell = new Cell(18 + (int)kind, 15, 21);
                    session.Dig(cell); session.BuyLiquid(kind); session.BuyLiquid(kind);
                    session.PourLiquid(cell, kind);
                }
                smokeArmorDemo = new Zombie { Id = -1, WaveIndex = -1, Kind = ZombieKind.Armored, Tier = GearTier.Osmium, Position = new Cell(20, 16, 22).Center };
                session.Zombies.Add(smokeArmorDemo);
                tool = 7; panel = 6;
            }
            if (smokePanel == "jump") { selected = session.Turrets.First(t => t.Kind == PieceKind.Knight); OpenJumpEditor(); }
            if (smokePanel == "armor")
            {
                System.Numerics.Vector3[] facings = [System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitX, -System.Numerics.Vector3.UnitZ, -System.Numerics.Vector3.UnitX];
                GearTier[] tiers = [GearTier.Wood, GearTier.Iron, GearTier.Gold, GearTier.Osmium];
                for (int i = 0; i < facings.Length; i++)
                {
                    var zombie = new Zombie { Id = -1 - i, WaveIndex = -1, Kind = ZombieKind.ArmedArmored, Tier = tiers[i], Position = new Cell(17 + i * 2, 16, 23).Center };
                    zombie.FaceToward(facings[i]);
                    session.Zombies.Add(zombie);
                    if (i == 3) smokeArmorDemo = zombie;
                }
            }
            SetNotice(smokeCombat ? "Live simulation / rendered validation" : "Crosshair placement / rendered validation");
        }
        else LoadCheckpoint();
        CenterMouse();
        base.LoadContent();
    }
    private Vector3 Forward => WorldRenderer.ToXna(cameraControl.Forward);
    private void CenterMouse() { Mouse.SetPosition(Window.ClientBounds.Width / 2, Window.ClientBounds.Height / 2); previousMouse = Mouse.GetState(); }
    private void Capture(bool value) { captured = value; IsMouseVisible = !value; if (value) CenterMouse(); }
    private bool Pressed(KeyboardState keys, Keys key) => keys.IsKeyDown(key) && previousKeys.IsKeyUp(key);
    protected override void Update(GameTime gameTime)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        elapsed += dt; noticeTime = Math.Max(0, noticeTime - dt);
        var keys = Keyboard.GetState(); var mouse = Mouse.GetState();
        uiScale = Math.Min(Window.ClientBounds.Width / 1440f, Window.ClientBounds.Height / 900f);
        uiScale = Math.Max(.4f, uiScale); width = Window.ClientBounds.Width / uiScale; height = Window.ClientBounds.Height / uiScale;
        if (IsActive && !smoke)
        {
            if (Pressed(keys, Keys.Tab)) Capture(!captured);
            if (Pressed(keys, Keys.Escape)) { if (panel == 3) panel = 2; else Capture(!captured); }
            if (Pressed(keys, Keys.P)) paused = !paused;
            if (Pressed(keys, Keys.F1)) { panel = 4; Capture(false); }
            if (Pressed(keys, Keys.V)) showZones = !showZones;
            if (Pressed(keys, Keys.G)) { panel = 1; Capture(false); }
            if (Pressed(keys, Keys.U)) { SelectTarget(); panel = 2; Capture(false); }
            if (Pressed(keys, Keys.J)) { SelectTarget(); OpenJumpEditor(); }
            if (Pressed(keys, Keys.L)) { panel = 6; Capture(false); }
            if (Pressed(keys, Keys.Enter)) BeginLevel();
            if (Pressed(keys, Keys.R) && session.Phase == GamePhase.Defeat) { LoadCheckpoint(); paused = false; }
            for (int i = 0; i < 7; i++) if (Pressed(keys, (Keys)((int)Keys.D1 + i))) { tool = i; panel = i >= 4 ? 5 : 0; }
            if (Pressed(keys, Keys.D8)) { tool = 7; panel = 6; }
            if (Pressed(keys, Keys.D9)) { tool = 8; panel = 6; }
            if (Pressed(keys, Keys.D0)) { tool = 9; panel = 6; }
            if (Pressed(keys, Keys.Delete) && session.Phase == GamePhase.Building)
            {
                var c = selected?.Cell ?? hit;
                if (c.HasValue) { session.Remove(c.Value); selected = null; SetNotice(session.Message); SaveCheckpoint(); }
            }
            if (captured)
            {
                mouse = Mouse.GetState();
                int cx = Window.ClientBounds.Width / 2, cy = Window.ClientBounds.Height / 2;
                cameraControl.Look(mouse.X - cx, mouse.Y - cy);
                System.Numerics.Vector3 movement = System.Numerics.Vector3.Zero;
                if (keys.IsKeyDown(Keys.W)) movement.Z++; if (keys.IsKeyDown(Keys.S)) movement.Z--;
                if (keys.IsKeyDown(Keys.D)) movement.X++; if (keys.IsKeyDown(Keys.A)) movement.X--;
                if (keys.IsKeyDown(Keys.Space)) movement.Y++;
                if (keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift)) movement.Y--;
                cameraControl.MovePrecise(movement, dt * (keys.IsKeyDown(Keys.LeftControl) ? 12 : 7), point => session.World.IsSolidAt(point) || session.Turrets.Any(t => t.Cell == new Cell((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z))));
                Aim();
                if (mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released) RemoveTarget();
                if (mouse.RightButton == ButtonState.Pressed && previousMouse.RightButton == ButtonState.Released) PlaceDefense();
                if (mouse.MiddleButton == ButtonState.Pressed && previousMouse.MiddleButton == ButtonState.Released) ApplyTemplateToTarget();
                Mouse.SetPosition(cx, cy);
            }
            else if (mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released)
            {
                var point = new Point((int)(mouse.X / uiScale), (int)(mouse.Y / uiScale));
                var clicked = buttons.LastOrDefault(b => b.Area.Contains(point)); clicked.Action?.Invoke();
            }
        }
        else if (smoke) Aim();
        var oldPhase = session.Phase;
        if (!paused && (IsActive || smoke)) session.Update(dt);
        if (smokeArmorDemo != null && frameCount == 30)
            session.DamageZombie(smokeArmorDemo, Balance.GearHp(GearTier.Osmium), DamageKind.Pierce, ShotKind.Pellet, -smokeArmorDemo.Facing);
        if (oldPhase == GamePhase.Combat && session.Phase == GamePhase.Building) { SetNotice(session.Message); SaveCheckpoint(); }
        if (oldPhase != GamePhase.Defeat && session.Phase == GamePhase.Defeat) { Capture(false); SetNotice("The core has fallen. Retry the saved preparation with R."); }
        if (selected is not null && !session.Turrets.Contains(selected)) selected = null;
        previousKeys = keys; previousMouse = Mouse.GetState();
        base.Update(gameTime);
    }
    private void Aim()
    {
        hit = null; placement = null;
        var last = new Cell((int)MathF.Floor(camera.X), (int)MathF.Floor(camera.Y), (int)MathF.Floor(camera.Z));
        var previous = last;
        for (float d = 0; d < 64; d += .04f)
        {
            var p = camera + Forward * d; var c = new Cell((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));
            if (c != last) { previous = last; last = c; }
            var point = new System.Numerics.Vector3(p.X, p.Y, p.Z);
            bool liquid = tool >= 7 && session.Liquids.GetLiquidAt(point).HasValue;
            if (WorldGrid.IsInside(c) && (session.World.IsSolidAt(point) || session.TrySelectTurret(c) is not null || liquid))
            { hit = c; placement = liquid ? c : previous; SelectTarget(); return; }
        }
    }
    private void SelectTarget()
    {
        if (hit.HasValue && session.TrySelectTurret(hit.Value) is { } turret) selected = turret;
    }
    private void RemoveTarget()
    {
        if (!hit.HasValue) { SetNotice("Aim at a tower or defensive block to remove it."); return; }
        if (tool == 6 && session.TrySelectTurret(hit.Value) is null && session.World.GetMaterial(hit.Value) is Material.Dirt or Material.Stone)
            session.Dig(hit.Value);
        else session.Remove(hit.Value);
        SetNotice(session.Message); SaveCheckpoint();
    }
    private void PlaceDefense()
    {
        if (tool == 6) { SetNotice("Left-click terrain with the excavation tool to dig."); return; }
        if (placement.HasValue)
        {
            if (cameraControl.Intersects(placement.Value)) { SetNotice("Move away from the placement cell first."); return; }
            if (tool >= 7) session.PourLiquid(placement.Value, (LiquidKind)(tool - 7), pourUnits);
            else if (tool is 4 or 5) session.BuildBarrier(placement.Value, tool == 4 ? Material.Dirt : Material.Stone);
            else session.Deploy((PieceKind)tool, placement.Value);
        }
        else { SetNotice("Aim at a solid surface to place a defense."); return; }
        SetNotice(session.Message); SaveCheckpoint();
    }
    private void BeginLevel()
    {
        if (session.Phase != GamePhase.Building) return;
        if (panel == 3) panel = 2;
        SaveCheckpoint(); session.StartLevel(); SetNotice(session.Message);
    }
    private void Pull(int count)
    {
        if (session.Phase != GamePhase.Building) { SetNotice("Pull components during preparation between levels."); return; }
        for (int i = 0; i < count; i++) if (session.Economy.Pull(session.Turrets) is null) break;
        SetNotice(session.Economy.LastMessage); SaveCheckpoint();
    }
    private void ApplyTemplateToTarget()
    {
        if (!hit.HasValue || session.TrySelectTurret(hit.Value) is not { } target)
        { SetNotice("Aim at a tower and middle-click to spend the template's points. U edits the template."); return; }
        selected = target;
        session.ApplyUpgradeTemplate(target.Cell, upgradeTemplate);
        SetNotice(session.Message); SaveCheckpoint();
    }
    private int TemplatePoints(UpgradeKind kind) => upgradeTemplate.Points.GetValueOrDefault(kind);
    private int TemplateMaximum(UpgradeKind kind) => session.Economy.GetAllocationCap(kind);
    private int TemplateApplicable(UpgradeKind kind)
    {
        if (selected is not null && session.Economy.GetRange(selected, kind) == int.MaxValue) return 0;
        int used = selected is null ? 0 : session.Economy.GetAllocated(selected, kind);
        return Math.Min(session.Economy.GetProgress(kind).Available, Math.Max(0, session.Economy.GetAllocationCap(kind) - used));
    }
    private void ChangeTemplatePoints(UpgradeKind kind, int delta)
    {
        upgradeTemplate.Points[kind] = delta < 0 ? Math.Max(0, TemplatePoints(kind) + delta) : Math.Min(TemplatePoints(kind) + delta, TemplateMaximum(kind));
    }
    private Turret TemplatePreview()
    {
        var preview = new Turret { Kind = selected?.Kind ?? PieceKind.Knight };
        foreach (var kind in Enum.GetValues<UpgradeKind>())
            preview.UpgradePoints[kind] = (selected is null ? 0 : session.Economy.GetAllocated(selected, kind)) + Math.Min(TemplatePoints(kind), TemplateApplicable(kind));
        return preview;
    }
    private void BuyTool(ToolKind kind)
    {
        session.BuyTool(kind); SetNotice(session.Message); SaveCheckpoint();
    }
    private void BuyLiquid(LiquidKind kind)
    {
        session.BuyLiquid(kind); tool = 7 + (int)kind; SetNotice(session.Message); SaveCheckpoint();
    }
    private void OpenJumpEditor()
    {
        if (session.Phase != GamePhase.Building) { SetNotice("Edit patterns during preparation."); return; }
        draftPattern.Clear(); draftPattern.AddRange(upgradeTemplate.JumpPattern); jumpY = 0; jumpX = 0; jumpZ = 0; panel = 3; Capture(false);
    }
    private void SetNotice(string text) { notice = text; noticeTime = 7; }
    private void SaveCheckpoint()
    {
        if (smoke || saveBlocked || session.Phase != GamePhase.Building) return;
        if (!SaveStore.TrySave(session, savePath, out var error)) SetNotice(error);
    }
    private void LoadCheckpoint()
    {
        if (SaveStore.TryLoad(savePath, out var restored, out var error))
        {
            session = restored!; SetNotice(string.IsNullOrEmpty(session.MigrationNotice) ? "Preparation restored. Your currency, pieces and collection are saved." : session.MigrationNotice); saveBlocked = false;
        }
        else
        {
            session = new GameSession(seed: Environment.TickCount); saveBlocked = !string.IsNullOrEmpty(error);
            if (saveBlocked) SetNotice(error + " Original save preserved; saving disabled.");
        }
        selected = null;
    }
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Background);
        var view = Matrix.CreateLookAt(camera, camera + Forward, Vector3.Up);
        var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(65), GraphicsDevice.Viewport.AspectRatio, .08f, 150);
        bool validPlacement = placement is { } p && session.Phase == GamePhase.Building && WorldGrid.IsInside(p) &&
            (tool >= 7 ? session.LiquidInventory.GetValueOrDefault((LiquidKind)(tool - 7)) >= pourUnits && session.Liquids.GetTotalUnits(p) + pourUnits <= 10 :
                (tool is 4 or 5 ? WorldGrid.IsBuildZone(p) && session.Inventory.GetCount(tool == 4 ? Material.Dirt : Material.Stone) > 0 && session.World.HasOrthogonalBlockSupport(p) : WorldGrid.IsTurretZone(p) && session.HasTowerSupport(p)) && session.Liquids.GetTotalUnits(p) == 0) &&
            session.World.GetMaterial(p) == Material.Air && !session.Turrets.Any(t => t.Cell == p) && !cameraControl.Intersects(p);
        renderer.Draw(session, view, projection, hit, tool == 6 ? null : placement, validPlacement, selected, showZones, elapsed);
        buttons.Clear();
        batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp, null, null, null, Matrix.CreateScale(uiScale));
        DrawHud();
        batch.End();
        frameCount++;
        if (smoke && frameCount >= 90)
        {
            if (capturePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                var colors = new Color[GraphicsDevice.PresentationParameters.BackBufferWidth * GraphicsDevice.PresentationParameters.BackBufferHeight];
                GraphicsDevice.GetBackBufferData(colors);
                using var texture = new Texture2D(GraphicsDevice, GraphicsDevice.PresentationParameters.BackBufferWidth, GraphicsDevice.PresentationParameters.BackBufferHeight);
                texture.SetData(colors); using var stream = File.Create(capturePath); texture.SaveAsPng(stream, texture.Width, texture.Height);
            }
            Exit();
        }
        base.Draw(gameTime);
    }
    private void Fill(float x, float y, float w, float h, Color color) => batch.Draw(pixel, new Rectangle((int)x, (int)y, (int)w, (int)h), color);
    private void Text(string text, float x, float y, float size = 18, Color? color = null) => font.Draw(batch, text, new Vector2(x, y), color ?? TextColor, size);
    private void Rule(float x, float y, float w) => Fill(x, y, w, 1, new Color(49, 64, 76));
    private void Button(string label, float x, float y, float w, float h, Action action, bool active = false, bool enabled = true, Color? accent = null)
    {
        var mouse = Mouse.GetState(); var bounds = new Rectangle((int)x, (int)y, (int)w, (int)h);
        bool hover = !captured && bounds.Contains((int)(mouse.X / uiScale), (int)(mouse.Y / uiScale));
        Fill(x, y, w, h, active ? (accent ?? Teal) : hover && enabled ? new Color(39, 57, 70) : Raised);
        if (active) Fill(x, y, 3, h, Color.White * .8f);
        Text(label, x + 14, y + (h - 22) / 2, 18, active ? Background : enabled ? TextColor : Muted * .55f);
        if (enabled) buttons.Add((bounds, action));
    }
    private void DrawHud()
    {
        Fill(0, 0, width, 92, Background); Fill(0, 91, width, 1, new Color(40, 56, 65));
        Fill(28, 25, 4, 44, Teal); Text("CHESS", 44, 12, 30); Text("vs ZOMBIES", 45, 48, 17, Muted);
        float divider = 265; Fill(divider, 22, 1, 48, new Color(48, 63, 73));
        Text($"LEVEL {session.LevelNumber:00}", divider + 25, 19, 21); Text(session.Phase == GamePhase.Building ? "PREPARATION" : session.Phase == GamePhase.Combat ? "DEFENSE IN PROGRESS" : "CORE LOST", divider + 25, 50, 13, session.Phase == GamePhase.Defeat ? Red : Teal);
        Text("CORE INTEGRITY", width * .43f, 19, 13, Muted); Text($"{session.CoreHp:0} / {Balance.CoreHp}", width * .43f + 137, 17, 17, Teal);
        Fill(width * .43f, 49, 235, 6, new Color(44, 59, 66)); Fill(width * .43f, 49, 235 * session.CoreHp / Balance.CoreHp, 6, Teal);
        Text("CURRENCY", width - 320, 19, 13, Muted); Text(session.Economy.Currency.ToString("N0"), width - 320, 39, 29, Gold);
        Text("FIELD", width - 143, 19, 13, Muted); Text($"{session.Turrets.Count:00} pieces", width - 143, 43, 21);

        // Crosshair always marks the true camera ray, including when menus release the pointer.
        float cx = width / 2, cy = height / 2; var crossColor = placement.HasValue ? Teal : TextColor;
        Fill(cx - 11, cy - 1, 7, 2, crossColor); Fill(cx + 4, cy - 1, 7, 2, crossColor); Fill(cx - 1, cy - 11, 2, 7, crossColor); Fill(cx - 1, cy + 4, 2, 7, crossColor); Fill(cx - 1, cy - 1, 2, 2, crossColor);
        if (hit.HasValue)
        {
            var barrier = session.World.GetBarrier(hit.Value);
            var material = session.World.GetMaterial(hit.Value);
            var label = barrier is not null
                ? $"{barrier.Content.ToString().ToUpperInvariant()}  {barrier.Hp:0}/{Balance.BlockHp(barrier.Content)} HP"
                : material is Material.Dirt or Material.Stone
                    ? $"{material.ToString().ToUpperInvariant()}  {session.World.GetTerrainHp(hit.Value):0}/{Balance.BlockHp(material)} HP"
                    : $"{(tool == 6 ? "EXCAVATE" : "TARGET")}  {hit.Value}";
            if (tool >= 7 && session.Liquids.GetTotalUnits(hit.Value) > 0)
                label = string.Join("  /  ", Enum.GetValues<LiquidKind>().Where(kind => session.Liquids.GetUnits(hit.Value, kind) > 0).Select(kind => $"{kind.ToString().ToUpperInvariant()} {session.Liquids.GetUnits(hit.Value, kind)}/10"));
            else if (session.World.GetObsidian(hit.Value) is { } crust)
                label = $"OBSIDIAN {crust.Units}/10  /  {crust.Hp:0}/{crust.Units * LiquidSystem.ObsidianHpPerUnit:0} HP";
            Text(label, cx - font.Measure(label, 13) / 2, cy + 21, 13, crossColor);
        }
        float left = 28;
        Text("SECTOR  /  32 x 32 x 32", left, 116, 14, Muted);
        Text(session.Level.Name, left, 139, 25);
        if (session.Phase == GamePhase.Combat)
        {
            int started = session.Waves.Count(w => w.Started), total = session.Waves.Sum(w => w.TotalCount), spawned = session.Waves.Sum(w => w.Spawned);
            Text($"WAVE {started}/{session.Waves.Count}     {session.Zombies.Count} ACTIVE     {spawned}/{total} SPAWNED", left, 180, 15, Teal);
            float y = 210;
            foreach (var wave in session.Waves)
            {
                Text($"{wave.Index + 1:00}  {(wave.Started ? "HP REMAINING" : "WAITING")}", left, y, 12, Muted);
                Fill(left, y + 23, 220, 5, Raised); Fill(left, y + 23, 220 * (1 - wave.DamageTaken / Math.Max(1, wave.TotalHp)), 5, wave.Started ? Red : Muted * .4f);
                Fill(left + 110, y + 21, 1, 9, TextColor); y += 47;
            }
        }
        else
        {
            Text("Build. Equip. Hold the center.", left, 180, 16, Muted);
            Text($"Next level: {session.Level.Waves.Sum(w => w.Groups.Sum(g => g.Count))} zombies / {session.Level.Waves.Count} wave(s)", left, 206, 15, Muted);
        }
        float px = width - 340, py = 112;
        Fill(px, py, 312, height - 248, Panel);
        Fill(px, py, 312, 3, Teal);
        Text("COMMAND", px + 20, py + 19, 13, Muted); Text(captured ? "TAB TO INTERACT" : "CURSOR RELEASED", px + 117, py + 19, 12, Teal);
        Button("Deploy", px + 16, py + 51, 88, 36, () => panel = 0, panel is 0 or 5 or 6);
        Button("Armory", px + 111, py + 51, 88, 36, () => panel = 1, panel == 1);
        Button("Template", px + 206, py + 51, 90, 36, () => panel = 2, panel is 2 or 3);
        if (panel == 0) DrawDeployPanel(px, py + 106);
        else if (panel == 1) DrawGachaPanel(px, py + 106);
        else if (panel == 2) DrawUpgradePanel(px, py + 106);
        else if (panel == 3) { DrawUpgradePanel(px, py + 106); DrawJumpEditor(); }
        else if (panel == 5) DrawTerrainPanel(px, py + 106);
        else if (panel == 6) DrawLiquidPanel(px, py + 106);
        else DrawHelpPanel(px, py + 106);
        float footer = height - 114;
        Fill(0, footer, width, 114, Background); Rule(0, footer, width);
        if (noticeTime > 0) Text(Trim(notice, (int)(width / 9)), 28, footer + 13, 17, Gold);
        else Text(captured ? "LEFT remove    RIGHT place    MIDDLE apply template to the aimed tower    U configure template" : "Choose point counts and a pattern, then press Tab and middle-click a tower to apply.", 28, footer + 13, 17, Teal);
        Text("WASD  move     MOUSE  look     SPACE / SHIFT  up / down     TAB  cursor     ENTER  start level", 28, footer + 48, 15, Muted);
        Text("1-4 pieces   5 dirt   6 stone   7 dig   8/9/0 liquids   L liquid shop   G gacha   U template   J pattern   P pause   F1 help", 28, footer + 75, 14, Muted);
        if (paused) { Fill(width / 2 - 125, 101, 250, 44, Background); Text("SIMULATION PAUSED", width / 2 - 100, 110, 19, Gold); }
        if (session.Phase == GamePhase.Defeat)
        {
            Fill(width / 2 - 250, height / 2 - 115, 500, 215, Panel); Text("THE CORE HAS FALLEN", width / 2 - 197, height / 2 - 91, 30, Red);
            Text("Retry from your last preparation checkpoint.", width / 2 - 190, height / 2 - 40, 18, Muted);
            Button("R  /  Retry preparation", width / 2 - 215, height / 2 + 10, 430, 48, () => { LoadCheckpoint(); paused = false; Capture(true); }, true);
        }
    }
    private void DrawDeployPanel(float x, float y)
    {
        Text("BUILD YOUR DEFENSE", x + 20, y, 18);
        string[] names = ["Pawn", "Knight", "Bishop", "Rook"], descriptions = ["Adjacent orthogonal melee", "Custom jumps / lobbed shots", "Unlimited plane rays / pellets", "Unlimited orthogonal / pellets"];
        for (int i = 0; i < 4; i++)
        {
            int k = i; float row = y + 37 + i * 69; bool active = tool == i;
            Button($"{i + 1}   {names[i]}", x + 16, row, 280, 60, () => { tool = k; panel = 0; }, active);
            Text(Balance.PieceCost((PieceKind)i).ToString(), x + 269, row + 9, 19, active ? Background : Gold);
            Text(descriptions[i], x + 54, row + 36, 12, active ? new Color(30, 75, 64) : Muted);
        }
        float next = y + 322;
        Button("Terrain / tools & blocks", x + 16, next, 280, 40, () => panel = 5);
        Button("Liquids / water, mercury & lava", x + 16, next + 47, 280, 40, () => panel = 6);
        Text("Pieces: outside the 8-cube enclosure", x + 20, next + 96, 14, Muted);
        Text("Survivors earn 2 currency each.", x + 20, next + 120, 14, Gold);
        Button(session.Phase == GamePhase.Building ? "ENTER  /  Start level" : "DEFENSE ACTIVE", x + 16, height - 201, 280, 47, BeginLevel, session.Phase == GamePhase.Building, session.Phase == GamePhase.Building);
    }
    private void DrawTerrainPanel(float x, float y)
    {
        Text("EXCAVATE & FORTIFY", x + 20, y, 21);
        Text("Buy tools once. Reuse the blocks you dig.", x + 20, y + 33, 13, Muted);
        Button(session.Inventory.HasShovel ? "Shovel / owned" : "Buy shovel / 100", x + 16, y + 68, 280, 43, () => BuyTool(ToolKind.Shovel), session.Inventory.HasShovel, !session.Inventory.HasShovel && session.Phase == GamePhase.Building);
        Text("Required to excavate dirt", x + 20, y + 117, 13, Muted);
        Button(session.Inventory.HasPickaxe ? "Pickaxe / owned" : "Buy pickaxe / 1,000", x + 16, y + 150, 280, 43, () => BuyTool(ToolKind.Pickaxe), session.Inventory.HasPickaxe, !session.Inventory.HasPickaxe && session.Phase == GamePhase.Building);
        Text("Required to excavate stone", x + 20, y + 199, 13, Muted);
        Rule(x + 20, y + 230, 272);
        Button($"5  Dirt  /  {session.Inventory.GetCount(Material.Dirt)} in bag", x + 16, y + 250, 280, 44, () => tool = 4, tool == 4);
        Text("100 HP / place one excavated dirt block", x + 20, y + 300, 13, Muted);
        Button($"6  Stone  /  {session.Inventory.GetCount(Material.Stone)} in bag", x + 16, y + 331, 280, 44, () => tool = 5, tool == 5);
        Text("500 HP / place one excavated stone block", x + 20, y + 381, 13, Muted);
        Button("7  Excavate / left-click terrain", x + 16, y + 416, 280, 40, () => tool = 6, tool == 6);
        Button("Return to field", x + 16, height - 201, 280, 47, () => Capture(true), true);
    }
    private static string UpgradeName(UpgradeKind kind) => kind switch { UpgradeKind.HP => "Health", UpgradeKind.ATK => "Attack", UpgradeKind.Orthogonal => "Orthogonal", UpgradeKind.PlaneDiagonal => "Plane diagonal", UpgradeKind.SpaceDiagonal => "Space diagonal", _ => "Jump pattern" };
    private void DrawLiquidPanel(float x, float y)
    {
        Text("LIQUID SUPPLY", x + 20, y, 21);
        Text("One block = 10 units. Pour with right click.", x + 20, y + 30, 13, Muted);
        foreach (var kind in Enum.GetValues<LiquidKind>())
        {
            int index = (int)kind;
            int price = Balance.LiquidCost(kind);
            int held = session.LiquidInventory.GetValueOrDefault(kind);
            float row = y + 63 + index * 99;
            Text($"{kind}  /  {held} units", x + 20, row, 17, LiquidColor(kind));
            Button($"Buy 10 / {price:N0}", x + 16, row + 27, 176, 37, () => BuyLiquid(kind), enabled: session.Phase == GamePhase.Building && session.Economy.Currency >= price);
            Button($"{(index == 2 ? 0 : index + 8)}  Pour", x + 200, row + 27, 96, 37, () => tool = index + 7, tool == index + 7, accent: LiquidColor(kind));
            Text(kind switch { LiquidKind.Water => "Drowns submerged zombies", LiquidKind.Mercury => "Chemical / armor bypass", _ => "Thermal / armor bypass" }, x + 20, row + 72, 12, Muted);
        }
        Rule(x + 20, y + 365, 272);
        Text("UNITS PER POUR", x + 20, y + 380, 13, Muted);
        Button("-", x + 16, y + 406, 58, 36, () => pourUnits = Math.Max(1, pourUnits - 1));
        Text($"{pourUnits} / 10", x + 119, y + 413, 18, Gold);
        Button("+", x + 238, y + 406, 58, 36, () => pourUnits = Math.Min(10, pourUnits + 1));
        Text("Dig a basin to contain the flow.", x + 20, y + 456, 13, Muted);
        Button("Return to field", x + 16, height - 201, 280, 47, () => Capture(true), true);
    }
    private static Color LiquidColor(LiquidKind kind) => kind switch
    {
        LiquidKind.Water => new Color(100, 181, 244), LiquidKind.Mercury => new Color(196, 203, 218), _ => new Color(249, 147, 82)
    };
    private void DrawGachaPanel(float x, float y)
    {
        Text("UPGRADE POINT SUPPLY", x + 20, y, 18); Text("5 per roll / each roll grants 1 point", x + 20, y + 29, 14, Muted);
        int i = 0;
        foreach (var kind in Enum.GetValues<UpgradeKind>())
        {
            var progress = session.Economy.GetProgress(kind); float row = y + 67 + i++ * 57;
            Text(UpgradeName(kind), x + 20, row, 17); Text($"{progress.Available} pts", x + 233, row, 14, Gold);
            Text($"Cap {progress.Rank} / tower    {progress.Duplicates % 10}/10 duplicates to next cap", x + 20, row + 25, 11, Muted);
            Fill(x + 20, row + 44, 270, 2, Raised); Fill(x + 20, row + 44, 270 * (progress.Duplicates % 10) / 10f, 2, Teal);
        }
        Button("Pull / 5", x + 16, y + 419, 135, 44, () => Pull(1), true, session.Phase == GamePhase.Building);
        Button("Pull 10 / 50", x + 161, y + 419, 135, 44, () => Pull(10), false, session.Phase == GamePhase.Building);
        Button("Configure upgrade template", x + 16, height - 201, 280, 47, () => panel = 2);
    }
    private void DrawUpgradePanel(float x, float y)
    {
        Text("UPGRADE TEMPLATE", x + 20, y, 22);
        Text("Middle-click applies the points that fit.", x + 20, y + 33, 12, Muted);
        Text(selected is null ? "Aim at a tower to inspect its allocations." : $"{selected.Kind} #{selected.Id}   HP {selected.Hp:0}/{session.Economy.GetMaxHp(selected)}   ATK {session.Economy.GetAttack(selected)}", x + 20, y + 58, 13, Teal);
        int i = 0;
        foreach (var kind in Enum.GetValues<UpgradeKind>())
        {
            var progress = session.Economy.GetProgress(kind); float row = y + 89 + i++ * 51;
            int amount = TemplatePoints(kind), allocated = selected is null ? 0 : session.Economy.GetAllocated(selected, kind);
            Text(UpgradeName(kind), x + 20, row, 16);
            bool infinite = selected is not null && session.Economy.GetRange(selected, kind) == int.MaxValue;
            Text(infinite ? $"Have {progress.Available} / unlimited" : $"Have {progress.Available} / cap {progress.Rank} / used {allocated}", x + 20, row + 23, 11, Muted);
            Button("-", x + 190, row, 31, 34, () => ChangeTemplatePoints(kind, -1), false, amount > 0);
            Text(amount.ToString(), x + 232, row + 6, 18, amount > TemplateApplicable(kind) ? Muted : Gold);
            Button("+", x + 263, row, 31, 34, () => ChangeTemplatePoints(kind, 1), false, amount < TemplateMaximum(kind));
        }
        Button("J  /  Jump pattern", x + 16, y + 408, 177, 40, OpenJumpEditor);
        Button(upgradeTemplate.UseJumpPattern ? "Use: ON" : "Use: OFF", x + 201, y + 408, 95, 40, () => upgradeTemplate.UseJumpPattern = !upgradeTemplate.UseJumpPattern, upgradeTemplate.UseJumpPattern);
        Text($"Up to {upgradeTemplate.Points.Values.Sum(v => (long)v)} pts; unused points stay banked.", x + 20, y + 459, 12, Muted);
        Button("Ready / return to field", x + 16, height - 201, 280, 47, () => Capture(true), true);
    }
    private void DrawJumpEditor()
    {
        float x = width / 2 - 350, y = 140; Fill(x, y, 700, 610, Panel); Fill(x, y, 700, 3, Gold);
        Text("TEMPLATE JUMP PATTERN", x + 25, y + 22, 26); Text("Configure now; middle-click applies it together with your chosen points.", x + 25, y + 64, 15, Muted);
        var preview = TemplatePreview();
        int range = Math.Max(2, session.Economy.GetRange(preview, UpgradeKind.Jump)), budget = Math.Max(8, session.Economy.GetJumpBudget(preview));
        Text($"RANGE {range}     DESTINATIONS {draftPattern.Count}/{budget}     Y {jumpY:+0;-0;0}", x + 25, y + 95, 16, Gold);
        Button("Y -", x + 482, y + 127, 85, 36, () => jumpY = Math.Max(-range, jumpY - 1)); Button("Y +", x + 577, y + 127, 85, 36, () => jumpY = Math.Min(range, jumpY + 1));
        int displayRange = Math.Min(range, 4); int n = displayRange * 2 + 1; float size = Math.Min(54, 365f / n), gx = x + 42, gy = y + 164;
        for (int dz = -displayRange; dz <= displayRange; dz++) for (int dx = -displayRange; dx <= displayRange; dx++)
            {
                var offset = new Cell(dx + jumpX, jumpY, dz + jumpZ); bool valid = offset.Chebyshev >= 2 && offset.Chebyshev <= range; bool on = draftPattern.Contains(offset);
                float px = gx + (dx + displayRange) * size, pz = gy + (dz + displayRange) * size;
                Button(offset == new Cell(0, 0, 0) ? "+" : "", px, pz, size - 3, size - 3, () => { if (draftPattern.Contains(offset)) draftPattern.Remove(offset); else if (draftPattern.Count < budget) draftPattern.Add(offset); else SetNotice("All construction points are allocated."); }, on, valid);
                if (on) Fill(px + size / 2 - 3, pz + size / 2 - 3, 6, 6, Background);
            }
        Text("XZ SLICE", x + 482, y + 187, 16, Teal); Text("Up on grid = -Z", x + 482, y + 221, 14, Muted); Text("Right = +X", x + 482, y + 248, 14, Muted); Text("Y +/- changes height", x + 482, y + 275, 14, Muted);
        Text("Teal: active destination", x + 482, y + 307, 13, Muted); Text($"Preview: {preview.Kind}", x + 482, y + 330, 13, Gold);
        if (range > 4)
        {
            Text($"CENTER X {jumpX} / Z {jumpZ}", x + 482, y + 365, 13, Gold);
            Button("X -", x + 482, y + 393, 85, 34, () => jumpX = Math.Max(-range + 4, jumpX - 4));
            Button("X +", x + 577, y + 393, 85, 34, () => jumpX = Math.Min(range - 4, jumpX + 4));
            Button("Z -", x + 482, y + 437, 85, 34, () => jumpZ = Math.Max(-range + 4, jumpZ - 4));
            Button("Z +", x + 577, y + 437, 85, 34, () => jumpZ = Math.Min(range - 4, jumpZ + 4));
        }
        Button("Reset to knight pattern", x + 482, y + 487, 180, 34, () => { draftPattern.Clear(); draftPattern.AddRange(new Turret().JumpPattern); jumpX = jumpY = jumpZ = 0; });
        Button("Use in template", x + 25, y + 541, 315, 44, () =>
        {
            if (draftPattern.Count == 0) { SetNotice("Keep at least one jump destination."); return; }
            upgradeTemplate.JumpPattern.Clear(); upgradeTemplate.JumpPattern.AddRange(draftPattern);
            upgradeTemplate.UseJumpPattern = true; panel = 2;
            SetNotice("Pattern saved to template. Middle-click a tower to apply; no points spent yet.");
        }, true);
        Button("Cancel", x + 356, y + 541, 315, 44, () => panel = 2);
    }
    private void DrawHelpPanel(float x, float y)
    {
        Text("FIELD MANUAL", x + 20, y, 23);
        string[] lines = ["WASD  horizontal movement", "Space  ascend / Shift  descend", "Mouse  pan / Ctrl  move faster", "Tab  release or capture mouse", "Left click  remove tower / block", "Right click  place / pour", "Middle click  apply template", "1-4 pieces / 5 dirt / 6 stone", "7 dig / 8 water / 9 mercury / 0 lava", "L liquid shop / G gacha", "U upgrade template / J pattern", "Enter  begin prepared level", "P pause / V zone guides", "R retry / F1 this panel", "Close window  save & quit"]; int i = 0;
        foreach (var line in lines) Text(line, x + 20, y + 49 + i++ * 27, 15, Muted);
        Button("Return to field", x + 16, height - 201, 280, 47, () => { panel = 0; Capture(true); }, true);
    }
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 3)] + "...";
    protected override void UnloadContent() { renderer.Dispose(); font.Dispose(); pixel.Dispose(); batch.Dispose(); base.UnloadContent(); }
}
