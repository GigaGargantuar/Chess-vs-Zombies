using ChessVsZombies.Core;
using ChessVsZombies.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessVsZombies;

public sealed class DefenseGame : Game
{
    private readonly GraphicsDeviceManager graphics;
    private GameSession session = new();
    private SpriteBatch batch = null!;
    private Texture2D pixel = null!;
    private UiFont font = null!;
    private WorldRenderer renderer = null!;
    private JumpEditorView jumpView = null!;
    private KeyboardState previousKeys;
    private MouseState previousMouse;
    private bool wasActive, synchronizeInput = true, suppressMouseUntilReleased;
    private readonly FlyCamera cameraControl = new();
    private Vector3 camera => WorldRenderer.ToXna(cameraControl.Position);
    private bool captured = true, paused, showZones = true, saveBlocked, batchAllTurrets = true;
    private readonly TowerBatchSelection batchSelection = new();
    private bool batchSelectMode, batchCornerDepthOverride;
    private float batchCornerDepth = 8;
    private Cell? batchCorner;
    private int tool, panel, pourUnits = 10;
    private Material recoveredMaterial = Material.Wood;
    private Material BlockMaterial => tool == 4 ? Material.Dirt : tool == 5 ? Material.Stone : recoveredMaterial;
    private Turret? selected;
    private Cell? hit, placement, highlight;
    private CrustFace? obsidianPlacementFace;
    private bool hitLiquid;
    private string notice = "Place your first pieces. Press Tab to use the command panel.";
    private float noticeTime = 8;
    private float uiScale = 1, width = 1440, height = 900;
    private readonly List<(Rectangle Area, Action Action)> buttons = [];
    private readonly List<Cell> draftPattern = [];
    private readonly JumpEditorModel jumpEditor = new();
    private Cell? jumpPreview;
    private UpgradeTemplate upgradeTemplate => session.UpgradeTemplate;
    private UpgradeKind? templateField;
    private string templateFieldText = "";
    private bool templateFieldSelected;
    private Rectangle templateFieldBounds;
    private readonly bool smoke, smokeCombat;
    private readonly bool smokeFormationRestore;
    private readonly bool smokeHorde;
    private readonly bool smokeSaveHorde;
    private readonly bool smokeSaveWave, instantWave, baseAttack;
    private readonly int profileWarmupTicks;
    private readonly bool smokePerformanceRecovery;
    private readonly bool profileSmoke;
    private readonly bool uncachedTurrets;
    private readonly bool verifyRenderGeometry;
    private readonly List<WorldRenderProfile> hordeRenderProfiles = [];
    private int hordeInitialTurrets, hordeInitialEnemies, hordePeakProjectiles;
    private int hordeMeasuredUpdateStart = int.MaxValue;
    private int hordeStartingLevel, hordeInitialSpawned, hordeInitialQueued;
    private int hordeSpawnedBeforePreRoll, hordeQueuedBeforePreRoll;
    private int hordeInitialMinAttack, hordeInitialMaxAttack;
    private WaveProfileInfo[] hordeWavePlans = [];
    private readonly record struct WaveProfileInfo(int Index, int TotalCount, float SpawnInterval);
    private readonly bool smokeMegaGuard, smokeGiantDeaths, smokeThrownCover, smokeTowerPenetration, smokeTerrainImpact;
    private readonly string smokePanel;
    private readonly string? capturePath;
    private Zombie? smokeArmorDemo;
    private int frameCount;
    private List<double>? hordeUpdateTimes, hordeFrameTimes;
    private readonly List<double> hordeDrawTimes = [], hordeHudTimes = [];
    private long previousHordeFrame;
    private readonly CombatOverlayPerformance overlayPerformance = new();
    private bool showDeathToll;
    private long previousOverlayFrame;
    private bool previousOverlayFrameActive;
    private int reducedOverlayFrames;
    private readonly int captureFrame = 90;
    private double elapsed;
    private readonly string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChessVsZombies", "campaign.json");
    private static readonly Material[] SaleMaterials = [Material.Wood, Material.Stone, Material.Copper, Material.Iron, Material.Gold, Material.Diamond, Material.Osmium, Material.Obsidian];
    private static readonly Color Background = new(11, 17, 26), Panel = new(17, 26, 37), Raised = new(26, 39, 52), TextColor = new(230, 235, 232), Muted = new(138, 157, 170), Teal = new(105, 225, 191), Gold = new(232, 190, 114), Red = new(235, 125, 112);

    public DefenseGame(string[] args)
    {
        graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 1440, PreferredBackBufferHeight = 900, GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = true };
        Window.Title = "Chess vs Zombies";
        Window.AllowUserResizing = true;
        Window.TextInput += (_, e) =>
        {
            if (!IsActive || captured || panel != 2 || templateField is null) return;
            if (e.Character is >= '0' and <= '9')
            {
                if (templateFieldSelected) templateFieldText = "";
                templateFieldSelected = false;
                if (templateFieldText.Length < 10) templateFieldText += e.Character;
            }
            else if (e.Character == '\b')
            {
                templateFieldText = templateFieldSelected || templateFieldText.Length == 0 ? "" : templateFieldText[..^1];
                templateFieldSelected = false;
            }
        };
        IsMouseVisible = false;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1d / 60);
        smokeMegaGuard = args.Contains("--smoke-mega-guard");
        smokeGiantDeaths = args.Contains("--smoke-giant-deaths");
        smokeThrownCover = args.Contains("--smoke-thrown-cover");
        smokeTowerPenetration = args.Contains("--smoke-tower-penetration");
        smokeTerrainImpact = args.Contains("--smoke-terrain-impact");
        smokeFormationRestore = args.Contains("--smoke-formation-restore");
        smokeSaveHorde = args.Contains("--smoke-save-horde");
        smokeSaveWave = args.Contains("--smoke-save-wave");
        instantWave = args.Contains("--instant-wave");
        baseAttack = args.Contains("--base-attack");
        smokePerformanceRecovery = args.Contains("--smoke-performance-recovery");
        profileSmoke = args.Contains("--profile-smoke");
        uncachedTurrets = args.Contains("--uncached-turrets");
        verifyRenderGeometry = args.Contains("--verify-render-geometry");
        smokeHorde = args.Contains("--smoke-horde") || smokeSaveHorde || smokeSaveWave || smokePerformanceRecovery;
        smoke = args.Contains("--smoke") || args.Contains("--smoke-combat") || args.Contains("--smoke-gacha") || args.Contains("--smoke-jump") || args.Contains("--smoke-template") || args.Contains("--smoke-terrain") || args.Contains("--smoke-liquids") || args.Contains("--smoke-liquid-turrets") || args.Contains("--smoke-armor") || args.Contains("--smoke-falls") || args.Contains("--smoke-bridges") || args.Contains("--smoke-buckets") || args.Contains("--smoke-sizes") || args.Contains("--smoke-megas") || args.Contains("--smoke-merged") || args.Contains("--smoke-all-in-one");
        smoke = smoke || smokeMegaGuard || smokeGiantDeaths || smokeThrownCover || smokeTowerPenetration || smokeTerrainImpact || smokeHorde;
        smoke = smoke || args.Contains("--smoke-formation") || smokeFormationRestore;
        smoke = smoke || args.Contains("--smoke-batch-select") || args.Contains("--smoke-batch-spend");
        smoke = smoke || args.Contains("--smoke-camera-up") || args.Contains("--smoke-camera-down");
        smoke = smoke || args.Contains("--smoke-jump-cost");
        smoke = smoke || args.Contains("--smoke-micro");
        smoke = smoke || args.Contains("--smoke-bottom-layer");
        smokeCombat = args.Contains("--smoke-combat");
        showDeathToll = args.Contains("--smoke-death-toll");
        smokePanel = args.Contains("--smoke-gacha") ? "gacha" : args.Contains("--smoke-jump") ? "jump" : args.Contains("--smoke-template") ? "template" : args.Contains("--smoke-terrain") ? "terrain" : args.Contains("--smoke-liquids") ? "liquids" : args.Contains("--smoke-armor") ? "armor" : args.Contains("--smoke-falls") ? "falls" : args.Contains("--smoke-bridges") ? "bridges" : args.Contains("--smoke-buckets") ? "buckets" : args.Contains("--smoke-sizes") ? "sizes" : args.Contains("--smoke-megas") ? "megas" : args.Contains("--smoke-merged") ? "merged" : "";
        if (args.Contains("--smoke-all-in-one")) smokePanel = "all-in-one";
        if (args.Contains("--smoke-liquid-turrets")) smokePanel = "liquid-turrets";
        if (smokeMegaGuard || smokeGiantDeaths) smokePanel = "megas";
        if (smokeThrownCover) smokePanel = "thrown";
        if (smokeTowerPenetration) smokePanel = "penetration";
        if (smokeTerrainImpact) smokePanel = "terrain-impact";
        if (smokeHorde) smokePanel = "horde";
        if (args.Contains("--smoke-formation") || smokeFormationRestore) smokePanel = "formation";
        if (args.Contains("--smoke-batch-select") || args.Contains("--smoke-batch-spend")) smokePanel = args.Contains("--smoke-batch-spend") ? "batch-spend" : "batch-select";
        if (args.Contains("--smoke-camera-up") || args.Contains("--smoke-camera-down")) smokePanel = args.Contains("--smoke-camera-up") ? "camera-up" : "camera-down";
        if (args.Contains("--smoke-jump-cost")) smokePanel = "jump-cost";
        if (args.Contains("--smoke-micro")) smokePanel = "micro";
        if (args.Contains("--smoke-bottom-layer")) smokePanel = "bottom-layer";
        // One deterministic update per captured frame; slow GPU/startup frames must
        // not replay demonstration triggers or skip the animation being inspected.
        if (smoke) IsFixedTimeStep = false;
        if (smokePanel == "armor") cameraControl = new FlyCamera(new System.Numerics.Vector3(20, 19, 31), MathF.PI, -.31f);
        if (smokePanel == "liquids") cameraControl = new FlyCamera(new System.Numerics.Vector3(18.5f, 18, 25), MathF.PI, -.54f);
        if (smokePanel == "liquid-turrets") cameraControl = new FlyCamera(new System.Numerics.Vector3(20.5f, 19, 27), MathF.PI, -MathF.Atan2(2.5f, 5.5f));
        if (smokePanel == "falls") cameraControl = new FlyCamera(new System.Numerics.Vector3(17, 23, 31), MathF.PI, -.4f);
        if (smokePanel == "bridges") cameraControl = new FlyCamera(new System.Numerics.Vector3(20.5f, 21, 31), MathF.PI, -.39f);
        if (smokePanel == "buckets") cameraControl = new FlyCamera(new System.Numerics.Vector3(17, 20.5f, 12), .26f, -.35f);
        if (smokePanel == "sizes") cameraControl = new FlyCamera(new System.Numerics.Vector3(22.5f, 20, 31), MathF.PI, -.35f);
        if (smokePanel == "micro") cameraControl = new FlyCamera(new System.Numerics.Vector3(22.5f, 20, 33), MathF.PI, -.38f);
        if (smokePanel == "megas") cameraControl = new FlyCamera(new System.Numerics.Vector3(17, 26, 42), MathF.PI, -.34f);
        if (smokeThrownCover) cameraControl = new FlyCamera(new System.Numerics.Vector3(17, 25, 32), MathF.PI, -.3f);
        if (smokeTowerPenetration) cameraControl = new FlyCamera(new System.Numerics.Vector3(17, 23, 34), MathF.PI, -.54f);
        if (smokeTerrainImpact) cameraControl = new FlyCamera(new System.Numerics.Vector3(17, 22, 32), MathF.PI, -.5f);
        if (smokeHorde) cameraControl = new FlyCamera(new System.Numerics.Vector3(16, 43, 47), MathF.PI, -.72f);
        if (smokePanel == "formation") cameraControl = new FlyCamera(new System.Numerics.Vector3(23, 22, 32), MathF.PI, -.52f);
        if (smokePanel is "batch-select" or "batch-spend") cameraControl = new FlyCamera(new System.Numerics.Vector3(23, 22, 32), MathF.PI, -.52f);
        if (smokePanel == "camera-up") cameraControl = new FlyCamera(new System.Numerics.Vector3(22.5f, 19, 22.5f), .7f, MathF.PI / 2);
        if (smokePanel == "camera-down") cameraControl = new FlyCamera(new System.Numerics.Vector3(22.5f, 30, 22.5f), .7f, -MathF.PI / 2);
        if (smokePanel == "jump-cost") cameraControl = new FlyCamera(new System.Numerics.Vector3(22.5f, 19, 28), MathF.PI, -MathF.Atan2(2.5f, 5.5f));
        if (smokePanel == "bottom-layer") cameraControl = new FlyCamera(new System.Numerics.Vector3(8.5f, 4.5f, 14.5f), MathF.PI, -MathF.Atan2(4.5f, 6));
        int frameIndex = Array.IndexOf(args, "--capture-frame");
        if (frameIndex >= 0 && frameIndex + 1 < args.Length && int.TryParse(args[frameIndex + 1], out var requestedFrame))
            captureFrame = Math.Clamp(requestedFrame, 1, 3600);
        int warmupIndex = Array.IndexOf(args, "--profile-warmup-ticks");
        if (warmupIndex >= 0)
        {
            if (warmupIndex + 1 >= args.Length || !int.TryParse(args[warmupIndex + 1], out var requestedWarmup) || requestedWarmup < 0)
                throw new ArgumentException("--profile-warmup-ticks requires a nonnegative integer.");
            profileWarmupTicks = requestedWarmup;
        }
        if (instantWave && !smokeSaveWave) throw new ArgumentException("--instant-wave requires --smoke-save-wave.");
        if (baseAttack && !smokeSaveWave) throw new ArgumentException("--base-attack requires --smoke-save-wave.");
        int captureIndex = Array.IndexOf(args, "--capture");
        capturePath = captureIndex >= 0 && captureIndex + 1 < args.Length ? Path.GetFullPath(args[captureIndex + 1]) : null;
        int saveIndex = Array.IndexOf(args, "--save-path");
        if (saveIndex >= 0 && saveIndex + 1 < args.Length) savePath = Path.GetFullPath(args[saveIndex + 1]);
        Activated += (_, _) => { synchronizeInput = true; suppressMouseUntilReleased = true; };
        Deactivated += (_, _) => ReleaseCursorForFocusLoss();
        Window.ClientSizeChanged += (_, _) => { if (captured) CenterMouse(); };
        Exiting += (_, _) => { CommitTemplateField(); SaveCheckpoint(); };
    }
    protected override void Initialize()
    {
        base.Initialize();
        DesktopWindow.Maximize(Window);
    }
    protected override void LoadContent()
    {
        batch = new SpriteBatch(GraphicsDevice); pixel = new Texture2D(GraphicsDevice, 1, 1); pixel.SetData(new[] { Color.White });
        font = new UiFont(GraphicsDevice); renderer = new WorldRenderer(GraphicsDevice); jumpView = new JumpEditorView(GraphicsDevice);
        renderer.UseTurretCache = !smoke || !uncachedTurrets;
        renderer.VerifyGeometry = smoke && verifyRenderGeometry;
        if (smokeHorde)
        {
            PrepareHordeSmoke();
            if (profileSmoke) { session.Profile = new SimulationProfile(); renderer.ProfileEnabled = true; }
            SetNotice(smokeSaveWave ? $"Checkpoint level {session.LevelNumber} / {session.Turrets.Count} towers / {(instantWave ? "simultaneous wave" : "normal spawn queue")} / isolated simulation."
                : smokeSaveHorde ? "Checkpoint-based approximation / 409 towers versus 300 zombies / isolated simulation."
                : "500 simultaneous zombies / 388 Standard, 97 Milli, 10 Kilo and 5 Mega / live rendered simulation.");
        }
        else if (smoke)
        {
            foreach (var c in new[] { new Cell(11, 16, 15), new Cell(11, 16, 16), new Cell(20, 16, 15), new Cell(20, 16, 16), new Cell(15, 16, 11), new Cell(16, 16, 11), new Cell(15, 16, 20), new Cell(16, 16, 20) })
                session.Deploy((PieceKind)(session.Turrets.Count % 4), c);
            for (int i = 0; i < 24; i++) session.Economy.Pull(session.Turrets);
            selected = session.Turrets[0];
            session.Economy.Apply(selected, UpgradeKind.ATK);
            session.Inventory.Add(Material.Dirt, 3); session.Inventory.Add(Material.Stone, 2);
            session.BuildBarrier(new Cell(13, 16, 13), Material.Stone); session.BuildBarrier(new Cell(13, 17, 13), Material.Dirt);
            if (smokeCombat && smokePanel is not ("merged" or "all-in-one")) { session.StartLevel(); session.Update(12f); }
            if (smokePanel == "gacha") panel = 1;
            if (smokePanel == "template")
            {
                for (int y = 16; y <= 18; y++) session.Deploy((PieceKind)(y - 16), new Cell(20, y, 20));
                var health = session.Economy.GetProgress(UpgradeKind.HP);
                health.PullCount = 41;
                health.Available = 8;
                session.Economy.Apply(selected, UpgradeKind.HP);
                health.PullCount = 51;
                upgradeTemplate.Points[UpgradeKind.HP] = 2; upgradeTemplate.Points[UpgradeKind.Jump] = 1; panel = 2;
                if (session.Economy.GetAllocated(selected, UpgradeKind.HP) != 5 ||
                    TemplateMaximum(UpgradeKind.HP) != 2 ||
                    TemplateApplicable(UpgradeKind.HP) != 2)
                    throw new InvalidOperationException("The upgrade template must show a fixed level-5 tower and a two-point catch-up to cap 6.");
                upgradeTemplate.Points[UpgradeKind.HP] = 1;
                if (TemplateApplicable(UpgradeKind.HP) != 0)
                    throw new InvalidOperationException("A one-point template budget cannot buy a catch-up.");
                upgradeTemplate.Points[UpgradeKind.HP] = 2;
                var cameraTarget = selected.Cell.Center + System.Numerics.Vector3.UnitY * 7;
                var cameraMove = cameraTarget - cameraControl.Position;
                var cameraForward = new System.Numerics.Vector3(MathF.Sin(cameraControl.Yaw), 0, MathF.Cos(cameraControl.Yaw));
                var cameraRight = new System.Numerics.Vector3(-cameraForward.Z, 0, cameraForward.X);
                cameraControl.MovePrecise(new System.Numerics.Vector3(System.Numerics.Vector3.Dot(cameraMove, cameraRight),
                    cameraMove.Y, System.Numerics.Vector3.Dot(cameraMove, cameraForward)), cameraMove.Length(), _ => false);
                cameraControl.Look(0, (cameraControl.Pitch + MathF.PI / 2) / .0025f);
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
            if (smokePanel == "falls")
            {
                for (int y = 16; y <= 20; y++) session.Deploy(PieceKind.Rook, new Cell(20, y, 22));
                session.Inventory.Add(Material.Dirt, 4);
                for (int y = 16; y <= 19; y++) session.BuildBarrier(new Cell(13, y, 18));
            }
            if (smokePanel == "bridges") PrepareBridgeSmoke();
            if (smokePanel == "liquid-turrets") PrepareLiquidTurretSmoke();
            if (smokePanel == "buckets") PrepareBucketSmoke();
            if (smokePanel == "sizes") PrepareSizeSmoke();
            if (smokePanel == "micro") PrepareMicroSmoke();
            if (smokePanel == "megas") PrepareMegaSmoke();
            if (smokeThrownCover) PrepareThrownCoverSmoke();
            if (smokeTowerPenetration) PrepareTowerPenetrationSmoke();
            if (smokeTerrainImpact) PrepareTerrainImpactSmoke();
            if (smokePanel == "formation") PrepareFormationSmoke();
            if (smokePanel is "batch-select" or "batch-spend") PrepareBatchSelectionSmoke();
            if (smokePanel is "camera-up" or "camera-down") PrepareVerticalCameraSmoke();
            if (smokePanel == "jump-cost") PrepareJumpCostSmoke();
            if (smokePanel == "bottom-layer") PrepareBottomLayerSmoke();
            if (smokePanel is "merged" or "all-in-one")
            {
                if (smokePanel == "all-in-one") session.SetAllInOne(true);
                else session.SetMergeFiveLevels(true);
                if (smokeCombat) { session.StartLevel(); session.Update(12f); }
                Capture(false);
            }
            SetNotice(smokeTerrainImpact ? "Terrain impact / a near-ground chunk scatters and settles while bullets keep flying." :
                smokeTowerPenetration ? "Tower penetration / a stone block pierces three weak towers, then stops at the fourth." :
                smokeThrownCover ? "Thrown cover / incoming fire damages real blocks in flight, leaving holes when they break." :
                smokeMegaGuard ? "Mega mobile cover / advancing to the core while guarding sustained incoming fire." :
                smokeGiantDeaths ? "Released Kilo hands and Mega chunks / finite blocks fall and settle after death." :
                smokePanel == "all-in-one" ? "All in one / 5x spawn rate / final-wave equipment / 75 currency per surviving tower." :
                smokePanel == "merged" ? "Five levels in one battle. Every surviving tower pays 25 currency on victory." :
                smokePanel == "megas" ? "Mega / 3 x 3 x 3. Two hands, two real blocks per hand. Stick left / chunky throw right." :
                smokePanel == "sizes" ? "Milli / Standard / Kilo. Two independent hands within one 2 x 2 x 2 volume." :
                smokePanel == "micro" ? "Micro flankers / quarter size / 2 HP / 3x speed / incoming-fire dodges and moving turret attacks." :
                smokePanel == "liquid-turrets" ? "Liquid sharing / wet tower left / obsidian shell at crosshair / one unit of room for pouring." :
                smokePanel == "formation" ? "Saved formation / ghost towers mark missing positions / auto restore constructs one tower at a time." :
                smokePanel is "batch-select" or "batch-spend" ? "Batch selection / two 3D corners, one added tower and one excluded tower / upgrade only the cyan group." :
                smokePanel is "camera-up" or "camera-down" ? "Quaternion camera / exact vertical view / traditional mouse panning and level movement." :
                smokePanel == "jump-cost" ? "Jump request 71 / radius 2 / spent 2 / 69 banked / identical reapplication spends 0." :
                smokePanel == "bottom-layer" ? "Bottom layer Y 0 / tower and fortification on the void floor / crosshair can deploy into empty space." :
                smokeCombat ? "Live simulation / rendered validation" : "Crosshair placement / rendered validation");
        }
        else LoadCheckpoint();
        CenterMouse();
        base.LoadContent();
    }
    private void PrepareFormationSmoke()
    {
        session.Reset();
        for (int x = 20; x <= 24; x += 2)
            for (int z = 20; z <= 24; z += 2)
            {
                session.Deploy((PieceKind)((x + z) / 2 % 4), new Cell(x, 16, z));
                if (z == 22) session.Deploy(PieceKind.Knight, new Cell(x, 17, z));
            }
        if (smokeFormationRestore)
        {
            session.Economy.GetProgress(UpgradeKind.HP).PullCount = 2;
            session.Economy.GetProgress(UpgradeKind.HP).Available = 2;
            session.Economy.Apply(session.TrySelectTurret(new Cell(22, 16, 22))!, UpgradeKind.HP);
        }
        session.SaveFormation();
        foreach (var turret in session.Turrets.Where(t => t.Cell.X != 20).OrderByDescending(t => t.Cell.Y).ToArray())
            session.Remove(turret.Cell);
        selected = null;
        panel = 8;
        Capture(false);
        if (smokeFormationRestore)
        {
            session.BuyTool(ToolKind.Shovel);
            session.Inventory.Add(Material.Dirt, 2);
            session.BuildBarrier(new Cell(22, 16, 20));
            session.Dig(new Cell(24, 15, 20));
            session.StartAutoRestoreFormation();
        }
    }
    private void PrepareBatchSelectionSmoke()
    {
        session.Reset();
        batchSelection.Clear();
        for (int x = 20; x <= 24; x += 2)
            for (int z = 20; z <= 24; z += 2)
            {
                session.Deploy((PieceKind)((x + z) / 2 % 4), new Cell(x, 16, z));
                if (z == 22) session.Deploy(PieceKind.Knight, new Cell(x, 17, z));
            }
        foreach (var kind in new[] { UpgradeKind.HP, UpgradeKind.ATK })
        {
            session.Economy.GetProgress(kind).PullCount = 21;
            session.Economy.GetProgress(kind).Available = 21;
        }
        upgradeTemplate.Points[UpgradeKind.HP] = 2;
        upgradeTemplate.Points[UpgradeKind.ATK] = 2;
        batchSelectMode = true;
        batchAllTurrets = false;
        batchCorner = new Cell(20, 16, 20);
        SelectBatchCorner();
        batchCorner = new Cell(22, 17, 22);
        SelectBatchCorner();
        hit = new Cell(24, 16, 24);
        ChangeBatchTowerSelection(true);
        hit = new Cell(20, 16, 20);
        ChangeBatchTowerSelection(false);
        if (BatchTargets().Length != 6) throw new InvalidOperationException("The batch selection smoke must select six towers.");
        if (smokePanel == "batch-spend")
        {
            BuyUpgradeBatch(false);
            if (session.Turrets.Count(t => session.Economy.GetAllocated(t, UpgradeKind.HP) == 3 &&
                session.Economy.GetAllocated(t, UpgradeKind.ATK) == 3) != 6 ||
                session.Economy.GetProgress(UpgradeKind.HP).Available != 9 || session.Economy.GetProgress(UpgradeKind.ATK).Available != 9)
                throw new InvalidOperationException("Batch spending must affect only the six selected towers.");
        }
        selected = null;
        panel = 1;
        Capture(false);
    }
    private void PrepareVerticalCameraSmoke()
    {
        session.Reset();
        session.Inventory.Add(Material.Stone, 40);
        for (int y = 16; y <= 22; y++) session.BuildBarrier(new Cell(20, y, 20), Material.Stone);
        for (int x = 20; x <= 24; x++)
            for (int z = 20; z <= 24; z++)
                if (x != 20 || z != 20) session.BuildBarrier(new Cell(x, 22, z), Material.Stone);
        for (int x = 20; x <= 24; x += 2)
            for (int z = 20; z <= 24; z += 2)
                session.Deploy((PieceKind)((x + z) / 2 % 4), new Cell(x, 23, z));
        if (session.Turrets.Count != 9) throw new InvalidOperationException("The vertical camera smoke requires nine elevated towers.");
        selected = null;
        Capture(false);
    }
    private void PrepareJumpCostSmoke()
    {
        session.Reset();
        var cell = new Cell(22, 16, 22);
        session.Deploy(PieceKind.Knight, cell);
        var jump = session.Economy.GetProgress(UpgradeKind.Jump);
        jump.PullCount = 701;
        jump.Available = 71;
        upgradeTemplate.Points[UpgradeKind.Jump] = 71;
        upgradeTemplate.UseJumpPattern = true;
        upgradeTemplate.JumpPattern = [new Cell(2, 0, 0)];
        hit = cell;
        hitLiquid = false;
        ApplyTemplateToTarget();
        ApplyTemplateToTarget();
        if (jump.Available != 69 || session.Economy.GetAllocated(session.Turrets[0], UpgradeKind.Jump) != 71)
            throw new InvalidOperationException("A radius-2 jump pattern must spend two points once, reaching cap 71 and leaving 69 banked.");
        panel = 2;
        Capture(false);
    }
    private void PrepareBottomLayerSmoke()
    {
        session = new GameSession();
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(session.World);
        for (int x = 3; x <= 13; x++)
            for (int z = 3; z <= 15; z++)
                for (int y = 0; y < 16; y++)
                    if (!dig(new Cell(x, y, z))) throw new InvalidOperationException("Could not clear the bottom-layer demonstration.");
        if (!session.Deploy(PieceKind.Rook, new Cell(6, 0, 8))) throw new InvalidOperationException(session.Message);
        session.Inventory.Add(Material.Stone);
        if (!session.BuildBarrier(new Cell(10, 0, 8), Material.Stone)) throw new InvalidOperationException(session.Message);
        var zombie = new Zombie { Id = -100, WaveIndex = -1, Kind = ZombieKind.Basic };
        zombie.Position = zombie.StandAt(new Cell(8, 0, 5));
        zombie.FaceToward(System.Numerics.Vector3.UnitZ);
        session.Zombies.Add(zombie);
        selected = null;
        tool = panel = 0;
        paused = true;
        Aim();
        if (placement is not { } target || target != new Cell(8, 0, 8) || !session.CanDeploy(PieceKind.Pawn, target))
            throw new InvalidOperationException("The crosshair must allow deployment onto the empty bottom layer.");
    }
    private void PrepareHordeSmoke()
    {
        if (smokeSaveWave) { PrepareSaveWaveSmoke(); return; }
        if (smokeSaveHorde) { PrepareSaveHordeSmoke(); return; }
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1, Name = "500-zombie horde",
            Waves = [new WaveDefinition
            {
                SpawnInterval = .28f,
                Groups =
                [
                    new() { Count = 388, Size = ZombieSize.Standard, Kind = ZombieKind.ArmedArmored, Tier = GearTier.Osmium },
                    new() { Count = 97, Size = ZombieSize.Milli, Kind = ZombieKind.Armored, Tier = GearTier.Osmium },
                    new() { Count = 10, Size = ZombieSize.Kilo, Kind = ZombieKind.ArmedArmored, Tier = GearTier.Osmium, IsBoss = true },
                    new() { Count = 5, Size = ZombieSize.Mega, Kind = ZombieKind.ArmedArmored, Tier = GearTier.Osmium, IsBoss = true }
                ]
            }]
        });
        session = new GameSession(levels: levels);
        foreach (int z in new[] { 5, 10, 21, 26 })
            foreach (int x in new[] { 3, 7, 11, 20, 24, 28 })
                if (!session.Deploy((x + z) % 2 == 0 ? PieceKind.Rook : PieceKind.Knight, new Cell(x, 16, z)))
                    throw new InvalidOperationException(session.Message);
        session.Inventory.Add(Material.Dirt, 16);
        foreach (int axis in new[] { 6, 12, 19, 25 })
            foreach (var cell in new[] { new Cell(5, 16, axis), new Cell(26, 16, axis), new Cell(axis, 16, 5), new Cell(axis, 16, 26) })
                if (!session.BuildBarrier(cell)) throw new InvalidOperationException(session.Message);
        if (!session.StartLevel()) throw new InvalidOperationException(session.Message);
        // Fill the authored wave through production spawning without advancing AI,
        // timers or positions. This demonstration never loads or saves a checkpoint.
        var spawn = typeof(GameSession).GetMethod("Spawn", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<WaveState, bool>>(session);
        var wave = session.Waves.Single();
        while (wave.Spawned < wave.TotalCount)
            if (!spawn(wave)) throw new InvalidOperationException($"Horde smoke could spawn only {wave.Spawned}/{wave.TotalCount} zombies.");
        if (session.Zombies.Count != 500) throw new InvalidOperationException("Horde smoke requires exactly 500 live zombies.");
        InitializeHordeMetrics();
    }
    private void PrepareSaveHordeSmoke()
    {
        session = SaveHordeScenario.Create(savePath);
        InitializeHordeMetrics();
    }
    private void PrepareSaveWaveSmoke()
    {
        if (!SaveStore.TryLoad(savePath, out var loaded, out var error))
            throw new InvalidOperationException($"Cannot load checkpoint for isolated profiling: {error}");
        session = loaded!;
        if (baseAttack)
            foreach (var turret in session.Turrets) turret.UpgradePoints.Remove(UpgradeKind.ATK);
        if (!session.StartLevel()) throw new InvalidOperationException(session.Message);
        if (instantWave)
        {
            var spawn = typeof(GameSession).GetMethod("Spawn", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<WaveState, bool>>(session);
            foreach (var wave in session.Waves)
            {
                wave.Started = true;
                while (wave.Spawned < wave.TotalCount)
                    if (!spawn(wave)) throw new InvalidOperationException($"Could not stage the full saved wave: {wave.Spawned}/{wave.TotalCount} enemies in wave {wave.Index + 1}.");
            }
        }
        InitializeHordeMetrics();
    }
    private void InitializeHordeMetrics()
    {
        hordeStartingLevel = session.LevelNumber;
        hordeWavePlans = session.Waves.Select(wave => new WaveProfileInfo(wave.Index, wave.TotalCount, session.Level.Waves[wave.Index].SpawnInterval)).ToArray();
        hordeSpawnedBeforePreRoll = session.Waves.Sum(wave => wave.Spawned);
        hordeQueuedBeforePreRoll = session.Waves.Sum(wave => wave.TotalCount - wave.Spawned);
        // Optional production pre-roll reaches a later battle state without adding
        // setup work to measured samples or touching the checkpoint on disk.
        for (int tick = 0; tick < profileWarmupTicks; tick++)
        {
            if (session.Phase != GamePhase.Combat)
                throw new InvalidOperationException($"Combat ended during profile pre-roll at tick {tick}/{profileWarmupTicks}.");
            session.Update(1f / 60);
        }
        if (profileWarmupTicks > 0 && session.Phase != GamePhase.Combat)
            throw new InvalidOperationException("Combat ended during profile pre-roll.");
        hordeInitialTurrets = session.Turrets.Count;
        hordeInitialEnemies = session.Zombies.Count;
        hordeInitialSpawned = session.Waves.Sum(wave => wave.Spawned);
        hordeInitialQueued = session.Waves.Sum(wave => wave.TotalCount - wave.Spawned);
        var attacks = session.Turrets.Select(session.Economy.GetAttack).ToArray();
        hordeInitialMinAttack = attacks.Length == 0 ? 0 : attacks.Min();
        hordeInitialMaxAttack = attacks.Length == 0 ? 0 : attacks.Max();
        hordePeakProjectiles = session.Projectiles.Count;
        hordeUpdateTimes = [];
        hordeFrameTimes = [];
        showZones = false;
        selected = null;
        paused = false;
        Capture(false);
    }
    private void ReportHordeSmoke()
    {
        const int warmup = 30;
        static object Statistics(IEnumerable<double> samples)
        {
            var ordered = samples.Order().ToArray();
            return ordered.Length == 0 ? new { Samples = 0, MeanMs = 0d, MedianMs = 0d, P95Ms = 0d, MaxMs = 0d } :
                new { Samples = ordered.Length, MeanMs = ordered.Average(), MedianMs = ordered.Length % 2 == 0 ?
                    (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2 : ordered[ordered.Length / 2],
                    P95Ms = ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], MaxMs = ordered[^1] };
        }
        var metrics = new
        {
            Frames = frameCount, SimulatedSeconds = hordeUpdateTimes!.Count / 60d,
            CheckpointApproximation = smokeSaveHorde,
            ExactCheckpoint = smokeSaveWave && !baseAttack, InstantWave = instantWave, BaseAttack = baseAttack,
            StartingLevel = hordeStartingLevel, WavePlans = hordeWavePlans,
            PreRollTicks = profileWarmupTicks, PreRollSeconds = profileWarmupTicks / 60d,
            SpawnedBeforePreRoll = hordeSpawnedBeforePreRoll, QueuedBeforePreRoll = hordeQueuedBeforePreRoll,
            InitialTurrets = hordeInitialTurrets, InitialEnemies = hordeInitialEnemies,
            InitialMinTurretAttack = hordeInitialMinAttack, InitialMaxTurretAttack = hordeInitialMaxAttack,
            InitialSpawned = hordeInitialSpawned, InitialQueued = hordeInitialQueued,
            BackBufferWidth = GraphicsDevice.PresentationParameters.BackBufferWidth,
            BackBufferHeight = GraphicsDevice.PresentationParameters.BackBufferHeight,
            VerticalSync = graphics.SynchronizeWithVerticalRetrace,
            PeakProjectiles = hordePeakProjectiles,
            VerifiedGeometryChecks = renderer.VerifiedGeometryChecks,
            Spawned = session.Waves.Sum(w => w.Spawned), LiveZombies = session.Zombies.Count(z => z.Hp > 0),
            Queued = session.Waves.Sum(wave => wave.TotalCount - wave.Spawned),
            LivingTowers = session.Turrets.Count(t => t.Hp > 0), session.CoreHp, session.Phase,
            WarmupFramesExcluded = warmup,
            WarmupUpdatesExcluded = Math.Min(hordeMeasuredUpdateStart, hordeUpdateTimes.Count),
            MeasuredUpdates = Math.Max(0, hordeUpdateTimes.Count - hordeMeasuredUpdateStart),
            CoreUpdate = Statistics(hordeUpdateTimes.Skip(hordeMeasuredUpdateStart)),
            // Intervals between completed draws include presentation and any frame
            // limiting; they are native rendered-frame timings, not isolated GPU time.
            RenderedFrame = Statistics(hordeFrameTimes!.Skip(warmup - 1)),
            DrawCpu = Statistics(hordeDrawTimes.Skip(warmup)),
            HudCpu = Statistics(hordeHudTimes.Skip(warmup)),
            ReducedOverlayFrames = reducedOverlayFrames,
            PerformanceMode = overlayPerformance.ReducedDetail,
            SyntheticFrameTimings = smokePerformanceRecovery,
            LaserVertices = renderer.LastProfile.LaserVertices,
            SimulationStages = hordeUpdateTimes.Count > hordeMeasuredUpdateStart ? session.Profile?.Report(hordeUpdateTimes.Count - hordeMeasuredUpdateStart) : null,
            RenderStages = hordeRenderProfiles.Count == 0 ? null : new
            {
                TotalMs = hordeRenderProfiles.Average(p => p.TotalMs),
                TerrainRebuildMs = hordeRenderProfiles.Average(p => p.TerrainRebuildMs),
                TurretMeshMs = hordeRenderProfiles.Average(p => p.TurretMeshMs),
                ZombieMeshMs = hordeRenderProfiles.Average(p => p.ZombieMeshMs),
                OtherGeometryMs = hordeRenderProfiles.Average(p => p.OtherGeometryMs),
                DynamicCopyMs = hordeRenderProfiles.Average(p => p.DynamicCopyMs),
                DynamicSubmitMs = hordeRenderProfiles.Average(p => p.DynamicSubmitMs),
                LiquidMs = hordeRenderProfiles.Average(p => p.LiquidMs),
                TurretVertices = hordeRenderProfiles.Average(p => p.TurretVertices),
                ZombieVertices = hordeRenderProfiles.Average(p => p.ZombieVertices),
                DynamicVertices = hordeRenderProfiles.Average(p => p.DynamicVertices)
            },
            Runtime = Environment.Version.ToString()
        };
        string json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
        if (capturePath is not null) File.WriteAllText(Path.ChangeExtension(capturePath, ".metrics.json"), json);
        Console.WriteLine(json);
    }
    private void PrepareMegaSmoke()
    {
        // This isolated demonstration acquires every weapon block through the
        // real simulation. It never loads, resets, or writes the player's save.
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1, Name = "Mega weapon demonstration",
            Waves = [new WaveDefinition { SpawnInterval = 1000, Groups = [new ZombieGroup { Kind = ZombieKind.Basic }] }]
        });
        session = new GameSession(levels: levels);
        session.StartLevel();
        session.Waves[0].Spawned = session.Waves[0].TotalCount;
        Cell[] positions = [new(7, 16, 23), new(20, 16, 23)];
        GearTier[] tiers = [GearTier.Iron, GearTier.Osmium];
        for (int index = 0; index < positions.Length; index++)
        {
            var mega = new Zombie
            {
                Id = 100 + index, WaveIndex = -1, IsBoss = true, Size = ZombieSize.Mega,
                Kind = ZombieKind.Armored, Tier = tiers[index],
                Attack = LevelCatalog.ZombieAttack(ZombieKind.Armored, tiers[index], ZombieSize.Mega)
            };
            mega.Position = mega.StandAt(positions[index]);
            mega.FaceToward(System.Numerics.Vector3.UnitZ);
            session.Zombies.Add(mega);
        }
        for (int tick = 0; tick < 320 && session.MegaWeapons.Count < 8; tick++) session.Update(.05f);
        if (session.MegaWeapons.Count != 8)
            throw new InvalidOperationException("The Mega demonstration could not acquire its eight real weapon blocks.");
        session.Update(.5f); // Let the last pickup finish before staging either throw.
        for (int index = 0; index < positions.Length; index++)
        {
            var mega = session.Zombies.Single(zombie => zombie.Id == 100 + index);
            // Only the demonstration stages the camera subjects; production
            // movement and every later weapon flight use their normal physics.
            mega.Position = mega.StandAt(positions[index]);
            mega.NextCell = null;
            mega.MoveProgress = 0;
            mega.Cooldown = 0;
            mega.FaceToward(System.Numerics.Vector3.UnitZ);
        }
        if (smokeMegaGuard)
        {
            foreach (var mega in session.Zombies.Where(zombie => zombie.Size == ZombieSize.Mega))
            {
                for (int hit = 0; hit < GameSession.MegaGuardLearningHits; hit++)
                    session.DamageZombie(mega, 1, DamageKind.Pierce, ShotKind.Pellet, -System.Numerics.Vector3.UnitZ);
            }
            QueueMegaGuardSmokeVolley();
            session.Update(.001f);
        }
        else if (smokeGiantDeaths)
        {
            var kilo = new Zombie { Id = 102, WaveIndex = -1, Size = ZombieSize.Kilo };
            kilo.Position = kilo.StandAt(new Cell(15, 16, 23));
            session.Zombies.Add(kilo);
            session.Update(.05f);
            foreach (var giant in session.Zombies.Where(zombie => zombie.IsGiant).ToArray())
                session.DamageZombie(giant, 100_000, DamageKind.Chemical);
            session.Update(.001f);
        }
        else
        {
            session.Deploy(PieceKind.Pawn, new Cell(8, 16, 16));
            session.Deploy(PieceKind.Pawn, new Cell(20, 16, 16));
            session.Deploy(PieceKind.Pawn, new Cell(21, 16, 16));
        }
        selected = null;
        showZones = false;
        paused = !smokeMegaGuard;
    }
    private void QueueMegaGuardSmokeVolley()
    {
        foreach (var mega in session.Zombies.Where(zombie => zombie.Hp > 0 && zombie.Size == ZombieSize.Mega))
            for (int shot = 0; shot < 3; shot++)
            {
                var aim = mega.Position + new System.Numerics.Vector3(.7f + shot * .1f, 1.1f + shot * .05f, 0);
                session.Projectiles.Add(new Projectile { Origin = aim + System.Numerics.Vector3.UnitZ * 6,
                    Target = aim - System.Numerics.Vector3.UnitZ * 3, Kind = ShotKind.Pellet, Damage = 1, Duration = .45f });
            }
    }
    private void PrepareThrownCoverSmoke()
    {
        session = new GameSession();
        var state = session.ExportState();
        state.MegaWeaponFlights = [new SavedMegaWeaponFlight
        {
            OwnerId = 100, Formation = MegaWeaponFormation.Chunk, X = 16, Y = 22, Z = 24,
            VelocityZ = -2, Damage = 12,
            Blocks = Enumerable.Range(0, 4).Select(index => new SavedMegaWeaponPart
            {
                BlockIndex = index, Material = Material.Dirt, Hp = 100,
                OffsetX = index % 2 == 0 ? -.5f : .5f, OffsetY = index / 2 == 0 ? -.5f : .5f
            }).ToList()
        }];
        RestoreSmokeState(state);
        selected = null;
        showZones = false;
        QueueThrownCoverSmokeShot(75);
    }
    private void PrepareTowerPenetrationSmoke()
    {
        session = new GameSession();
        for (int x = 13; x <= 22; x += 3)
        {
            if (!session.Deploy(PieceKind.Rook, new Cell(x, 16, 24)))
                throw new InvalidOperationException("Could not prepare the penetration targets.");
            session.Turrets[^1].Hp = x == 22 ? 30 : 1;
        }
        var state = session.ExportState();
        state.MegaWeaponFlights = [new SavedMegaWeaponFlight
        {
            OwnerId = 100, Formation = MegaWeaponFormation.Chunk, IsDebris = true,
            X = 11, Y = 17.1f, Z = 24.5f, VelocityX = 40, Damage = 12,
            Blocks = [new SavedMegaWeaponPart { BlockIndex = 0, Material = Material.Stone, Hp = 500 }]
        }];
        RestoreSmokeState(state);
        selected = null;
        showZones = false;
    }
    private void RestoreSmokeState(CampaignState state)
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-smoke-{Guid.NewGuid():N}.json");
        try
        {
            var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true, Converters = { new JsonStringEnumConverter() } };
            File.WriteAllText(fixture, JsonSerializer.Serialize(state, options));
            if (!SaveStore.TryLoad(fixture, out var restored, out var error)) throw new InvalidOperationException(error);
            session = restored!;
        }
        finally { if (File.Exists(fixture)) File.Delete(fixture); }
    }
    private void PrepareTerrainImpactSmoke()
    {
        session = new GameSession();
        var state = session.ExportState();
        state.MegaWeaponFlights = [new SavedMegaWeaponFlight
        {
            OwnerId = 100, Formation = MegaWeaponFormation.Chunk,
            X = 16, Y = MathF.BitIncrement(16 + .49999f) + .5f, Z = 24.5f, Damage = 12,
            Blocks = Enumerable.Range(0, 4).Select(index => new SavedMegaWeaponPart
            {
                BlockIndex = index, Material = index % 2 == 0 ? Material.Stone : Material.Wood,
                Hp = index % 2 == 0 ? 311 : 123,
                OffsetX = index % 2 - .5f, OffsetY = index / 2 - .5f
            }).ToList()
        }];
        RestoreSmokeState(state);
        for (int shot = 0; shot < 50; shot++)
            session.Projectiles.Add(new Projectile
            {
                Origin = new System.Numerics.Vector3(28, 27, 24), Target = new System.Numerics.Vector3(29, 27, 24),
                Duration = 10, Kind = ShotKind.Pellet, Damage = 1
            });
        selected = null;
        showZones = false;
    }
    private void QueueThrownCoverSmokeShot(float damage)
    {
        var flight = session.ExportState().MegaWeaponFlights?.FirstOrDefault();
        if (flight is null) return;
        var block = flight.Blocks.OrderBy(block => block.BlockIndex).First();
        const float lead = .0875f;
        var aim = new System.Numerics.Vector3(flight.X + block.OffsetX, flight.Y + block.OffsetY, flight.Z + block.OffsetZ) +
            new System.Numerics.Vector3(flight.VelocityX, flight.VelocityY, flight.VelocityZ) * lead -
            System.Numerics.Vector3.UnitY * (GameSession.MegaWeaponGravity * lead * lead * .5f);
        session.Projectiles.Add(new Projectile { Origin = aim - System.Numerics.Vector3.UnitX * 4,
            Target = aim + System.Numerics.Vector3.UnitX * 4, Kind = ShotKind.Pellet, Damage = damage, Duration = .2f });
    }
    private void PrepareSizeSmoke()
    {
        session.StartLevel();
        ZombieSize[] sizes = [ZombieSize.Milli, ZombieSize.Standard, ZombieSize.Kilo];
        int[] columns = [17, 20, 24];
        for (int i = 0; i < sizes.Length; i++)
        {
            var zombie = new Zombie { Id = -100 - i, WaveIndex = -1, Size = sizes[i],
                Kind = ZombieKind.ArmedArmored, Tier = GearTier.Iron, Attack = 9 };
            zombie.Position = zombie.StandAt(new Cell(columns[i], 16, 23));
            zombie.FaceToward(System.Numerics.Vector3.UnitZ);
            session.Zombies.Add(zombie);
        }
        session.Update(.05f);
        if (session.Zombies.FirstOrDefault(zombie => zombie.Size == ZombieSize.Kilo) is { } kilo)
        {
            var aim = kilo.Position + new System.Numerics.Vector3(.7f, .8f, 0);
            session.Projectiles.Add(new Projectile { Origin = aim + System.Numerics.Vector3.UnitZ * 3,
                Target = aim, Kind = ShotKind.Pellet, Damage = 1, Duration = .1f });
            session.Update(.1f);
        }
        selected = null;
        paused = true;
    }
    private void PrepareMicroSmoke()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1, Name = "Micro flanker demonstration",
            Waves = [new WaveDefinition { SpawnInterval = 1000, Groups = [new ZombieGroup { Kind = ZombieKind.Basic }] }]
        });
        session = new GameSession(levels: levels);
        foreach (int x in new[] { 17, 20, 23 })
            if (!session.Deploy(PieceKind.Pawn, new Cell(x, 16, 21)))
                throw new InvalidOperationException("Could not prepare the Micro demonstration targets.");
        session.StartLevel();
        session.Waves[0].Spawned = session.Waves[0].TotalCount;
        for (int index = 0; index < 4; index++)
        {
            var kind = index % 2 == 0 ? ZombieKind.Basic : ZombieKind.Armored;
            var micro = new Zombie
            {
                Id = 100 + index, WaveIndex = -1, Size = ZombieSize.Micro,
                Kind = kind, Tier = GearTier.Wood,
                Attack = LevelCatalog.ZombieAttack(kind, GearTier.Wood, ZombieSize.Micro)
            };
            micro.Position = micro.StandAt(new Cell(17 + index * 2, 16, 24));
            micro.FaceToward(-System.Numerics.Vector3.UnitZ);
            session.Zombies.Add(micro);
        }
        // Adjacent sizes make the quarter-size silhouette inspectable while the
        // actual combat update controls all flanking, dodging, and dash motion.
        foreach (var (size, x) in new[] { (ZombieSize.Milli, 24), (ZombieSize.Standard, 26) })
        {
            var comparison = new Zombie { Id = 200 + x, WaveIndex = -1, Size = size, Kind = ZombieKind.Basic };
            comparison.Position = comparison.StandAt(new Cell(x, 16, 24));
            session.Zombies.Add(comparison);
        }
        QueueMicroSmokeVolley();
        selected = null;
        showZones = false;
        paused = false;
    }
    private void QueueMicroSmokeVolley()
    {
        foreach (var micro in session.Zombies.Where(zombie => zombie.Size == ZombieSize.Micro && zombie.BodyHp > 0))
        {
            var aim = micro.Position;
            session.Projectiles.Add(new Projectile
            {
                Origin = aim + System.Numerics.Vector3.UnitZ * 4,
                Target = aim - System.Numerics.Vector3.UnitZ * 2,
                TargetId = micro.Id, Kind = ShotKind.Pellet, Damage = .1f, Duration = .7f
            });
        }
    }
    private void PrepareLiquidTurretSmoke()
    {
        session = new GameSession();
        var wetCell = new Cell(18, 16, 21);
        var shellCell = new Cell(20, 16, 21);
        session.Deploy(PieceKind.Bishop, wetCell);
        session.Deploy(PieceKind.Rook, shellCell);
        session.BuyLiquid(LiquidKind.Water);
        session.Liquids.RestoreState(new LiquidState
        {
            Cells = [new LiquidCell(wetCell, 5, 0, 0), new LiquidCell(shellCell, 0, 0, 0, 4, CrustFace.West)],
            Obsidian = [new ObsidianCell(shellCell, 4, CrustFace.West, 20, OppositeUnits: 2, OppositeHp: 10)]
        });
        selected = session.TrySelectTurret(shellCell);
        panel = 6;
        tool = 7;
        pourUnits = 1;
        showZones = false;
        paused = true;
        Aim();
    }

    private void PrepareBucketSmoke()
    {
        session.Economy.Credit(4000);
        session.BuyTool(ToolKind.Shovel);
        foreach (var source in new[] { new Cell(19, 15, 19), new Cell(19, 15, 18) })
        {
            session.Dig(source);
            session.BuyLiquid(LiquidKind.Lava);
            session.PourLiquid(source, LiquidKind.Lava);
        }
        session.Inventory.Add(Material.Dirt);
        session.BuildBarrier(new Cell(23, 16, 23));
        session.Deploy(PieceKind.Rook, new Cell(23, 16, 20));
        session.StartLevel();
        session.Zombies.Add(new Zombie { Id = -90, WaveIndex = -1, CarriesBucket = true,
            Position = new Cell(20, 16, 19).Center, Attack = Balance.BaseAttack });
        session.Zombies.Add(new Zombie { Id = -91, WaveIndex = -1, CarriesBucket = true,
            Position = new Cell(23, 16, 25).Center, Attack = Balance.BaseAttack });
        var kilo = new Zombie { Id = -92, WaveIndex = -1, Size = ZombieSize.Kilo,
            Kind = ZombieKind.Armored, Tier = GearTier.Wood, CarriesBucket = true, Attack = Balance.BaseAttack };
        // Acquire real cover on dry ground first, then demonstrate switching to
        // the buckets at the bank with both blocks still in its inventory.
        kilo.Position = kilo.StandAt(new Cell(18, 16, 24));
        session.Zombies.Add(kilo);
        session.Update(.05f);
        kilo.Position = kilo.StandAt(new Cell(18, 16, 18));
        kilo.NextCell = null;
        kilo.MoveProgress = 0;
    }

    private void PrepareBridgeSmoke()
    {
        for (int i = 0; i < SaleMaterials.Length; i++) session.Inventory.Add(SaleMaterials[i], SaleMaterials[i] == Material.Obsidian ? 13 : i + 1);
        var blockMaterials = SaleMaterials.Where(Balance.IsTerrainMaterial).ToArray();
        var state = session.ExportState();
        for (int i = 0; i < blockMaterials.Length; i++)
            state.TerrainPlacements!.Add(new SavedTerrainPlacement { Cell = new Cell(17 + i, 16, 22), Material = blockMaterials[i] });
        var crustCell = new Cell(24, 16, 22);
        state.Liquids!.Cells.Add(new LiquidCell(crustCell, 0, 0, 0, 2, CrustFace.West));
        state.Liquids.Obsidian.Add(new ObsidianCell(crustCell, 2, CrustFace.West, 10));
        var fallingCrust = crustCell + new Cell(0, 2, 0);
        state.Liquids.Cells.Add(new LiquidCell(fallingCrust, 0, 0, 0, 2, CrustFace.East));
        state.Liquids.Obsidian.Add(new ObsidianCell(fallingCrust, 2, CrustFace.East, 10));
        var fixture = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-bridge-smoke-{Guid.NewGuid():N}.json");
        try
        {
            var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true, Converters = { new JsonStringEnumConverter() } };
            File.WriteAllText(fixture, JsonSerializer.Serialize(state, options));
            if (!SaveStore.TryLoad(fixture, out var restored, out var error)) throw new InvalidOperationException(error);
            session = restored!;
        }
        finally { if (File.Exists(fixture)) File.Delete(fixture); }
        selected = session.Turrets.FirstOrDefault();
        for (int i = 0; i < blockMaterials.Length; i++)
        {
            var carrier = new Zombie { Id = -30 - i, WaveIndex = -1, Kind = ZombieKind.ArmedArmored,
                Tier = (GearTier)i, Position = new Cell(17 + i, 17, 22).Center };
            carrier.FaceToward(System.Numerics.Vector3.UnitZ);
            session.Zombies.Add(carrier);
        }
        panel = 7;
        recoveredMaterial = Material.Iron;
        tool = 10;
    }
    private Vector3 Forward => WorldRenderer.ToXna(cameraControl.Forward);
    private Matrix CameraView => Matrix.CreateLookAt(camera, camera + Forward, WorldRenderer.ToXna(cameraControl.Up));
    private void CenterMouse()
    {
        if (!IsActive || !captured) return;
        Mouse.SetPosition(Window.ClientBounds.Width / 2, Window.ClientBounds.Height / 2);
        previousMouse = Mouse.GetState();
    }
    private void Capture(bool value)
    {
        CommitTemplateField();
        captured = value && IsActive;
        IsMouseVisible = !captured;
        if (captured) { suppressMouseUntilReleased = true; CenterMouse(); }
    }
    private void ReleaseCursorForFocusLoss()
    {
        Capture(false);
        synchronizeInput = true;
        suppressMouseUntilReleased = true;
    }
    private bool Pressed(KeyboardState keys, Keys key) => keys.IsKeyDown(key) && previousKeys.IsKeyUp(key);
    protected override void Update(GameTime gameTime)
    {
        float dt = smoke ? 1f / 60 : (float)gameTime.ElapsedGameTime.TotalSeconds;
        elapsed += dt; noticeTime = Math.Max(0, noticeTime - dt);
        var keys = Keyboard.GetState(); var mouse = Mouse.GetState();
        // Event handling releases immediately; the state check also covers a
        // missed transition. Returning focus never captures or warps the cursor.
        if (!IsActive && (wasActive || captured)) ReleaseCursorForFocusLoss();
        bool resynchronizing = synchronizeInput || IsActive != wasActive;
        if (resynchronizing)
        {
            previousKeys = keys;
            previousMouse = mouse;
            synchronizeInput = false;
        }
        wasActive = IsActive;
        if (suppressMouseUntilReleased && mouse.LeftButton == ButtonState.Released &&
            mouse.RightButton == ButtonState.Released && mouse.MiddleButton == ButtonState.Released)
            suppressMouseUntilReleased = false;
        uiScale = Math.Min(Window.ClientBounds.Width / 1440f, Window.ClientBounds.Height / 900f);
        uiScale = Math.Max(.4f, uiScale); width = Window.ClientBounds.Width / uiScale; height = Window.ClientBounds.Height / uiScale;
        if (IsActive && !smoke && !resynchronizing)
        {
            if (Pressed(keys, Keys.F2)) showDeathToll = !showDeathToll;
            if (templateField.HasValue && panel == 2 && !captured) HandleTemplateFieldInput(keys, mouse);
            else if (panel == 3) HandleJumpEditorInput(keys, mouse);
            else
            {
                if (Pressed(keys, Keys.Tab)) Capture(!captured);
                if (Pressed(keys, Keys.Escape))
                {
                    if (batchSelectMode) { batchSelectMode = false; panel = 1; Capture(false); }
                    else if (panel == 7) panel = 5;
                    else Capture(!captured);
                }
                if (Pressed(keys, Keys.P)) paused = !paused;
                if (Pressed(keys, Keys.F1)) { batchSelectMode = false; panel = 4; Capture(false); }
                if (Pressed(keys, Keys.V)) showZones = !showZones;
                if (Pressed(keys, Keys.G)) { panel = 1; Capture(false); }
                if (Pressed(keys, Keys.B)) ToggleBatchSelection();
                if (batchSelectMode && Pressed(keys, Keys.Home)) batchCornerDepthOverride = false;
                if (Pressed(keys, Keys.U)) { batchSelectMode = false; SelectTarget(); panel = 2; Capture(false); }
                if (Pressed(keys, Keys.F)) { batchSelectMode = false; panel = 8; Capture(false); }
                if (Pressed(keys, Keys.J)) { batchSelectMode = false; SelectTarget(); OpenJumpEditor(); }
                if (panel != 3 && Pressed(keys, Keys.L)) { batchSelectMode = false; panel = 6; Capture(false); }
                if (panel != 3 && Pressed(keys, Keys.Enter)) BeginLevel();
                if (panel != 3 && Pressed(keys, Keys.R) && session.Phase == GamePhase.Defeat) { LoadCheckpoint(); paused = false; }
                for (int i = 0; i < 7; i++) if (panel != 3 && Pressed(keys, (Keys)((int)Keys.D1 + i))) { batchSelectMode = false; tool = i; panel = i >= 4 ? 5 : 0; }
                if (panel != 3 && Pressed(keys, Keys.D8)) { batchSelectMode = false; tool = 7; panel = 6; }
                if (panel != 3 && Pressed(keys, Keys.D9)) { batchSelectMode = false; tool = 8; panel = 6; }
                if (panel != 3 && Pressed(keys, Keys.D0)) { batchSelectMode = false; tool = 9; panel = 6; }
                if (panel != 3 && captured)
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
                    if (batchSelectMode && mouse.ScrollWheelValue != previousMouse.ScrollWheelValue)
                    {
                        if (!batchCornerDepthOverride && batchCorner.HasValue)
                            batchCornerDepth = System.Numerics.Vector3.Distance(cameraControl.Position, batchCorner.Value.Center);
                        batchCornerDepth = Math.Clamp(batchCornerDepth + (mouse.ScrollWheelValue - previousMouse.ScrollWheelValue) / 120f, 1, 64);
                        batchCornerDepthOverride = true;
                        UpdateBatchCorner();
                    }
                    if (!suppressMouseUntilReleased)
                    {
                        if (batchSelectMode)
                        {
                            if (mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released) ChangeBatchTowerSelection(false);
                            if (mouse.RightButton == ButtonState.Pressed && previousMouse.RightButton == ButtonState.Released) ChangeBatchTowerSelection(true);
                            if (mouse.MiddleButton == ButtonState.Pressed && previousMouse.MiddleButton == ButtonState.Released) SelectBatchCorner();
                        }
                        else
                        {
                            if (mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released) RemoveTarget();
                            if (mouse.RightButton == ButtonState.Pressed && previousMouse.RightButton == ButtonState.Released) PlaceDefense();
                            if (mouse.MiddleButton == ButtonState.Pressed && previousMouse.MiddleButton == ButtonState.Released) ApplyTemplateToTarget();
                        }
                    }
                    CenterMouse();
                }
                else if (panel != 3 && !suppressMouseUntilReleased && mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released)
                {
                    var point = new Point((int)(mouse.X / uiScale), (int)(mouse.Y / uiScale));
                    var clicked = buttons.LastOrDefault(b => b.Area.Contains(point)); clicked.Action?.Invoke();
                }
                if (panel != 3 && !batchSelectMode && Pressed(keys, Keys.Delete) && session.CanInteractWithTowers)
                {
                    Aim();
                    var c = selected?.Cell ?? hit;
                    if (c.HasValue) { session.Remove(c.Value); selected = null; SetNotice(session.Message); SaveCheckpoint(); }
                }
            }
        }
        var oldPhase = session.Phase;
        if (smokeMegaGuard && frameCount > 0 && frameCount % 15 == 0) QueueMegaGuardSmokeVolley();
        if (smokePanel == "micro" && frameCount > 0 && frameCount % 20 == 0) QueueMicroSmokeVolley();
        if (smokeThrownCover && frameCount is 15 or 30) QueueThrownCoverSmokeShot(25);
        var queuedBeforeUpdate = session.QueuedUpgradeBatch;
        bool restoringFormation = session.IsAutoRestoringFormation;
        int towersBeforeUpdate = session.Turrets.Count;
        bool graceBeforeUpdate = queuedBeforeUpdate?.GraceRemaining.HasValue ?? false;
        if (!paused && (IsActive || smoke))
        {
            if (smokeHorde)
            {
                long start = Stopwatch.GetTimestamp();
                session.Update(dt);
                hordeUpdateTimes!.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            else session.Update(dt);
        }
        if (queuedBeforeUpdate != null && (session.QueuedUpgradeBatch == null ||
            !graceBeforeUpdate && session.QueuedUpgradeBatch.GraceRemaining.HasValue))
        { SetNotice(session.Message); SaveCheckpoint(); }
        if (restoringFormation && (session.Turrets.Count != towersBeforeUpdate || !session.IsAutoRestoringFormation))
        { SetNotice(session.FormationRestoreStatus); SaveCheckpoint(); }
        if (batchSelection.SelectedIds.Count > 0) batchSelection.Prune(session.Turrets);
        // Frame 30 shows both two-block hands and all four armor slots. Frame
        // 90 shows the stick/chunk arcs; around frame 150 they scatter as debris.
        if (smokePanel == "megas" && frameCount == 60) paused = false;
        if (smokeArmorDemo != null && frameCount == 30)
            session.DamageZombie(smokeArmorDemo, Balance.GearHp(GearTier.Osmium), DamageKind.Pierce, ShotKind.Pellet, -smokeArmorDemo.Facing);
        if (smokePanel == "armor" && frameCount == 55)
        {
            var wood = session.Zombies.First(z => z.Tier == GearTier.Wood);
            session.DamageZombie(wood, 3, DamageKind.Blunt, ShotKind.Lob, -System.Numerics.Vector3.UnitY);
            session.DamageZombie(wood, 4, DamageKind.Pierce, ShotKind.Pellet, wood.Facing);
            session.DamageZombie(wood, 2, DamageKind.Blunt, ShotKind.Melee, -wood.Facing);
        }
        if (smokePanel == "armor" && smokeArmorDemo != null && frameCount == captureFrame - 2)
            session.DamageZombie(smokeArmorDemo, 25, DamageKind.Pierce, ShotKind.Pellet, smokeArmorDemo.Facing);
        if (smokePanel == "falls" && frameCount == 30)
        {
            for (int i = 0; i < 4; i++) session.Remove(new Cell(20, 16, 22));
            for (int y = 16; y <= 18; y++) session.Remove(new Cell(13, y, 18));
            session.StartLevel();
            session.Zombies.Add(new Zombie { Id = -20, WaveIndex = -1, Kind = ZombieKind.Basic,
                Position = new Cell(17, 22, 23).Center, Attack = 1 });
        }
        if (oldPhase == GamePhase.Combat && session.Phase == GamePhase.Building) { SetNotice(session.Message); SaveCheckpoint(); }
        if (oldPhase != GamePhase.Defeat && session.Phase == GamePhase.Defeat)
        {
            batchSelectMode = false;
            if (panel == 3) { panel = 0; jumpPreview = null; }
            Capture(false);
            SetNotice("The core has fallen. Retry the saved preparation with R.");
            if (!smoke && !saveBlocked && !SaveStore.TryClearMergeSelection(savePath, out var error)) SetNotice(error);
        }
        // Keep selection tied to the current view even while menus are open or
        // simulation moves/destroys a tower under the stationary crosshair.
        if (panel != 3) Aim();
        previousKeys = keys; previousMouse = Mouse.GetState();
        base.Update(gameTime);
    }
    private void Aim()
    {
        var aim = WorldPicker.Pick(session, cameraControl.Position, cameraControl.Forward,
            includeLiquids: !batchSelectMode, includeVoidFloor: !batchSelectMode);
        hit = aim?.Hit;
        placement = aim?.Placement;
        highlight = aim?.Highlight;
        hitLiquid = aim?.IsLiquid ?? false;
        if (batchSelectMode)
        {
            obsidianPlacementFace = null;
            UpdateBatchCorner();
            SelectTarget();
            return;
        }
        if (hitLiquid && hit is { } liquidCell && (tool is >= 0 and <= 5 or 10))
            placement = highlight = liquidCell;
        else if (tool is >= 7 and <= 9 && hit is { } towerCell && session.TrySelectTurret(towerCell) != null)
            placement = highlight = towerCell;
        obsidianPlacementFace = null;
        if (tool == 10 && recoveredMaterial == Material.Obsidian)
        {
            if (!hitLiquid && hit is { } crustCell && session.World.GetObsidian(crustCell) is { Units: < LiquidSystem.UnitsPerBlock })
                placement = highlight = crustCell;
            if (placement is { } target)
            {
                if (session.World.GetObsidian(target) is { } existing) obsidianPlacementFace = aim?.ObsidianFace ?? existing.Face;
                else if (hit is { } support && (support - target).Manhattan == 1)
                    obsidianPlacementFace = LiquidSystem.FaceToward(target, support);
                else if (hitLiquid)
                {
                    CrustFace[] faces = [CrustFace.Bottom, CrustFace.West, CrustFace.East, CrustFace.North, CrustFace.South, CrustFace.Top];
                    obsidianPlacementFace = faces.FirstOrDefault(face => session.CanBuildObsidian(target, face), CrustFace.Bottom);
                }
                else if (target.Y == 0 && hit == target && session.World.GetMaterial(target) == Material.Air)
                    obsidianPlacementFace = CrustFace.Bottom;
            }
        }
        SelectTarget();
    }
    private void SelectTarget()
    {
        const float selectionRange = 10;
        selected = !hitLiquid && hit.HasValue && session.TrySelectTurret(hit.Value) is { } turret &&
            System.Numerics.Vector3.DistanceSquared(cameraControl.Position, turret.Cell.Center) <= selectionRange * selectionRange
            ? turret : null;
    }
    private void RemoveTarget()
    {
        if (!hit.HasValue) { SetNotice("Aim at a tower or defensive block to remove it."); return; }
        if (tool == 6 && !hitLiquid && session.TrySelectTurret(hit.Value) is null &&
            (Balance.IsTerrainMaterial(session.World.GetMaterial(hit.Value)) || session.World.GetMaterial(hit.Value) == Material.Obsidian))
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
            if (tool is >= 7 and <= 9) session.PourLiquid(placement.Value, (LiquidKind)(tool - 7), pourUnits);
            else if (tool == 10 && recoveredMaterial == Material.Obsidian)
            {
                if (!obsidianPlacementFace.HasValue) { SetNotice("Aim at a supporting block face or an existing crust."); return; }
                session.BuildObsidian(placement.Value, obsidianPlacementFace.Value);
            }
            else if (tool is 4 or 5 or 10) session.BuildBarrier(placement.Value, BlockMaterial);
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
    private void SelectBattleMode(int mode)
    {
        bool changed = mode == 2 ? session.SetAllInOne(true) : session.SetMergeFiveLevels(mode == 1);
        if (!changed) { SetNotice(session.Message); return; }
        SetNotice(session.AllInOne
            ? $"Levels {session.LevelNumber}-{session.EndLevelNumber} / All in one / 5x spawn rate / 75 currency per surviving tower."
            : session.MergeFiveLevels
            ? $"Levels {session.LevelNumber}-{session.EndLevelNumber} merged / no preparation breaks / 25 currency per surviving tower."
            : "Single-level defense selected / 2 currency per surviving tower.");
        SaveCheckpoint();
    }
    private string LevelRange => session.LevelSpan == 1 ? $"{session.LevelNumber:00}" : $"{session.LevelNumber:00}-{session.EndLevelNumber:00}";
    private void Pull(int count)
    {
        if (session.Phase != GamePhase.Building) { SetNotice("Pull components during preparation between levels."); return; }
        for (int i = 0; i < count; i++) if (session.Economy.Pull(session.Turrets) is null) break;
        SetNotice(session.Economy.LastMessage); SaveCheckpoint();
    }
    private int[] BatchTargets() => batchAllTurrets
        ? session.Turrets.Where(turret => turret.Hp > 0).Select(turret => turret.Id).ToArray()
        : batchSelection.GetTargets(session.Turrets).Select(turret => turret.Id).ToArray();

    private void ToggleBatchSelection()
    {
        if (!session.CanInteractWithTowers) { SetNotice("Retry the level before selecting towers for upgrades."); return; }
        batchSelectMode = !batchSelectMode;
        batchAllTurrets = false;
        panel = 1;
        if (batchSelectMode)
        {
            batchCornerDepthOverride = false;
            Capture(true);
            SetNotice("Batch select: MIDDLE two 3D corners / RIGHT add a tower / LEFT deselect / wheel corner depth / B finish.");
        }
        else
        {
            Capture(false);
            SetNotice($"{BatchTargets().Length} towers selected. Spend or queue upgrade points in the armory.");
        }
    }
    private void UpdateBatchCorner()
    {
        if (batchCornerDepthOverride)
            batchCorner = TowerSelectionCursor.Pick(cameraControl.Position, cameraControl.Forward, batchCornerDepth);
        else
            batchCorner = hit.HasValue && session.TrySelectTurret(hit.Value) != null ? hit
                : placement ?? hit ?? TowerSelectionCursor.Pick(cameraControl.Position, cameraControl.Forward, batchCornerDepth);
    }
    private void SelectBatchCorner()
    {
        if (!batchCorner.HasValue) { SetNotice("Aim inside the battlefield or scroll to adjust the 3D corner depth."); return; }
        bool completing = batchSelection.IsChoosingSecondCorner;
        batchSelection.SelectCorner(batchCorner.Value, session.Turrets);
        batchAllTurrets = false;
        SetNotice(completing ? $"Box added: {BatchTargets().Length} towers selected. RIGHT adds / LEFT deselects / B finish."
            : $"First corner: {batchCorner.Value}. Middle-click the opposite 3D corner.");
    }
    private void ChangeBatchTowerSelection(bool add)
    {
        if (hit is not { } cell || session.TrySelectTurret(cell) is not { } tower)
        { SetNotice(add ? "Aim directly at a living tower to add it to the batch." : "Aim directly at a selected tower to deselect it."); return; }
        if (add) batchSelection.Add(tower); else batchSelection.Remove(tower);
        batchAllTurrets = false;
        SetNotice($"{tower.Kind} #{tower.Id} {(add ? "selected" : "deselected")} / {BatchTargets().Length} towers in the batch.");
    }
    private void ClearBatchSelection()
    {
        batchSelection.Clear();
        batchAllTurrets = false;
        SetNotice("Batch selection cleared. Choose two corners or add individual towers.");
    }

    private void BuyUpgradeBatch(bool queue)
    {
        bool bought = queue
            ? session.QueueUpgradeBatch(BatchTargets(), upgradeTemplate)
            : session.SpendUpgradeBatchNow(BatchTargets(), upgradeTemplate);
        SetNotice(session.Message);
        if (bought) SaveCheckpoint();
    }

    private void CancelUpgradeBatch()
    {
        session.CancelUpgradeBatch();
        SetNotice(session.Message);
        SaveCheckpoint();
    }
    private void ApplyTemplateToTarget()
    {
        if (!hit.HasValue || session.TrySelectTurret(hit.Value) is not { } target)
        { SetNotice("Aim at a tower and middle-click to spend the template's points. U edits the template."); return; }
        SelectTarget();
        session.ApplyUpgradeTemplate(target.Cell, upgradeTemplate);
        SetNotice(session.Message); SaveCheckpoint();
    }
    private int TemplatePoints(UpgradeKind kind) => upgradeTemplate.Points.GetValueOrDefault(kind);
    private int TemplateMaximum(UpgradeKind kind) => 2;
    private int TemplateApplicable(UpgradeKind kind)
    {
        var target = selected ?? new Turret { Kind = PieceKind.Pawn };
        if (kind == UpgradeKind.Jump && upgradeTemplate.UseJumpPattern)
        {
            if (session.Economy.GetJumpPatternPointCost(target, upgradeTemplate.JumpPattern) is not > 0) return 0;
        }
        return session.Economy.GetUpgradePurchase(target, kind,
            Math.Min(TemplatePoints(kind), session.Economy.GetProgress(kind).Available)).Cost;
    }
    private void SetTemplatePoints(UpgradeKind kind, long amount)
    {
        upgradeTemplate.Points[kind] = (int)Math.Clamp(amount, 0, TemplateMaximum(kind));
        SaveTemplate();
    }
    private void BeginTemplateField(UpgradeKind kind)
    {
        CommitTemplateField();
        templateField = kind;
        templateFieldText = TemplatePoints(kind).ToString(System.Globalization.CultureInfo.InvariantCulture);
        templateFieldSelected = true;
    }
    private void CommitTemplateField()
    {
        if (templateField is not { } kind) return;
        templateField = null;
        if (templateFieldText == TemplatePoints(kind).ToString(System.Globalization.CultureInfo.InvariantCulture)) return;
        long.TryParse(templateFieldText, out long amount);
        SetTemplatePoints(kind, amount);
    }
    private void HandleTemplateFieldInput(KeyboardState keys, MouseState mouse)
    {
        if (Pressed(keys, Keys.Escape)) { templateField = null; return; }
        if (Pressed(keys, Keys.Enter)) { CommitTemplateField(); return; }
        if (Pressed(keys, Keys.Tab))
        {
            int direction = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift) ? -1 : 1;
            var kinds = Enum.GetValues<UpgradeKind>();
            var next = kinds[(Array.IndexOf(kinds, templateField!.Value) + direction + kinds.Length) % kinds.Length];
            BeginTemplateField(next);
            return;
        }
        if ((keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl)) && Pressed(keys, Keys.A))
            templateFieldSelected = true;
        if (Pressed(keys, Keys.Delete)) { templateFieldText = ""; templateFieldSelected = false; }
        if (!suppressMouseUntilReleased && mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released)
        {
            var point = new Point((int)(mouse.X / uiScale), (int)(mouse.Y / uiScale));
            if (templateFieldBounds.Contains(point)) { templateFieldSelected = true; return; }
            CommitTemplateField();
            buttons.LastOrDefault(b => b.Area.Contains(point)).Action?.Invoke();
        }
    }
    private void ToggleTemplatePattern()
    {
        upgradeTemplate.UseJumpPattern = !upgradeTemplate.UseJumpPattern;
        SaveTemplate();
    }
    private void BuyTool(ToolKind kind)
    {
        session.BuyTool(kind); SetNotice(session.Message); SaveCheckpoint();
    }
    private void BuyLiquid(LiquidKind kind)
    {
        session.BuyLiquid(kind); tool = 7 + (int)kind; SetNotice(session.Message); SaveCheckpoint();
    }
    private void SellBlock(Material material)
    {
        session.SellBlock(material); SetNotice(session.Message); SaveCheckpoint();
    }
    private void SelectRecoveredMaterial(Material material)
    {
        recoveredMaterial = material;
        tool = 10;
        SetNotice(material == Material.Obsidian
            ? "Obsidian selected / 5 HP per layer. Right-click a support face or crust to add one layer."
            : $"{material} selected / {Balance.BlockHp(material):N0} HP. Press Tab, then right-click to place a recovered block.");
    }
    private void OpenJumpEditor()
    {
        if (!session.CanInteractWithTowers) { SetNotice("Retry the level before editing jump patterns."); return; }
        draftPattern.Clear(); draftPattern.AddRange(upgradeTemplate.JumpPattern);
        jumpEditor.SetLayer(0, JumpEditorRadius()); jumpPreview = null; panel = 3; Capture(false);
    }
    private int JumpEditorRadius() => (int)Math.Clamp(Math.Max((long)session.Economy.GetAllocationCap(UpgradeKind.Jump),
        JumpRules.FormationRadius(draftPattern)), 2, 31);

    private Rectangle JumpSceneBounds => new(20, 112, Math.Max(1, (int)width - 350), Math.Max(1, (int)height - 248));

    private void HandleJumpEditorInput(KeyboardState keys, MouseState mouse)
    {
        if (Pressed(keys, Keys.Escape)) { panel = 2; jumpPreview = null; return; }
        if (Pressed(keys, Keys.PageUp)) jumpEditor.SetLayer(jumpEditor.Layer + 1, JumpEditorRadius());
        if (Pressed(keys, Keys.PageDown)) jumpEditor.SetLayer(jumpEditor.Layer - 1, JumpEditorRadius());
        if (Pressed(keys, Keys.P)) paused = !paused;
        int wheel = mouse.ScrollWheelValue - previousMouse.ScrollWheelValue;
        if (wheel != 0) jumpEditor.ZoomBy(Math.Sign(wheel) * Math.Max(1, Math.Abs(wheel) / 120));
        if (mouse.MiddleButton == ButtonState.Pressed && previousMouse.MiddleButton == ButtonState.Pressed)
            jumpEditor.Rotate(mouse.X - previousMouse.X, mouse.Y - previousMouse.Y);

        var point = new Point((int)(mouse.X / uiScale), (int)(mouse.Y / uiScale));
        if (!JumpSceneBounds.Contains(point)) { jumpPreview = null; }
        else
        {
            var (origin, direction) = jumpView.Ray(jumpEditor, mouse.X, mouse.Y);
            jumpPreview = jumpEditor.PickExisting(draftPattern, origin, direction) is null
                ? jumpEditor.PickPlacement(origin, direction, JumpEditorRadius()) : null;
        }
        if (suppressMouseUntilReleased) return;
        bool left = mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released;
        bool right = mouse.RightButton == ButtonState.Pressed && previousMouse.RightButton == ButtonState.Released;
        if (!left && !right) return;
        if (left)
        {
            var clicked = buttons.LastOrDefault(button => button.Area.Contains(point));
            if (clicked.Action != null) { clicked.Action(); return; }
        }
        if (!JumpSceneBounds.Contains(point)) return;
        var ray = jumpView.Ray(jumpEditor, mouse.X, mouse.Y);
        var existing = jumpEditor.PickExisting(draftPattern, ray.Origin, ray.Direction);
        if (existing is { } selectedOffset)
        {
            draftPattern.Remove(selectedOffset);
            jumpEditor.SetLayer(jumpEditor.Layer, JumpEditorRadius());
            jumpPreview = null;
        }
        else if (left && jumpEditor.PickPlacement(ray.Origin, ray.Direction, JumpEditorRadius()) is { } offset)
            jumpEditor.TryAdd(draftPattern, offset, JumpEditorRadius());
    }
    private void SetNotice(string text) { notice = text; noticeTime = 7; }
    private void SaveCheckpoint()
    {
        if (smoke || saveBlocked || session.Phase != GamePhase.Building) return;
        if (!SaveStore.TrySave(session, savePath, out var error)) SetNotice(error);
    }
    private void SaveTemplate()
    {
        if (session.Phase == GamePhase.Building) { SaveCheckpoint(); return; }
        if (smoke || saveBlocked) return;
        if (!SaveStore.TrySaveUpgradeTemplate(upgradeTemplate, savePath, out var error)) SetNotice(error);
    }
    private void SaveFormation()
    {
        if (!session.SaveFormation()) { SetNotice(session.Message); return; }
        SetNotice(session.Message);
        if (session.Phase == GamePhase.Building) { SaveCheckpoint(); return; }
        if (smoke || saveBlocked) return;
        if (!SaveStore.TrySaveFormationTemplate(session.SavedFormation!, savePath, out var error)) SetNotice(error);
    }
    private void ToggleFormationRestore()
    {
        if (session.IsAutoRestoringFormation) session.CancelAutoRestoreFormation();
        else session.StartAutoRestoreFormation(cell => !cameraControl.Intersects(cell));
        SetNotice(session.Message);
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
        batchSelection.Clear();
        batchSelectMode = false;
        batchCornerDepthOverride = false;
        draftPattern.Clear();
        if (panel == 3) panel = 2;
    }
    protected override void Draw(GameTime gameTime)
    {
        long drawCpuStart = smokeHorde ? Stopwatch.GetTimestamp() : 0;
        long overlayFrame = Stopwatch.GetTimestamp();
        bool overlayFrameActive = (IsActive || smoke) && !paused && panel != 3;
        // Deterministic presentation-only smoke: exercise both transitions even
        // when the machine running the test cannot sustain the recovery FPS.
        double frameSeconds = smokePerformanceRecovery ? (frameCount < 90 ? 1d / 30 : 1d / 60) :
            previousOverlayFrame == 0 ? 0 : Stopwatch.GetElapsedTime(previousOverlayFrame, overlayFrame).TotalSeconds;
        overlayPerformance.ObserveFrame(frameSeconds,
            session.Phase == GamePhase.Combat, overlayFrameActive && previousOverlayFrameActive);
        previousOverlayFrame = overlayFrame;
        previousOverlayFrameActive = overlayFrameActive;
        session.Feedback.Enabled = !overlayPerformance.ReducedDetail;
        if (overlayPerformance.ReducedDetail) reducedOverlayFrames++;
        GraphicsDevice.Clear(Background);
        if (panel == 3) jumpView.Draw(jumpEditor, draftPattern, JumpEditorRadius(), jumpPreview);
        else
        {
            var view = CameraView;
            var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(65), GraphicsDevice.Viewport.AspectRatio, .08f, 150);
            bool liquidTool = tool is >= 7 and <= 9;
            bool obsidianTool = tool == 10 && recoveredMaterial == Material.Obsidian;
            bool validPlacement = tool != 6 && placement is { } p &&
                (obsidianTool ? obsidianPlacementFace.HasValue && session.CanBuildObsidian(p, obsidianPlacementFace.Value) :
                liquidTool ? session.CanPourLiquid(p, (LiquidKind)(tool - 7), pourUnits) :
                tool is 4 or 5 or 10 ? session.CanBuildBarrier(p, BlockMaterial) : session.CanDeploy((PieceKind)tool, p)) &&
                !cameraControl.Intersects(p);
            renderer.Draw(session, view, projection, highlight, batchSelectMode || tool == 6 ? null : placement, validPlacement,
                batchSelectMode ? null : selected, showZones, elapsed,
                batchAllTurrets ? null : batchSelection, batchCorner, batchSelectMode, reducedDetail: overlayPerformance.ReducedDetail);
        }
        long hudCpuStart = smokeHorde ? Stopwatch.GetTimestamp() : 0;
        buttons.Clear();
        batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp, null, null, null, Matrix.CreateScale(uiScale));
        if (panel == 3) DrawJumpEditorHud();
        else
        {
            var view = CameraView;
            var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(65), GraphicsDevice.Viewport.AspectRatio, .08f, 150);
            if (!overlayPerformance.ReducedDetail)
                CombatOverlay.Draw(session, batch, pixel, font, GraphicsDevice.Viewport, view, projection, uiScale);
            DrawHud();
            if (showDeathToll) DrawDeathTollOverlay();
        }
        batch.End();
        if (smokeHorde)
        {
            long drawCpuEnd = Stopwatch.GetTimestamp();
            hordeDrawTimes.Add(Stopwatch.GetElapsedTime(drawCpuStart, drawCpuEnd).TotalMilliseconds);
            hordeHudTimes.Add(Stopwatch.GetElapsedTime(hudCpuStart, drawCpuEnd).TotalMilliseconds);
        }
        frameCount++;
        if (smokeHorde)
        {
            hordePeakProjectiles = Math.Max(hordePeakProjectiles, session.Projectiles.Count);
            if (profileSmoke && frameCount > 30) hordeRenderProfiles.Add(renderer.LastProfile);
            if (frameCount == 30)
            {
                hordeMeasuredUpdateStart = hordeUpdateTimes!.Count;
                session.Profile?.Reset();
            }
            long now = Stopwatch.GetTimestamp();
            if (previousHordeFrame != 0) hordeFrameTimes!.Add(Stopwatch.GetElapsedTime(previousHordeFrame, now).TotalMilliseconds);
            previousHordeFrame = now;
        }
        if (smoke && frameCount >= captureFrame)
        {
            if (capturePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                var colors = new Color[GraphicsDevice.PresentationParameters.BackBufferWidth * GraphicsDevice.PresentationParameters.BackBufferHeight];
                GraphicsDevice.GetBackBufferData(colors);
                using var texture = new Texture2D(GraphicsDevice, GraphicsDevice.PresentationParameters.BackBufferWidth, GraphicsDevice.PresentationParameters.BackBufferHeight);
                texture.SetData(colors); using var stream = File.Create(capturePath); texture.SaveAsPng(stream, texture.Width, texture.Height);
            }
            if (smokeHorde) ReportHordeSmoke();
            Exit();
        }
        base.Draw(gameTime);
    }
    private void Fill(float x, float y, float w, float h, Color color) => batch.Draw(pixel, new Rectangle((int)x, (int)y, (int)w, (int)h), color);
    private void Text(string text, float x, float y, float size = 18, Color? color = null) => font.Draw(batch, text, new Vector2(x, y), color ?? TextColor, size);
    private void Rule(float x, float y, float w) => Fill(x, y, w, 1, new Color(49, 64, 76));
    private void DrawDeathTollOverlay()
    {
        var toll = session.DeathToll;
        float x = Math.Max(16, (width - 744) / 2), y = 103, w = Math.Min(744, width - 32);
        Fill(x, y, w, 252, new Color(17, 28, 36, 242));
        Fill(x, y, w, 3, Teal);
        Text("DEATH TOLL / F2", x + 18, y + 12, 18, Teal);
        Text(session.DeathTollStartedFromCheckpoint ? "THIS SESSION / FROM CHECKPOINT" : "THIS SESSION / NEW GAME",
            x + 320, y + 16, 12, Muted);
        Rule(x + 18, y + 43, w - 36);
        Text($"PHYSICAL SPAWNS  {toll.PhysicalSpawns:N0}", x + 18, y + 53, 14);
        Text($"FIELD DEATHS  {toll.FieldDeaths:N0}", x + 370, y + 53, 14);
        Text($"QUEUE DEATHS BEFORE SPAWN  {toll.PreSpawnQueueDeaths:N0}", x + 18, y + 77, 14);
        Text($"TOTAL DEATHS  {toll.TotalDeaths:N0}", x + 370, y + 77, 14, Red);
        Text($"PROCESSED QUEUE SLOTS  {toll.ProcessedQueuedSlots:N0}", x + 18, y + 101, 14);
        Text($"DEAD SLOTS PROCESSED  {toll.DeadScheduledSlotsProcessed:N0}", x + 370, y + 101, 14);
        Text($"UNPROCESSED QUEUE ENTRIES (INCLUDING DEAD)  {session.UnprocessedQueueEntries:N0}", x + 18, y + 125, 14);
        Text($"QUEUED PROJECTILE HITS  {toll.QueuedProjectileHits:N0}", x + 18, y + 149, 14);
        Text($"QUEUED HP + ARMOR DAMAGE  {toll.QueuedDamage:N0}", x + 370, y + 149, 14);
        Text($"BOUNDARY IMPACTS WITHOUT QUEUED DAMAGE  {toll.BoundaryImpactsWithoutQueuedDamage:N0}", x + 18, y + 173, 14);
        Rule(x + 18, y + 201, w - 36);
        Text($"LAST 60 SIM S  /  QUEUE {toll.QueueProcessingPerSecond:N1}/s  /  KILLS {toll.KillsPerSecond:N1}/s",
            x + 18, y + 210, 14, Gold);
        Text($"{toll.CombatSeconds:N1} combat s elapsed; first window uses elapsed time", x + 18, y + 232, 11, Muted);
    }
    private void Button(string label, float x, float y, float w, float h, Action action, bool active = false, bool enabled = true, Color? accent = null, float textSize = 18)
    {
        var mouse = Mouse.GetState(); var bounds = new Rectangle((int)x, (int)y, (int)w, (int)h);
        bool hover = !captured && bounds.Contains((int)(mouse.X / uiScale), (int)(mouse.Y / uiScale));
        Fill(x, y, w, h, active ? (accent ?? Teal) : hover && enabled ? new Color(39, 57, 70) : Raised);
        if (active) Fill(x, y, 3, h, Color.White * .8f);
        Text(label, x + 14, y + (h - textSize - 4) / 2, textSize, active ? Background : enabled ? TextColor : Muted * .55f);
        if (enabled) buttons.Add((bounds, action));
    }
    private void DrawHud()
    {
        Fill(0, 0, width, 92, Background); Fill(0, 91, width, 1, new Color(40, 56, 65));
        Fill(28, 25, 4, 44, Teal); Text("CHESS", 44, 12, 30); Text("vs ZOMBIES", 45, 48, 17, Muted);
        float divider = 265; Fill(divider, 22, 1, 48, new Color(48, 63, 73));
        Text($"{(session.LevelSpan == 1 ? "LEVEL" : "LEVELS")} {LevelRange}", divider + 25, 19, 21); Text(session.Phase == GamePhase.Building ? "PREPARATION" : session.Phase == GamePhase.Combat ? "DEFENSE IN PROGRESS" : "CORE LOST", divider + 25, 50, 13, session.Phase == GamePhase.Defeat ? Red : Teal);
        Text("CORE INTEGRITY", width * .43f, 19, 13, Muted); Text($"{session.CoreHp:0} / {Balance.CoreHp}", width * .43f + 137, 17, 17, Teal);
        Fill(width * .43f, 49, 235, 6, new Color(44, 59, 66)); Fill(width * .43f, 49, 235 * session.CoreHp / Balance.CoreHp, 6, Teal);
        Text("CURRENCY", width - 320, 19, 13, Muted); Text(session.Economy.Currency.ToString("N0"), width - 320, 39, 29, Gold);
        Text("FIELD", width - 143, 19, 13, Muted); Text($"{session.Turrets.Count:00} pieces", width - 143, 43, 21);

        // Crosshair always marks the true camera ray, including when menus release the pointer.
        float cx = width / 2, cy = height / 2; var crossColor = placement.HasValue ? Teal : TextColor;
        Fill(cx - 11, cy - 1, 7, 2, crossColor); Fill(cx + 4, cy - 1, 7, 2, crossColor); Fill(cx - 1, cy - 11, 2, 7, crossColor); Fill(cx - 1, cy + 4, 2, 7, crossColor); Fill(cx - 1, cy - 1, 2, 2, crossColor);
        if (batchSelectMode)
        {
            Fill(width / 2 - 215, 102, 430, 53, Panel);
            Text($"BATCH SELECT / {batchSelection.SelectedIds.Count} towers / B finish", width / 2 - 195, 108, 17, Teal);
            Text(batchCorner is { } corner ? $"{(batchSelection.IsChoosingSecondCorner ? "Second" : "First")} corner {corner} / {(batchCornerDepthOverride ? $"depth {batchCornerDepth:0.0}" : "aim surface")} / wheel depth"
                : "Corner outside the field / wheel depth / Home aim surface", width / 2 - 195, 135, 12, Gold);
        }
        bool turretEnvironmentShown = false;
        if (hit.HasValue)
        {
            var barrier = session.World.GetBarrier(hit.Value);
            var material = session.World.GetMaterial(hit.Value);
            var label = barrier is not null
                ? $"{barrier.Content.ToString().ToUpperInvariant()}  {barrier.Hp:0}/{Balance.BlockHp(barrier.Content)} HP"
                : Balance.IsTerrainMaterial(material)
                    ? $"{material.ToString().ToUpperInvariant()}  {session.World.GetTerrainHp(hit.Value):0}/{Balance.BlockHp(material)} HP"
                    : $"{(tool == 6 ? "EXCAVATE" : "TARGET")}  {hit.Value}";
            if (hitLiquid)
                label = string.Join("  /  ", Enum.GetValues<LiquidKind>().Where(kind => session.Liquids.GetUnits(hit.Value, kind) > 0).Select(kind => $"{kind.ToString().ToUpperInvariant()} {session.Liquids.GetUnits(hit.Value, kind)}/10")) +
                    (placement == hit ? $"  /  DISPLACE AT {hit.Value}" : placement is { } above ? $"  /  ABOVE {above}" : "  /  TOP OF GRID");
            else if (session.TrySelectTurret(hit.Value) is { } aimedTurret)
            {
                label = $"{aimedTurret.Kind.ToString().ToUpperInvariant()} #{aimedTurret.Id}  /  {aimedTurret.Hp:0}/{session.Economy.GetMaxHp(aimedTurret)} HP";
                int liquidUnits = session.Liquids.GetTotalUnits(aimedTurret.Cell);
                var shell = session.World.GetObsidian(aimedTurret.Cell);
                if (liquidUnits > 0 || shell != null)
                {
                    var status = liquidUnits > 0 ? $"WET {liquidUnits}/5 UNITS" : "";
                    if (shell != null)
                        status += $"{(status.Length > 0 ? "  /  " : "")}OBSIDIAN SHELL {shell.Hp:0} HP  /  {(session.Phase == GamePhase.Combat ? "CLEARING SHELL" : "SHOOTING BLOCKED")}";
                    Text(status, cx - font.Measure(status, 12) / 2, cy + 43, 12, shell != null ? Gold : Teal);
                    turretEnvironmentShown = true;
                }
            }
            else if (session.World.GetObsidian(hit.Value) is { } crust)
                label = $"OBSIDIAN {crust.Units}/10  /  {crust.Hp:0}/{crust.Units * LiquidSystem.ObsidianHpPerUnit:0} HP";
            Text(label, cx - font.Measure(label, 13) / 2, cy + 21, 13, crossColor);
        }
        if (tool == 10)
        {
            var materialLabel = recoveredMaterial == Material.Obsidian
                ? $"PLACE OBSIDIAN  /  {session.Inventory.ObsidianLayers:N0} LAYERS  /  1 LAYER PER CLICK"
                : $"PLACE {recoveredMaterial.ToString().ToUpperInvariant()}  /  {session.Inventory.GetCount(recoveredMaterial):N0} IN BAG  /  {Balance.BlockHp(recoveredMaterial):N0} HP";
            Text(materialLabel, cx - font.Measure(materialLabel, 12) / 2, cy + (turretEnvironmentShown ? 63 : 43), 12, Gold);
        }
        float left = 28;
        Text("SECTOR  /  32 x 32 x 32", left, 116, 14, Muted);
        Text(session.LevelSpan == 1 ? session.Level.Name : $"Levels {LevelRange} / {(session.AllInOne ? "All in one" : "merged defense")}", left, 139, 25);
        if (session.Phase == GamePhase.Combat)
        {
            int started = session.Waves.Count(w => w.Started), total = session.Waves.Sum(w => w.TotalCount), spawned = session.Waves.Sum(w => w.Spawned);
            Text($"WAVE {started}/{session.Waves.Count}     {session.Zombies.Count} ACTIVE     {spawned}/{total} SPAWNED", left, 180, 15, Teal);
            int resupplying = session.Zombies.Count(session.IsZombieRetreating);
            if (resupplying > 0) Text($"{resupplying} returning for supplies", left, 197, 12, Gold);
            float y = 210;
            if (overlayPerformance.ReducedDetail)
            {
                double hp = 0, maximum = 0;
                foreach (var zombie in session.Zombies)
                {
                    if (zombie.BodyHp <= 0) continue;
                    hp += zombie.Hp;
                    maximum += zombie.MaxHp;
                }
                Text("ACTIVE ENEMY HP + ARMOR", left, y, 12, Muted);
                Text($"{hp:N0} / {maximum:N0}", left, y + 21, 16, Red);
                Fill(left, y + 47, 220, 7, Raised);
                Fill(left, y + 47, 220 * (float)Math.Clamp(hp / Math.Max(1, maximum), 0, 1), 7, Red);
                Text("PERFORMANCE MODE", left, y + 70, 12, Gold);
                Text("Damage indicators disabled", left, y + 90, 12, Muted);
            }
            else foreach (var wave in session.Waves)
            {
                string source = session.AllInOne ? $"L{LevelRange}" : $"L{session.GetSourceLevelNumber(wave.Index):00}";
                Text($"{wave.Index + 1:00}  {source}  {(wave.Started ? "HP REMAINING" : "WAITING")}", left, y, 12, Muted);
                float remaining = wave.TotalHp > 0 ? Math.Clamp(1 - wave.DamageTaken / wave.TotalHp, 0, 1) : 0;
                Fill(left, y + 23, 220, 5, Raised); Fill(left, y + 23, 220 * remaining, 5, wave.Started ? Red : Muted * .4f);
                Fill(left + 110, y + 21, 1, 9, TextColor); y += 47;
            }
        }
        else
        {
            Text("Build. Equip. Hold the center.", left, 180, 16, Muted);
            Text($"Next {LevelRange}: {session.PreparedWaves.Sum(w => w.SpawnCount) + session.PendingReinforcementCount} zombies / {session.PreparedWaves.Count} wave(s)", left, 206, 15, Muted);
            Text($"HP budget {session.PreparedWaves.Sum(w => w.HpBudget) + session.PendingReinforcementBudget:N0} / boss reserve {session.PreparedWaves.Sum(w => w.BossBudget) + session.PendingBossReinforcementBudget:N0}", left, 232, 13, Muted);
            Text($"Victory / {session.SurvivorRewardPerTower} currency per surviving tower{(session.AllInOne ? " / 7.5x bonus" : "")}", left, 254, 13, Gold);
            if (session.AllInOne)
                Text("5x spawn rate / final-wave equipment from the start", left, 276, 13, Gold);
            if (session.PendingReinforcementCount > 0)
                Text($"Includes {session.PendingReinforcementCount} returning survivors", left, session.AllInOne ? 298 : 276, 13, Gold);
        }
        float px = width - 340, py = 112;
        Fill(px, py, 312, height - 248, Panel);
        Fill(px, py, 312, 3, Teal);
        Text("COMMAND", px + 20, py + 19, 13, Muted); Text(captured ? "TAB TO INTERACT" : "CURSOR RELEASED", px + 117, py + 19, 12, Teal);
        Button("Deploy", px + 16, py + 51, 88, 36, () => { batchSelectMode = false; panel = 0; }, panel is 0 or 5 or 6 or 7 or 8);
        Button("Armory", px + 111, py + 51, 88, 36, () => panel = 1, panel == 1);
        Button("Template", px + 206, py + 51, 90, 36, () => { batchSelectMode = false; panel = 2; }, panel is 2 or 3);
        if (panel == 0) DrawDeployPanel(px, py + 106);
        else if (panel == 1) DrawGachaPanel(px, py + 106);
        else if (panel == 2) DrawUpgradePanel(px, py + 106);
        else if (panel == 5) DrawTerrainPanel(px, py + 106);
        else if (panel == 6) DrawLiquidPanel(px, py + 106);
        else if (panel == 7) DrawMaterialsPanel(px, py + 106);
        else if (panel == 8) DrawFormationPanel(px, py + 106);
        else DrawHelpPanel(px, py + 106);
        float footer = height - 114;
        Fill(0, footer, width, 114, Background); Rule(0, footer, width);
        if (noticeTime > 0) Text(Trim(notice, (int)(width / 9)), 28, footer + 13, 17, Gold);
        else if (batchSelectMode) Text("Choose two opposite 3D corners, then refine the group with right and left click.", 28, footer + 13, 17, Teal);
        else Text(captured ? "LEFT remove    RIGHT place    MIDDLE apply template to the aimed tower    U configure template" : "Choose point counts and a pattern, then press Tab and middle-click a tower to apply.", 28, footer + 13, 17, Teal);
        Text(batchSelectMode ? "MIDDLE  two 3D corners    RIGHT  add tower    LEFT  deselect    WHEEL  corner depth    HOME  aim surface    B  finish"
            : "WASD  move     MOUSE  look     SPACE / SHIFT  up / down     TAB  cursor     ENTER  start level", 28, footer + 48, 15, Muted);
        Text("1-4 pieces   5/6 blocks   7 dig   8/9/0 liquids   L shop   G armory   U upgrades   J pattern   F formation   B batch select   P pause   F1 help", 28, footer + 75, 13, Muted);
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
        string[] names = ["Pawn", "Knight", "Bishop", "Rook"], descriptions = ["Adjacent orthogonal melee", "24 jumps / 3 planes / lobbed shots", "Unlimited plane rays / pellets", "Unlimited orthogonal / pellets"];
        for (int i = 0; i < 4; i++)
        {
            int k = i; float row = y + 34 + i * 53; bool active = tool == i;
            Button($"{i + 1}   {names[i]}", x + 16, row, 280, 47, () => { tool = k; panel = 0; }, active);
            Text(Balance.PieceCost((PieceKind)i).ToString(), x + 269, row + 9, 19, active ? Background : Gold);
            Text(descriptions[i], x + 54, row + 32, 12, active ? new Color(30, 75, 64) : Muted);
        }
        float next = y + 250;
        Button("Terrain / tools & blocks", x + 16, next, 280, 34, () => panel = 5, textSize: 17);
        Button("Liquids / water, mercury & lava", x + 16, next + 39, 280, 34, () => panel = 6, textSize: 17);
        Button("F  Formation / save & auto restore", x + 16, next + 78, 280, 34, () => panel = 8, textSize: 15);
        bool preparation = session.Phase == GamePhase.Building;
        Text(preparation ? "BATTLE MODE / choose before starting" : "BATTLE MODE / locked during combat", x + 20, height - 306, 12, Muted);
        Button("1 level", x + 16, height - 283, 78, 40, () => SelectBattleMode(0), !session.MergeFiveLevels, preparation, textSize: 14);
        Button("Merge 5", x + 98, height - 283, 89, 40, () => SelectBattleMode(1), session.MergeFiveLevels && !session.AllInOne, preparation, textSize: 14);
        Button("All in one", x + 191, height - 283, 105, 40, () => SelectBattleMode(2), session.AllInOne, preparation, textSize: 14);
        Text($"{(session.LevelSpan == 1 ? "Level" : "Levels")} {LevelRange} / {session.PreparedWaves.Count} wave(s) / {session.SurvivorRewardPerTower} C per tower", x + 20, height - 238, 12, Gold);
        Text(session.AllInOne ? "5x spawns / final equipment / 7.5x bonus" : session.MergeFiveLevels ? "2.5x bonus / resets after win or loss" : "Merge 5: 25 C / All in one: 75 C per survivor", x + 20, height - 222, 11, Muted);
        Button(preparation ? $"ENTER  /  Start {(session.LevelSpan == 1 ? "level" : "levels")}" : "DEFENSE ACTIVE", x + 16, height - 201, 280, 47,
            BeginLevel, preparation, preparation);
    }
    private void DrawFormationPanel(float x, float y)
    {
        Text("TURRET FORMATION", x + 20, y, 21);
        Text("Save the towers you have built and configured.", x + 20, y + 33, 12, Muted);
        Text("Positions, piece types, upgrades and jumps.", x + 20, y + 53, 12, Muted);
        bool canInteract = session.CanInteractWithTowers;
        Button(session.SavedFormation == null ? "Save formation" : "Replace saved formation", x + 16, y + 82, 280, 43,
            SaveFormation, enabled: canInteract && session.Turrets.Any(t => t.Hp > 0), textSize: 17);
        int savedCount = session.SavedFormation?.Towers.Count ?? 0;
        Text(savedCount == 0 ? "No formation saved yet." : $"{savedCount:N0} saved / {session.MissingFormationCount:N0} missing", x + 20, y + 141, 17, Teal);
        Text("Terrain uses your blocks and owned tools.", x + 20, y + 174, 12, Muted);
        Rule(x + 20, y + 206, 272);
        Text("COST TO REBUILD MISSING TOWERS", x + 20, y + 225, 12, Muted);
        Text($"{session.MissingFormationCurrencyCost:N0} currency / {session.Economy.Currency:N0} held", x + 20, y + 249, 17, Gold);
        var points = session.MissingFormationUpgradeCosts;
        Text($"Points: HP {points.GetValueOrDefault(UpgradeKind.HP):N0} / ATK {points.GetValueOrDefault(UpgradeKind.ATK):N0}", x + 20, y + 281, 13, Muted);
        Text($"O {points.GetValueOrDefault(UpgradeKind.Orthogonal):N0} / PD {points.GetValueOrDefault(UpgradeKind.PlaneDiagonal):N0} / SD {points.GetValueOrDefault(UpgradeKind.SpaceDiagonal):N0} / J {points.GetValueOrDefault(UpgradeKind.Jump):N0}", x + 20, y + 303, 13, Muted);
        Button(session.IsAutoRestoringFormation ? "Stop auto restore" : "Auto restore", x + 16, y + 339, 280, 45,
            ToggleFormationRestore, session.IsAutoRestoringFormation,
            session.IsAutoRestoringFormation || canInteract && session.MissingFormationCount > 0, textSize: 19);
        string statusLine = "";
        int statusRow = 0;
        foreach (string word in session.FormationRestoreStatus.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string nextLine = statusLine.Length == 0 ? word : statusLine + " " + word;
            if (font.Measure(nextLine, 12) > 272 && statusLine.Length > 0)
            {
                Text(statusLine, x + 20, y + 399 + statusRow++ * 18, 12, session.IsAutoRestoringFormation ? Gold : Muted);
                statusLine = word;
            }
            else statusLine = nextLine;
        }
        Text(statusLine, x + 20, y + 399 + statusRow * 18, 12, session.IsAutoRestoringFormation ? Gold : Muted);
        Button("Back to deploy", x + 16, height - 201, 280, 47, () => panel = 0);
    }
    private void DrawTerrainPanel(float x, float y)
    {
        Text("EXCAVATE & FORTIFY", x + 20, y, 21);
        Text("Fortify the core enclosure and turret area.", x + 20, y + 33, 13, Muted);
        Button(session.Inventory.HasShovel ? "Shovel / owned" : "Buy shovel / 100", x + 16, y + 62, 280, 40, () => BuyTool(ToolKind.Shovel), session.Inventory.HasShovel, !session.Inventory.HasShovel && session.Phase == GamePhase.Building);
        Text("Required to excavate dirt", x + 20, y + 108, 13, Muted);
        Button(session.Inventory.HasPickaxe ? "Pickaxe / owned" : "Buy pickaxe / 1,000", x + 16, y + 133, 280, 40, () => BuyTool(ToolKind.Pickaxe), session.Inventory.HasPickaxe, !session.Inventory.HasPickaxe && session.Phase == GamePhase.Building);
        Text("Stone, recovered materials and obsidian", x + 20, y + 179, 12, Muted);
        Rule(x + 20, y + 208, 272);
        Button($"5  Dirt  /  {session.Inventory.GetCount(Material.Dirt)} in bag", x + 16, y + 224, 280, 40, () => tool = 4, tool == 4);
        Text("100 HP / place one excavated dirt block", x + 20, y + 269, 13, Muted);
        Button($"6  Stone  /  {session.Inventory.GetCount(Material.Stone)} in bag", x + 16, y + 295, 280, 40, () => tool = 5, tool == 5);
        Text("500 HP / place one excavated stone block", x + 20, y + 340, 13, Muted);
        Button("7  Excavate / left-click terrain", x + 16, y + 367, 280, 40, () => tool = 6, tool == 6);
        Button("Recovered materials / use & sell", x + 16, y + 417, 280, 40, () => panel = 7, textSize: 16);
        Button("Return to field", x + 16, height - 201, 280, 47, () => Capture(true), true);
    }
    private void DrawMaterialsPanel(float x, float y)
    {
        Text("RECOVERED MATERIALS", x + 20, y, 20);
        Text(session.Phase == GamePhase.Building ? "Reuse blocks or sell stored materials." : "Placement and sales reopen between levels.", x + 20, y + 33, 12, Muted);
        for (int i = 0; i < SaleMaterials.Length; i++)
        {
            var material = SaleMaterials[i];
            bool obsidian = material == Material.Obsidian;
            int count = session.Inventory.GetCount(material), price = Balance.BlockSellValue(material);
            float row = y + 68 + i * 47;
            Text(material.ToString(), x + 20, row, 17);
            Text($"{count:N0} {(obsidian ? "layers" : "in bag")}", x + 20, row + 23, 12, Muted);
            bool canPlace = session.Phase == GamePhase.Building && count > 0;
            bool canSell = session.Phase == GamePhase.Building && count >= (obsidian ? LiquidSystem.UnitsPerBlock : 1);
            Button("Place", x + 140, row + 3, 72, 28, () => SelectRecoveredMaterial(material),
                (tool is 4 or 5 or 10) && BlockMaterial == material, canPlace, textSize: 16);
            Button(obsidian ? "Sell 10" : "Sell 1", x + 220, row + 3, 76, 28, () => SellBlock(material), enabled: canSell, textSize: 16);
            Text(obsidian ? "5 HP / layer" : $"{Balance.BlockHp(material):N0} HP", x + 145, row + 33, 10, Muted);
            Text($"+{price:N0}", x + 234, row + 33, 11, Gold);
        }
        Text("Dirt cannot be sold. Obsidian sells in 10s.", x + 20, y + 448, 12, Muted);
        Button("Back to terrain / tools", x + 16, height - 201, 280, 47, () => panel = 5, true);
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
        Text("Towers share up to 5 units with liquid / crust.", x + 20, y + 456, 12, Muted);
        Button("Return to field", x + 16, height - 201, 280, 47, () => Capture(true), true);
    }
    private static Color LiquidColor(LiquidKind kind) => kind switch
    {
        LiquidKind.Water => new Color(100, 181, 244), LiquidKind.Mercury => new Color(196, 203, 218), _ => new Color(249, 147, 82)
    };
    private void DrawGachaPanel(float x, float y)
    {
        Text("UPGRADE COMPONENTS", x + 20, y, 18);
        Text("5 currency / pull; random point type", x + 20, y + 29, 13, Muted);
        int i = 0;
        foreach (var kind in Enum.GetValues<UpgradeKind>())
        {
            var progress = session.Economy.GetProgress(kind);
            float row = y + 52 + i++ * 22;
            Text(UpgradeName(kind), x + 20, row, 14);
            Text($"{progress.Available} pts / cap {progress.Rank}", x + 175, row, 12, Gold);
        }
        bool preparation = session.Phase == GamePhase.Building;
        Button("Pull 1 / 5", x + 16, y + 190, 135, 32, () => Pull(1), true, preparation, textSize: 14);
        Button("Pull 10 / 50", x + 161, y + 190, 135, 32, () => Pull(10), enabled: preparation, textSize: 14);
        Button("Pull 100 / 500", x + 16, y + 230, 135, 32, () => Pull(100), enabled: preparation, textSize: 14);
        Button("Pull 1000 / 5000", x + 161, y + 230, 135, 32, () => Pull(1000), enabled: preparation, textSize: 14);
        Text("BATCH TARGETS", x + 20, y + 276, 13, Muted);
        Button($"All ({session.Turrets.Count(turret => turret.Hp > 0)})", x + 16, y + 298, 135, 30,
            () => { batchAllTurrets = true; batchSelectMode = false; }, batchAllTurrets, textSize: 16);
        Button($"Selected ({batchSelection.SelectedIds.Count})", x + 161, y + 298, 135, 30,
            () => batchAllTurrets = false, !batchAllTurrets, textSize: 16);
        Button(batchSelectMode ? "B  Finish selecting" : "B  Select in field", x + 16, y + 336, 190, 30,
            ToggleBatchSelection, batchSelectMode, session.CanInteractWithTowers, textSize: 15);
        Button("Clear", x + 214, y + 336, 82, 30, ClearBatchSelection, enabled: batchSelection.SelectedIds.Count > 0 || batchSelection.FirstCorner.HasValue, textSize: 15);
        var costs = session.GetUpgradeBatchCosts(BatchTargets(), upgradeTemplate);
        Text($"Full: HP {costs.GetValueOrDefault(UpgradeKind.HP)}  ATK {costs.GetValueOrDefault(UpgradeKind.ATK)}  O {costs.GetValueOrDefault(UpgradeKind.Orthogonal)}",
            x + 20, y + 378, 12, Muted);
        Text($"PD {costs.GetValueOrDefault(UpgradeKind.PlaneDiagonal)}  SD {costs.GetValueOrDefault(UpgradeKind.SpaceDiagonal)}  J {costs.GetValueOrDefault(UpgradeKind.Jump)}",
            x + 20, y + 396, 12, Muted);
        Button("Spend now", x + 16, y + 416, 135, 36, () => BuyUpgradeBatch(false),
            enabled: BatchTargets().Length > 0 && session.Economy.Progress.Values.Any(progress => progress.Available > 0));
        var queued = session.QueuedUpgradeBatch;
        Button(queued == null ? "Queue full" : "Cancel queue", x + 161, y + 416, 135, 36,
            queued == null ? () => BuyUpgradeBatch(true) : CancelUpgradeBatch,
            enabled: queued != null || BatchTargets().Length > 0);
        Text(queued == null ? "Per stat: 2 pts to reach the current cap."
            : queued.GraceRemaining is { } seconds
                ? $"Queued {queued.Cost} pts / {seconds:0.0}s to cancel"
                : $"Queued {queued.Cost} pts / waiting for each type",
            x + 20, y + 460, 11, queued == null ? Muted : Gold);
        Button("Configure upgrade template", x + 16, height - 201, 280, 47, () => { batchSelectMode = false; panel = 2; });
    }
    private void DrawUpgradePanel(float x, float y)
    {
        Text("UPGRADE TEMPLATE", x + 20, y, 22);
        Text(templateField.HasValue ? "Enter saves / Esc cancels / Tab next."
            : "Click points to edit / Max costs up to 2.", x + 20, y + 33, 12, Muted);
        Text(selected is null ? "Aim at a tower to inspect its stat levels." : $"{selected.Kind} #{selected.Id}   HP {selected.Hp:0}/{session.Economy.GetMaxHp(selected)}   ATK {session.Economy.GetAttack(selected)}", x + 20, y + 58, 13, Teal);
        int i = 0;
        foreach (var kind in Enum.GetValues<UpgradeKind>())
        {
            float row = y + 89 + i++ * 51;
            int amount = TemplatePoints(kind), allocated = selected is null ? 0 : session.Economy.GetAllocated(selected, kind);
            Text(UpgradeName(kind), x + 20, row, 14);
            bool infinite = selected is not null && session.Economy.GetRange(selected, kind) == int.MaxValue;
            var progress = session.Economy.GetProgress(kind);
            int maxCost = selected is null ? 2
                : session.Economy.GetUpgradePointCost(selected, kind, progress.Rank);
            string detail = kind == UpgradeKind.Jump && upgradeTemplate.UseJumpPattern
                ? $"Have {progress.Available} / level {allocated}/{progress.Rank} / pattern {JumpRules.FormationRadius(upgradeTemplate.JumpPattern)}"
                : infinite ? $"Have {progress.Available} / unlimited"
                : $"Have {progress.Available} / level {allocated}/{progress.Rank} / max {maxCost} pt";
            float detailSize = Math.Min(11, 272 * 11 / Math.Max(1, font.Measure(detail, 11)));
            Text(detail, x + 20, row + 30, detailSize, Muted);
            bool editing = templateField == kind;
            var fieldBounds = new Rectangle((int)(x + 156), (int)(row - 3), 64, 28);
            Fill(fieldBounds.X, fieldBounds.Y, fieldBounds.Width, fieldBounds.Height, editing ? new Color(39, 57, 70) : Background);
            if (editing)
            {
                templateFieldBounds = fieldBounds;
                Fill(fieldBounds.X, fieldBounds.Bottom - 2, fieldBounds.Width, 2, Teal);
                if (templateFieldSelected) Fill(fieldBounds.X + 4, fieldBounds.Y + 3, fieldBounds.Width - 8, 20, new Color(58, 89, 99));
            }
            string value = editing ? templateFieldText : amount.ToString();
            float size = Math.Min(16, 54 * 16 / Math.Max(1, font.Measure(value, 16)));
            Text(value, fieldBounds.X + 5, row + 2, size, editing ? TextColor : amount > 0 && TemplateApplicable(kind) == 0 ? Muted : Gold);
            if (editing && !templateFieldSelected && (int)(elapsed * 2) % 2 == 0)
                Fill(fieldBounds.X + 5 + font.Measure(value, size), row + 2, 1, 18, Teal);
            buttons.Add((fieldBounds, () => BeginTemplateField(kind)));
            Button("Min", x + 225, row - 3, 34, 28, () => SetTemplatePoints(kind, 0), textSize: 10);
            Button("Max", x + 264, row - 3, 34, 28, () => SetTemplatePoints(kind, TemplateMaximum(kind)), textSize: 10);
        }
        Button("J  /  Jump pattern", x + 16, y + 408, 177, 40, OpenJumpEditor);
        Button(upgradeTemplate.UseJumpPattern ? "Use: ON" : "Use: OFF", x + 201, y + 408, 95, 40, ToggleTemplatePattern, upgradeTemplate.UseJumpPattern);
        Text(upgradeTemplate.UseJumpPattern
            ? selected is { } target && session.Economy.GetJumpPatternPointCost(target, upgradeTemplate.JumpPattern) is { } jumpCost
                ? $"Jump needed: {jumpCost} pt. Extra stays banked."
                : "Patterns cost up to 2 points per tower."
            : "2 pts reach cap / 2 pts catch up later.", x + 20, y + 459, 12, Muted);
        Button("Ready / return to field", x + 16, height - 201, 280, 47, () => Capture(true), true);
    }
    private void DrawJumpEditorHud()
    {
        Fill(0, 0, width, 105, Background);
        Text("JUMP FORMATION / 3D EDITOR", 27, 18, 28, Teal);
        Text("Left-click plane to add  |  Click a destination to remove  |  Middle-drag orbit  |  Wheel zoom", 28, 64, 16, Muted);
        float x = width - 314;
        Fill(x, 105, 314, height - 220, Panel);
        Text("VIEW & PLACEMENT", x + 20, 131, 17, Gold);
        Text($"Layer Y {jumpEditor.Layer:+0;-0;0}  /  radius {JumpEditorRadius()}", x + 20, 169, 16);
        Button("Y -", x + 19, 202, 128, 42, () => jumpEditor.SetLayer(jumpEditor.Layer - 1, JumpEditorRadius()));
        Button("Y +", x + 164, 202, 128, 42, () => jumpEditor.SetLayer(jumpEditor.Layer + 1, JumpEditorRadius()));
        Button(jumpEditor.CrossSection ? "Section: ON" : "Section: OFF", x + 19, 263, 273, 43,
            jumpEditor.ToggleCrossSection, jumpEditor.CrossSection);
        Text("Section shows this Y layer only.", x + 20, 318, 13, Muted);
        Text($"Zoom {jumpEditor.Zoom:0.0}", x + 20, 357, 16);
        Button("Zoom +", x + 19, 390, 128, 42, () => jumpEditor.ZoomBy(1));
        Button("Zoom -", x + 164, 390, 128, 42, () => jumpEditor.ZoomBy(-1));
        Text($"{draftPattern.Count} destinations", x + 20, 460, 17, Teal);
        long radius = JumpRules.FormationRadius(draftPattern);
        var patternCost = session.Economy.GetJumpPatternPointCost(selected ?? new Turret { Kind = PieceKind.Pawn }, draftPattern);
        Text(patternCost is { } cost ? $"Formation cost: {cost} Jump points" : $"Formation radius: {radius}", x + 20, 493, 14, Gold);
        int cap = session.Economy.GetAllocationCap(UpgradeKind.Jump);
        Text($"Collection cap: {cap}", x + 20, 519, 13, Muted);
        Text(cap < radius ? "New edits need a higher cap." : "2 points whenever more reach is needed.",
            x + 20, 543, 12, cap < radius ? Gold : Muted);
        Button("Reset knight pattern", x + 19, 578, 273, 40, () =>
        {
            draftPattern.Clear(); draftPattern.AddRange(JumpRules.DefaultKnightPattern());
            jumpEditor.SetLayer(0, JumpEditorRadius());
        }, textSize: 16);
        Button("Use in template", x + 19, height - 218, 273, 43, () =>
        {
            upgradeTemplate.JumpPattern.Clear(); upgradeTemplate.JumpPattern.AddRange(draftPattern);
            upgradeTemplate.UseJumpPattern = true; panel = 2; jumpPreview = null;
            SetNotice("Formation saved to template; aim and middle-click to apply its Jump cost.");
            SaveTemplate();
        }, enabled: draftPattern.Count > 0);
        Button("Cancel / back", x + 19, height - 164, 273, 42, () => { panel = 2; jumpPreview = null; });
        Fill(0, height - 114, width, 114, Background);
        Text(jumpPreview is { } candidate ? $"Place destination {candidate} on Y {jumpEditor.Layer:+0;-0;0}"
            : "Orbit to inspect all three dimensions. The gold cube is the origin.", 28, height - 91, 17, Teal);
        Text(noticeTime > 0 ? notice : "Esc cancels. Pattern edits do not spend points until applied to a turret.",
            28, height - 53, 15, noticeTime > 0 ? Gold : Muted);
    }
    private void DrawHelpPanel(float x, float y)
    {
        Text("FIELD MANUAL", x + 20, y, 23);
        string[] lines = ["WASD  horizontal movement", "Space  ascend / Shift  descend", "Mouse  pan / Ctrl  move faster", "Tab  release or capture mouse", "Left click  remove tower / block", "Right click  place / pour", "Middle click  apply upgrade template", "B batch: middle corners / right add", "B batch: left deselect / wheel depth", "1-4 pieces / 5-7 terrain / 8-0 liquids", "L shop / G armory / U upgrades", "J pattern / F formation", "Enter start / P pause / V guides", "R retry / F1 help / close to save"]; int i = 0;
        foreach (var line in lines) Text(line, x + 20, y + 49 + i++ * 27, 15, Muted);
        Button("Return to field", x + 16, height - 201, 280, 47, () => { panel = 0; Capture(true); }, true);
    }
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 3)] + "...";
    protected override void UnloadContent() { jumpView.Dispose(); renderer.Dispose(); font.Dispose(); pixel.Dispose(); batch.Dispose(); base.UnloadContent(); }
}
