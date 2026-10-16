using System.Numerics;

namespace ChessVsZombies.Core;

/// <summary>Rendering-independent building, wave scheduling, navigation, and combat.</summary>
public sealed partial class GameSession
{
    private static readonly Cell[] Horizontal = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)];
    private static readonly Cell[] Adjacent = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
    private readonly int _seed;
    private readonly LevelCatalog _levels;
    private Random _random;
    private int _nextId = 1;
    private int _topologyRevision;
    private int _navigationRevision = -1;
    private int _navigationWorldRevision = -1;
    private int _settledWorldRevision = -1;
    private int _settledTopologyRevision = -1;
    private readonly Dictionary<Cell, Cell>[] _routes = [[], [], [], []];
    private readonly Dictionary<Cell, Turret> _turretsByCell = [];
    private readonly Dictionary<int, Cell> _stepOrigins = [];
    private readonly Dictionary<int, float> _jumpHeights = [];
    private readonly Dictionary<int, float> _fallStartHeights = [];
    private readonly Dictionary<int, Vector3> _liquidDestinations = [];
    private readonly Dictionary<int, Vector3> _liquidOrigins = [];
    private readonly Dictionary<(Vector3 Position, bool SinksInLava, bool SinksInMercury, ZombieSize Size), Vector3?> _buoyantSteps = [];
    private readonly List<WaveSpawnPlan> _wavePlans = [];
    private LevelDefinition? _previewLevel;
    private IReadOnlyList<WaveSpawnPlan> _previewPlans = [];
    private long _buoyantLiquidRevision = -1;
    private int _buoyantWorldRevision = -1, _buoyantTopologyRevision = -1;
    private readonly Dictionary<LiquidKind, int> _liquidInventory = Enum.GetValues<LiquidKind>().ToDictionary(kind => kind, _ => 0);
    private List<Cell> _spawnCells = [];

    public WorldGrid World { get; private set; } = new();
    public LiquidSystem Liquids { get; private set; }
    public IReadOnlyDictionary<LiquidKind, int> LiquidInventory => _liquidInventory;
    public Economy Economy { get; }
    public BlockInventory Inventory { get; } = new();
    public FallAnimations Falls { get; } = new();
    public CombatFeedback Feedback { get; } = new();
    public UpgradeTemplate UpgradeTemplate { get; private set; } = new();
    public List<Turret> Turrets { get; } = [];
    public List<Zombie> Zombies { get; } = [];
    public List<Projectile> Projectiles { get; } = [];
    public List<WaveState> Waves { get; } = [];
    public float CoreHp { get; private set; } = Balance.CoreHp;
    public int LevelNumber { get; private set; } = 1;
    public LevelDefinition Level { get; private set; }
    public GamePhase Phase { get; private set; } = GamePhase.Building;
    public bool CanInteractWithTowers => Phase is GamePhase.Building or GamePhase.Combat;
    public string Message { get; private set; } = "Build defenses, collect upgrades, then start the level.";
    public string MigrationNotice { get; internal set; } = string.Empty;
    /// <summary>Completed buoyant path searches, useful for profiling repeated decision work.</summary>
    public long BuoyantRouteSearchCount { get; private set; }
    public IReadOnlyList<WaveSpawnPlan> PreparedWaves
    {
        get
        {
            if (!ReferenceEquals(_previewLevel, Level))
            {
                _previewPlans = AllInOne ? [_allInOnePlan!] : Level.Waves.Select(WaveSpawnPlanner.Plan).ToArray();
                _previewLevel = Level;
            }
            return _previewPlans;
        }
    }

    public GameSession(int seed = 12345, LevelCatalog? levels = null)
    {
        _seed = seed;
        _random = new Random(seed);
        _levels = levels ?? new LevelCatalog(Path.Combine(AppContext.BaseDirectory, "Levels"));
        Economy = new Economy(seed);
        Liquids = new LiquidSystem(World);
        Level = _levels.Get(1);
    }

    public void Reset()
    {
        World = new WorldGrid();
        Liquids = new LiquidSystem(World);
        foreach (var kind in Enum.GetValues<LiquidKind>()) _liquidInventory[kind] = 0;
        Economy.Reset();
        Inventory.Reset();
        Feedback.Reset();
        UpgradeTemplate = new UpgradeTemplate();
        QueuedUpgradeBatch = null;
        Turrets.Clear();
        Zombies.Clear();
        Projectiles.Clear();
        Waves.Clear();
        _turretsByCell.Clear();
        _stepOrigins.Clear();
        _jumpHeights.Clear();
        _fallStartHeights.Clear();
        _liquidDestinations.Clear();
        _liquidOrigins.Clear();
        _buoyantSteps.Clear();
        _buoyantLiquidRevision = -1;
        _buoyantWorldRevision = -1;
        _buoyantTopologyRevision = -1;
        BuoyantRouteSearchCount = 0;
        Falls.Clear();
        _spawnCells.Clear();
        ResetBridges();
        ResetSpentRoutes();
        ResetCoverBehavior();
        ResetBucketBehavior();
        ResetKilos();
        ResetMegas();
        ResetSizeRoutes();
        ResetRetreatBehavior();
        _returningZombies.Clear();
        _wavePlans.Clear();
        _random = new Random(_seed);
        _nextId = 1;
        _topologyRevision++;
        _navigationRevision = -1;
        LevelNumber = 1;
        Level = _levels.Get(1);
        ResetLevelMerging();
        CoreHp = Balance.CoreHp;
        Phase = GamePhase.Building;
        Message = "New defense: 1000 currency. Place your pieces and protect the core.";
        MigrationNotice = string.Empty;
    }

    public Turret? TrySelectTurret(Cell cell)
    {
        // Keep list order and public-state changes visible without allocating a
        // captured predicate for every support, collision, and liquid query.
        foreach (var turret in Turrets)
            if (turret.Cell == cell && turret.Hp > 0) return turret;
        return null;
    }
    public bool HasTowerSupport(Cell cell) => WorldGrid.IsInside(cell) && cell.Y > 0 &&
        (World.GetMaterial(cell + new Cell(0, -1, 0)) != Material.Air || TrySelectTurret(cell + new Cell(0, -1, 0)) != null);

    public bool IsOccupiedByZombie(Cell cell)
    {
        var min = new Vector3(cell.X, cell.Y, cell.Z);
        var max = min + Vector3.One;
        return Zombies.Any(zombie => zombie.Hp > 0 &&
            BoundsOverlap(min, max, zombie.Position - zombie.BodyHalfExtents, zombie.Position + zombie.BodyHalfExtents));
    }

    public bool ApplyUpgradeTemplate(Cell cell, UpgradeTemplate template)
    {
        if (!CanUseTowers()) return false;
        var turret = TrySelectTurret(cell);
        if (turret == null) return Reject("Aim at a living deployed piece to apply the upgrade template.");
        if (!Economy.TryApplyTemplate(turret, template, out var error)) return Reject(error);
        Message = Economy.LastMessage;
        CancelChangedUpgradeBatch();
        return true;
    }

    public CampaignState ExportState()
    {
        if (Phase != GamePhase.Building) throw new InvalidOperationException("Checkpoints are saved between levels.");
        var excavated = new List<Cell>();
        for (var x = 0; x < WorldGrid.Size; x++)
            for (var y = 0; y < 16; y++)
                for (var z = 0; z < WorldGrid.Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (!WorldGrid.IsCore(cell) && World.GetNaturalMaterial(cell) != WorldGrid.InitialMaterial(cell)) excavated.Add(cell);
                }
        var megaFlights = ExportMegaWeaponFlights();
        return new CampaignState
        {
            LevelNumber = LevelNumber,
            MergeFiveLevels = MergeFiveLevels,
            AllInOne = AllInOne,
            CoreHp = CoreHp,
            Economy = Economy.ExportState(),
            UpgradeTemplate = SavedUpgradeTemplate.FromTemplate(UpgradeTemplate),
            QueuedUpgradeBatch = CopyUpgradeBatch(QueuedUpgradeBatch),
            Inventory = Inventory.ExportState(),
            PendingBlockDrops = _pendingBlockDrops.ToList(),
            ReturningZombies = _returningZombies.Count == 0 ? null : _returningZombies.Select(zombie => zombie.Copy()).ToList(),
            Excavated = excavated,
            TerrainPlacements = World.MovableBlocks().Where(cell => Balance.IsTerrainMaterial(World.GetNaturalMaterial(cell)) &&
                World.GetNaturalMaterial(cell) != WorldGrid.InitialMaterial(cell))
                .Select(cell => new SavedTerrainPlacement { Cell = cell, Material = World.GetNaturalMaterial(cell) }).ToList(),
            DamagedTerrain = World.DamagedTerrain.Select(pair => new SavedTerrainDamage { Cell = pair.Key, Hp = pair.Value }).ToList(),
            Liquids = Liquids.ExportState(),
            Buckets = Buckets.ExportState(),
            MegaWeaponFlights = megaFlights.Count == 0 ? null : megaFlights,
            LiquidInventory = new Dictionary<LiquidKind, int>(_liquidInventory),
            Turrets = Turrets.Select(turret => new SavedTurret
            {
                Id = turret.Id,
                Kind = turret.Kind,
                Cell = turret.Cell,
                Hp = turret.Hp,
                UpgradePoints = new Dictionary<UpgradeKind, int>(turret.UpgradePoints),
                JumpPattern = turret.JumpPattern.ToList()
            }).ToList(),
            Barriers = World.Barriers.Select(barrier => new SavedBarrier { Cell = barrier.Cell, Content = barrier.Content, Hp = barrier.Hp }).ToList()
        };
    }

    internal void RestoreCheckpoint(CampaignState state)
    {
        Reset();
        Economy.ImportState(state.Economy);
        UpgradeTemplate = state.UpgradeTemplate!.ToTemplate();
        QueuedUpgradeBatch = CopyUpgradeBatch(state.QueuedUpgradeBatch);
        var inventory = state.Inventory!;
        Inventory.Restore(inventory);
        _pendingBlockDrops.AddRange(state.PendingBlockDrops!);
        if (state.ReturningZombies != null)
        {
            _returningZombies.AddRange(state.ReturningZombies.Select(zombie => zombie.Copy()));
            foreach (var returning in _returningZombies) _nextId = Math.Max(_nextId, returning.Id + 1);
        }
        LevelNumber = state.LevelNumber;
        MergeFiveLevels = state.MergeFiveLevels;
        AllInOne = state.AllInOne;
        var encounter = PrepareEncounter(LevelNumber, MergeFiveLevels, AllInOne);
        Level = encounter.Level;
        _sourceLevelNumbers = encounter.SourceLevels;
        _allInOnePlan = encounter.CombinedPlan;
        CoreHp = state.CoreHp;
        foreach (var cell in state.Excavated) World.Dig(cell);
        foreach (var placement in state.TerrainPlacements!) World.RestoreTerrainPlacement(placement.Cell, placement.Material);
        foreach (var damage in state.DamagedTerrain!) World.RestoreTerrainHp(damage.Cell, damage.Hp);
        foreach (var barrier in state.Barriers) World.AddBarrier(barrier.Cell, barrier.Content!.Value, barrier.Hp);
        Liquids.RestoreState(state.Liquids!);
        Buckets.RestoreState(state.Buckets ?? new BucketSystemState());
        RestoreMegaWeaponFlights(state.MegaWeaponFlights ?? []);
        foreach (var kind in Enum.GetValues<LiquidKind>()) _liquidInventory[kind] = state.LiquidInventory![kind];
        foreach (var saved in state.Turrets)
        {
            var turret = new Turret { Id = saved.Id, Kind = saved.Kind, Cell = saved.Cell, Hp = saved.Hp };
            foreach (var allocation in saved.UpgradePoints!) turret.UpgradePoints.Add(allocation.Key, allocation.Value);
            turret.JumpPattern.Clear();
            turret.JumpPattern.AddRange(saved.JumpPattern);
            Turrets.Add(turret);
            _nextId = Math.Max(_nextId, saved.Id + 1);
        }
        _topologyRevision++;
        Message = $"Restored checkpoint. Prepare level {LevelNumber}.";
    }

    public bool Deploy(PieceKind kind, Cell cell)
    {
        if (!CanUseTowers()) return false;
        if (!Enum.IsDefined(kind)) return Reject("Choose a valid piece.");
        if (!WorldGrid.IsTurretZone(cell)) return Reject("Pieces belong inside the 30-cube zone, outside the entire 8-cube block enclosure.");
        if (!IsEmpty(cell)) return Reject("That cell is occupied.");
        if (IsOccupiedByZombie(cell)) return Reject("A zombie is occupying that cell.");
        if (!HasTowerSupport(cell)) return Reject("Pieces need a solid block or another living tower directly below them.");
        if (Phase == GamePhase.Combat && !CanHoldSurvivorReward((long)Economy.Currency - Balance.PieceCost(kind),
            Turrets.Count(turret => turret.Hp > 0) + 1, SurvivorRewardPerTower))
            return Reject("The wallet cannot hold this tower's eventual survivor reward.");
        if (!Economy.TrySpend(Balance.PieceCost(kind))) return Reject(Economy.LastMessage);
        Turrets.Add(new Turret { Id = _nextId++, Kind = kind, Cell = cell });
        _topologyRevision++;
        Message = $"Deployed {kind} at {cell}.";
        return true;
    }

    public bool BuyTool(ToolKind tool)
    {
        if (!CanBuild()) return false;
        if (!Inventory.TryPurchase(tool, Economy, out var error)) return Reject(error);
        Message = $"Purchased {tool}. It is permanently available for excavation.";
        return true;
    }

    public bool SellBlock(Material material)
    {
        if (!CanBuild()) return false;
        int price = Balance.BlockSellValue(material);
        if (price <= 0) return Reject("Choose a recovered material to sell.");
        if (Economy.Currency > int.MaxValue - price) return Reject("The wallet cannot hold the full sale value.");
        int units = material == Material.Obsidian ? LiquidSystem.UnitsPerBlock : 1;
        if (!Inventory.TrySpend(material, units)) return Reject(material == Material.Obsidian
            ? "Selling obsidian requires 10 layers (one whole block)."
            : $"No {material.ToString().ToLowerInvariant()} blocks available to sell.");
        Economy.Credit(price);
        Message = $"Sold 1 {material.ToString().ToLowerInvariant()} block for {price} currency.";
        return true;
    }

    public bool BuyLiquid(LiquidKind kind)
    {
        if (!CanBuild()) return false;
        if (!Enum.IsDefined(kind)) return Reject("Choose water, mercury, or lava.");
        if (_liquidInventory[kind] > int.MaxValue - Balance.LiquidPurchaseUnits) return Reject("Liquid inventory is full.");
        if (!Economy.TrySpend(Balance.LiquidCost(kind))) return Reject(Economy.LastMessage);
        _liquidInventory[kind] += Balance.LiquidPurchaseUnits;
        Message = $"Purchased {Balance.LiquidPurchaseUnits} units of {kind.ToString().ToLowerInvariant()}.";
        return true;
    }

    public bool CanPourLiquid(Cell cell, LiquidKind kind, int units = Balance.LiquidPurchaseUnits) =>
        Phase == GamePhase.Building && Enum.IsDefined(kind) && units > 0 && WorldGrid.IsInside(cell) &&
        !WorldGrid.IsCore(cell) && TrySelectTurret(cell) == null && _liquidInventory.GetValueOrDefault(kind) >= units &&
        Liquids.CanAdd(cell, kind, units);

    public bool PourLiquid(Cell cell, LiquidKind kind, int units = Balance.LiquidPurchaseUnits)
    {
        if (!CanBuild()) return false;
        if (!Enum.IsDefined(kind) || units <= 0) return Reject("Choose a liquid and a positive quantity to pour.");
        if (!WorldGrid.IsInside(cell) || WorldGrid.IsCore(cell) || TrySelectTurret(cell) != null)
            return Reject("Pour into an unoccupied battlefield cell.");
        if (_liquidInventory[kind] < units) return Reject($"Not enough {kind.ToString().ToLowerInvariant()} in inventory.");
        if (!Liquids.TryAdd(cell, kind, units)) return Reject("The cell cannot hold the complete liquid quantity.");
        _liquidInventory[kind] -= units;
        Message = $"Poured {units} units of {kind.ToString().ToLowerInvariant()}.";
        return true;
    }

    public bool BuildBarrier(Cell cell, Material material = Material.Dirt)
    {
        if (!CanBuild()) return false;
        if (!Balance.IsTerrainMaterial(material)) return Reject("Choose a terrain material from inventory.");
        if (!WorldGrid.IsFortificationZone(cell)) return Reject("Blocks can be built in the core enclosure and turret area, outside the core and spawn shell.");
        if (!IsEmpty(cell)) return Reject("That cell is occupied.");
        if (!World.HasOrthogonalBlockSupport(cell)) return Reject("Blocks need a supporting block on one of their six faces.");
        if (!Inventory.TrySpend(material)) return Reject($"No {material.ToString().ToLowerInvariant()} blocks available. Excavate some first.");
        if (!World.AddBarrier(cell, material))
        {
            Inventory.Add(material);
            return Reject("That defensive block could not be placed.");
        }
        Message = $"Placed {material.ToString().ToLowerInvariant()} at {cell} ({Balance.BlockHp(material)} HP).";
        return true;
    }

    public bool Dig(Cell cell)
    {
        if (!CanBuild()) return false;
        var material = World.GetMaterial(cell);
        var crust = World.GetObsidian(cell);
        if (!Balance.IsTerrainMaterial(material) && crust is null) return Reject("Choose a material block or obsidian crust to excavate.");
        if (!Inventory.CanExcavate(material, out var error)) return Reject(error);
        int units = crust?.Units ?? 1;
        if (!Inventory.Add(material, units)) return Reject("Block inventory is full.");
        if (!(crust is null ? World.Dig(cell) : World.DamageObsidian(cell, crust.Hp)))
        {
            Inventory.TrySpend(material, units);
            return Reject("That block could not be excavated.");
        }
        SettleDefenses();
        Message = crust is null ? $"Excavated {cell}; collected 1 {material.ToString().ToLowerInvariant()} block."
            : $"Excavated {cell}; collected {units} obsidian layers.";
        return true;
    }

    public bool Remove(Cell cell)
    {
        var turret = TrySelectTurret(cell);
        if (turret != null)
        {
            if (!CanUseTowers()) return false;
            long refundedCurrency = (long)Economy.Currency + Balance.PieceCost(turret.Kind);
            if (refundedCurrency > int.MaxValue || Phase == GamePhase.Combat &&
                !CanHoldSurvivorReward(refundedCurrency, Turrets.Count(tower => tower.Hp > 0) - 1, SurvivorRewardPerTower))
                return Reject("The wallet cannot hold the refund and surviving-tower reward.");
            Turrets.Remove(turret);
            Economy.Credit(Balance.PieceCost(turret.Kind));
            _topologyRevision++;
            SettleDefenses();
            Message = $"Recovered {Balance.PieceCost(turret.Kind)} currency. Equipped components were consumed.";
            CancelChangedUpgradeBatch();
            return true;
        }
        if (!CanBuild()) return false;
        var barrier = World.GetBarrier(cell);
        if (barrier == null) return Reject("Choose a placed piece or defensive block to remove.");
        if (!Inventory.Add(barrier.Content)) return Reject("Block inventory is full.");
        World.RemoveBarrier(cell);
        SettleDefenses();
        Message = $"Recovered 1 {barrier.Content.ToString().ToLowerInvariant()} block.";
        return true;
    }

    public bool StartLevel()
    {
        if (Phase != GamePhase.Building) return Reject("The current level is already running.");
        if (!CanHoldSurvivorReward(Economy.Currency, Turrets.Count(turret => turret.Hp > 0), SurvivorRewardPerTower))
            return Reject("The wallet cannot hold the full surviving-tower reward.");
        WaveSpawnPlan[] plans;
        var waves = new List<WaveState>();
        (LevelDefinition Level, int[] SourceLevels, WaveSpawnPlan? CombinedPlan) nextEncounter;
        try
        {
            LevelCatalog.Validate(Level);
            nextEncounter = PrepareEncounter(checked(LevelNumber + LevelSpan), merged: false);
            plans = AllInOne ? [_allInOnePlan!] : Level.Waves.Select(WaveSpawnPlanner.Plan).ToArray();
            for (var i = 0; i < plans.Length; i++)
            {
                var plan = plans[i];
                waves.Add(new WaveState
                {
                    Index = i,
                    TotalCount = plan.SpawnCount,
                    TotalHp = plan.TotalHp,
                    HpBudget = plan.HpBudget,
                    NormalBudget = plan.NormalBudget,
                    BossBudget = plan.BossBudget,
                    Started = i == 0
                });
            }
            foreach (var returning in _returningZombies.Where(zombie => zombie.TargetLevel == LevelNumber))
                AddReinforcementBudget(waves[returning.TargetWaveIndex], returning);
        }
        catch (Exception error) when (error is OverflowException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return Reject($"Cannot start this level selection: {error.Message}");
        }
        RebuildNavigation();
        RefreshSpawnCells();
        Waves.Clear();
        Waves.AddRange(waves);
        _wavePlans.Clear();
        _wavePlans.AddRange(plans);
        _previewPlans = plans;
        _previewLevel = Level;
        _nextEncounter = nextEncounter.Level;
        _nextSourceLevelNumbers = nextEncounter.SourceLevels;
        Phase = GamePhase.Combat;
        Message = MergeFiveLevels
            ? $"Levels {LevelNumber}–{EndLevelNumber}: {Level.Name}. Defend the core!"
            : $"Level {LevelNumber}: {Level.Name}. Defend the core!";
        return true;
    }

    public void Update(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0) return;
        if (Phase == GamePhase.Defeat) { Feedback.Update(deltaSeconds); Falls.Update(deltaSeconds, World, Turrets); return; }
        // Substeps preserve collision and wave boundaries after a slow rendered frame.
        while (deltaSeconds > 0 && Phase != GamePhase.Defeat)
        {
            var step = Math.Min(deltaSeconds, .05f);
            Feedback.Update(step);
            Falls.Update(step, World, Turrets);
            SettleDefenses();
            ResolveBlockDrops();
            Liquids.Update(step, cell => TrySelectTurret(cell) != null);
            Buckets.Update(step, cell => TrySelectTurret(cell) != null);
            SettleDefenses();
            UpdateUpgradeBatch(step);
            if (Phase == GamePhase.Combat) Step(step);
            else UpdateProjectiles(step);
            deltaSeconds -= step;
        }
        if (deltaSeconds > 0)
        {
            Feedback.Update(deltaSeconds);
            Falls.Update(deltaSeconds, World, Turrets);
        }
        CancelChangedUpgradeBatch();
    }

    public void DamageZombie(Zombie zombie, float damage, DamageKind kind = DamageKind.Blunt,
        ShotKind? shot = null, Vector3? incomingDirection = null)
    {
        if (damage <= 0 || !float.IsFinite(damage) || !Enum.IsDefined(kind) || shot.HasValue && !Enum.IsDefined(shot.Value) || zombie.Hp <= 0) return;
        if (kind == DamageKind.Thermal)
            damage = zombie.ConsumeHeatProtection(damage / Balance.LavaDamagePerSecond) * Balance.LavaDamagePerSecond;
        ApplyZombieDamage(zombie, damage, kind, shot, incomingDirection);
    }

    private void ApplyZombieDamage(Zombie zombie, float damage, DamageKind kind,
        ShotKind? shot = null, Vector3? incomingDirection = null)
    {
        if (damage <= 0 || zombie.Hp <= 0) return;
        var before = zombie.Hp;
        var bodyBefore = zombie.BodyHp;
        var armorBefore = zombie.ArmorPieces.Sum(piece => piece.Hp);
        var sankInLava = zombie.SinksIn(LiquidKind.Lava);
        var sankInMercury = zombie.SinksIn(LiquidKind.Mercury);
        zombie.ApplyDamage(damage, kind, shot, incomingDirection);
        var durabilityLost = (bodyBefore - zombie.BodyHp) + (armorBefore - zombie.ArmorPieces.Sum(piece => piece.Hp));
        Feedback.Record(zombie.Id, zombie.Position, durabilityLost, kind);
        if (durabilityLost > 0)
        {
            OnZombieDamaged(zombie);
            if (shot is ShotKind.Pellet or ShotKind.Lob && incomingDirection is { } incoming)
                FaceKiloTowardShot(zombie, incoming);
        }
        if (zombie.Hp > 0 && (sankInLava != zombie.SinksIn(LiquidKind.Lava) || sankInMercury != zombie.SinksIn(LiquidKind.Mercury)))
            ClearZombieMovement(zombie);
        // Body death discards any remaining armor from the wave's live HP pool.
        var actual = before - zombie.Hp;
        if (zombie.Hp <= 0) { QueueDeathBlock(zombie); SpillZombieBucket(zombie); HandleKiloDeath(zombie); }
        if (zombie.WaveIndex >= 0 && zombie.WaveIndex < Waves.Count)
        {
            var wave = Waves[zombie.WaveIndex];
            wave.DamageTaken = Math.Min(wave.TotalHp, wave.DamageTaken + actual);
        }
        zombie.DamageSinceLaneSwitch += actual;
        var threshold = zombie.MaxHp * Balance.LaneThreshold(GetSourceLevelNumber(zombie.WaveIndex));
        if (zombie.Hp > 0 && zombie.DamageSinceLaneSwitch >= threshold)
        {
            zombie.Lane = (zombie.Lane + 1 + _random.Next(3)) % 4;
            zombie.DamageSinceLaneSwitch = 0;
            // Complete an in-progress physical step, then use the new approach lane.
        }
    }

    private void Step(float dt)
    {
        for (var i = 1; i < Waves.Count; i++)
            if (!Waves[i].Started && Waves[i - 1].Started && (Waves[i - 1].DamageTaken >= Waves[i - 1].TotalHp * .5f ||
                Waves[i - 1].Spawned >= Waves[i - 1].TotalCount && !Zombies.Any(zombie => zombie.Hp > 0 && zombie.WaveIndex == i - 1)))
            {
                Waves[i].Started = true;
                Message = $"Wave {i + 1}/{Waves.Count} is spawning: half of the previous wave's HP pool is gone.";
            }

        foreach (var wave in Waves)
        {
            if (!wave.Started || wave.Spawned >= wave.TotalCount) continue;
            wave.SpawnTimer -= dt;
            while (wave.SpawnTimer <= 0 && wave.Spawned < wave.TotalCount)
            {
                if (!Spawn(wave)) { wave.SpawnTimer = .1f; break; }
                wave.SpawnTimer += Level.Waves[wave.Index].SpawnInterval;
            }
        }

        if (_navigationRevision != _topologyRevision || _navigationWorldRevision != World.Revision) RebuildNavigation();
        UpdateTurrets(dt);
        UpdateProjectiles(dt);
        UpdateRetreatBehavior(dt);
        UpdateCoverGroups(dt);
        // Resolve exposure before batched Kilo actions so a dead carrier cannot
        // grab material or launch an ally earlier in this same simulation slice.
        foreach (var zombie in Zombies)
            if (zombie.Hp > 0) UpdateLiquidExposure(zombie, dt);
        UpdateKilos(dt);
        UpdateMegas(dt);
        foreach (var zombie in Zombies.ToArray())
            if (zombie.Hp > 0) UpdateZombie(zombie, dt);
        foreach (var zombie in Zombies)
            if (zombie.Hp <= 0)
            {
                QueueDeathBlock(zombie);
                SpillZombieBucket(zombie);
                HandleKiloDeath(zombie);
                Falls.CancelZombie(zombie);
                _fallStartHeights.Remove(zombie.Id);
                _stepOrigins.Remove(zombie.Id);
                _jumpHeights.Remove(zombie.Id);
                _liquidDestinations.Remove(zombie.Id);
                _liquidOrigins.Remove(zombie.Id);
            }
        Zombies.RemoveAll(zombie => zombie.Hp <= 0);
        ResolveBlockDrops();

        if (CoreHp <= 0)
        {
            CoreHp = 0;
            if (MergeFiveLevels)
            {
                var preparation = PrepareEncounter(LevelNumber, merged: false);
                MergeFiveLevels = false;
                AllInOne = false;
                _allInOnePlan = null;
                Level = preparation.Level;
                _sourceLevelNumbers = preparation.SourceLevels;
                _previewLevel = null;
                _nextEncounter = null;
                _nextSourceLevelNumbers = [];
            }
            Phase = GamePhase.Defeat;
            Message = $"The core fell on level {LevelNumber}. Start a new defense to try again.";
            return;
        }
        if (Zombies.Count == 0 && Waves.All(wave => wave.Started && wave.Spawned >= wave.TotalCount))
        {
            var reward = checked(SurvivorRewardPerTower * Turrets.Count(turret => turret.Hp > 0));
            Economy.Credit(reward);
            LevelNumber = checked(LevelNumber + LevelSpan);
            MergeFiveLevels = false;
            AllInOne = false;
            _allInOnePlan = null;
            Level = _nextEncounter!;
            _sourceLevelNumbers = _nextSourceLevelNumbers;
            _nextEncounter = null;
            _nextSourceLevelNumbers = [];
            _previewLevel = null;
            Phase = GamePhase.Building;
            Message = $"Level complete! {reward} currency from surviving pieces. Prepare level {LevelNumber}.";
        }
    }

    private bool Spawn(WaveState wave)
    {
        if (_spawnWorldRevision != World.Revision || _spawnTopologyRevision != _topologyRevision || _spawnLiquidRevision != Liquids.Revision)
            RefreshSpawnCells();
        if (TrySpawnReinforcement(wave, out bool reinforcementPending)) return true;
        if (reinforcementPending) return false;
        var group = WaveSpawnPlanner.SelectGroup(_wavePlans[wave.Index], wave.Spawned - wave.ReinforcementsSpawned);
        var shape = new Zombie { Size = group.Size };
        var available = FindSizedSpawnCells(shape).Where(cell => IsEmpty(cell) && HasZombieSupport(cell, shape) && IsBodyClear(shape.StandAt(cell), shape)).ToArray();
        if (available.Length == 0) return false;
        var cell = available[_random.Next(available.Length)];
        var hp = LevelCatalog.ZombieMaxHp(group.Kind, group.Tier, group.Size);
        var zombie = new Zombie
        {
            Id = _nextId++,
            WaveIndex = wave.Index,
            Kind = group.Kind,
            Tier = group.Tier,
            Size = group.Size,
            IsBoss = group.IsBoss,
            Position = shape.StandAt(cell),
            Hp = hp,
            MaxHp = hp,
            Attack = LevelCatalog.ZombieAttack(group.Kind, group.Tier, group.Size),
            CarriesBucket = shape.IsGiant || wave.Spawned % Balance.BucketCarrierInterval == 0 && NeedsBucketCarrier(cell, shape),
            OriginalBudgetCost = WaveSpawnPlan.BudgetCost(group),
            Lane = cell.X == 0 ? 0 : cell.X + shape.GridFootprint == WorldGrid.Size ? 1 : cell.Z == 0 ? 2 : 3
        };
        zombie.FaceToward(new Vector3(16, zombie.Position.Y, 16) - zombie.Position);
        Zombies.Add(zombie);
        wave.Spawned++;
        wave.BudgetSpent += WaveSpawnPlan.BudgetCost(group);
        return true;
    }

    private void UpdateTurrets(float dt)
    {
        foreach (var turret in Turrets)
        {
            if (turret.Hp <= 0) continue;
            turret.Cooldown = Math.Max(0, turret.Cooldown - dt);
            if (turret.Cooldown > 0) continue;
            var target = SelectTurretTarget(turret, out var shot, out var nearest);
            if (target == null) continue;
            var aim = GetTurretAimPoint(turret, target, shot);
            turret.Cooldown = shot == ShotKind.Lob ? 1f : .7f;
            if (shot == ShotKind.Melee)
            {
                DamageZombie(target, Economy.GetAttack(turret), DamageKind.Blunt, ShotKind.Melee, aim - turret.Cell.Center);
                // A brief strike segment is also visible to the renderer.
                Projectiles.Add(new Projectile
                {
                    Origin = turret.Cell.Center,
                    Target = aim,
                    TargetId = target.Id,
                    Damage = 0,
                    Kind = ShotKind.Melee,
                    Duration = .13f
                });
            }
            else
                Projectiles.Add(new Projectile
                {
                    Origin = turret.Cell.Center,
                    Target = aim,
                    TargetId = target.Id,
                    Damage = Economy.GetAttack(turret),
                    Kind = shot,
                    Duration = Math.Max(.12f, MathF.Sqrt(nearest) / (shot == ShotKind.Lob ? 5f : 16f))
                });
        }
    }

    private void UpdateZombie(Zombie zombie, float dt)
    {
        // Another zombie may have destroyed a support or obstruction earlier in
        // this same simulation slice. Do not select a new step from that old field.
        if (_navigationRevision != _topologyRevision || _navigationWorldRevision != World.Revision) RebuildNavigation();
        zombie.Cooldown = Math.Max(0, zombie.Cooldown - dt);
        UpdateKiloEquipment(zombie, dt);
        if (TryUpdateKiloFlight(zombie, dt)) return;
        if (TryFindBodyObstacle(zombie.Position, zombie.Position, out var enclosingSolid, zombie: zombie))
        {
            // A reaction can grow a thin crust through a living body. Break that
            // actual obstruction at the usual attack cadence before moving again.
            ClearZombieMovement(zombie);
            AttackObstacle(zombie, enclosingSolid);
            return;
        }
        // Workers can scoop at the lip of a pool, before buoyancy takes over a
        // wet step. Action cooldown never suspends an unsupported falling body.
        if (!IsZombieRetreating(zombie) && zombie.CarriesBucket && TryBucketAction(zombie)) return;
        if (UpdateBuoyancy(zombie, dt))
        {
            Falls.CancelZombie(zombie);
            _fallStartHeights.Remove(zombie.Id);
            return;
        }
        if (IsZombieRetreating(zombie) && TryCompleteRetreat(zombie, dt)) return;
        if (zombie.NextCell is { } movingTo)
        {
            if (!_stepOrigins.TryGetValue(zombie.Id, out var movingFrom))
                _stepOrigins[zombie.Id] = movingFrom = zombie.Cell;
            if (IsJumpStep(movingFrom, movingTo))
            {
                if (_jumpHeights.TryGetValue(zombie.Id, out var height) && HasZombieSupport(movingTo, zombie: zombie) && !IsZombieObstacle(movingTo, zombie: zombie))
                {
                    float progress = Math.Min(1, zombie.MoveProgress + dt * zombie.MovementSpeed / 2);
                    if (IsJumpArcClear(movingFrom, movingTo, height, zombie.MoveProgress, progress, zombie: zombie))
                    {
                        Falls.CancelZombie(zombie);
                        var position = JumpPosition(movingFrom, movingTo, height, progress, zombie: zombie);
                        zombie.FaceToward(position - zombie.Position);
                        zombie.Position = position;
                        zombie.MoveProgress = progress;
                        if (progress >= 1) ClearZombieMovement(zombie);
                        return;
                    }
                }
                // A changed landing or a new obstruction interrupts the flight at its
                // current position; ordinary falling handles the unsupported body.
                ClearZombieMovement(zombie);
            }
            else
            {
                var intermediate = StepClearance(movingFrom, movingTo);
                if (!IsValidStep(movingFrom, movingTo) || !HasZombieSupport(movingFrom, zombie: zombie) || !HasZombieSupport(movingTo, zombie: zombie) || IsZombieObstacle(movingTo, zombie: zombie) ||
                    (intermediate.HasValue && IsZombieObstacle(intermediate.Value, zombie: zombie)))
                {
                    ClearZombieMovement(zombie);
                }
                else
                {
                    Falls.CancelZombie(zombie);
                    var remaining = dt * zombie.MovementSpeed;
                    // Climb vertically before crossing the ledge; descend only after crossing
                    // above the lower destination. A diagonal chord would cut through its corner.
                    if (intermediate.HasValue && zombie.MoveProgress < 1)
                    {
                        if (!MoveToward(zombie, zombie.StandAt(intermediate.Value), ref remaining)) return;
                        zombie.MoveProgress = 1;
                    }
                    if (!MoveToward(zombie, zombie.StandAt(movingTo), ref remaining)) return;
                    zombie.NextCell = null;
                    _stepOrigins.Remove(zombie.Id);
                    return;
                }
            }
        }

        _stepOrigins.Remove(zombie.Id);
        _jumpHeights.Remove(zombie.Id);
        var current = zombie.Cell;
        if (!TrySolidFloor(zombie.Position, out var floorHeight, zombie: zombie))
        {
            DamageZombie(zombie, zombie.Hp);
            return;
        }
        if (zombie.Position.Y > floorHeight + .0001f)
        {
            // Retargeting a fall or pausing at an obstruction must not erase the
            // height already lost. Only landing or buoyancy ends the physical fall.
            _fallStartHeights.TryAdd(zombie.Id, zombie.Position.Y);
            // Slabs land bodies on their actual upper face; a side crust outside
            // the feet cannot suspend a unit over the remaining empty space.
            var landing = new Vector3(zombie.Position.X, floorHeight, zombie.Position.Z);
            var fallPosition = Falls.AdvanceZombieFall(zombie, landing, dt);
            var origin = zombie.Position;
            var samples = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(origin, fallPosition) / .05f));
            for (var sample = 1; sample <= samples; sample++)
            {
                var position = Vector3.Lerp(origin, fallPosition, sample / (float)samples);
                if (TryFindBodyObstacle(zombie.Position, position, out var fallBlocker, zombie: zombie))
                {
                    Falls.CancelZombie(zombie);
                    AttackObstacle(zombie, fallBlocker, position);
                    break;
                }
                if (!IsBodyClear(position, zombie: zombie)) { Falls.CancelZombie(zombie); break; }
                zombie.Position = position;
                // Detect buoyant liquid along the sweep, before a fast fall can
                // cross a shallow surface and apply ground-impact damage.
                if (TryBuoyantSurface(zombie, position, out _))
                {
                    Falls.CancelZombie(zombie);
                    _fallStartHeights.Remove(zombie.Id);
                    return;
                }
            }
            if (Math.Abs(zombie.Position.Y - floorHeight) <= .0001f) CompleteZombieFall(zombie, floorHeight);
            return;
        }
        Falls.CancelZombie(zombie);
        CompleteZombieFall(zombie, floorHeight);
        if (zombie.Hp <= 0) return;
        if (zombie.Position.Y < floorHeight - .0001f)
        {
            // A new block can fit below the collision body while its normal
            // standing height is slightly above us. Regain footing continuously
            // instead of falling into and attacking that new support.
            var remaining = dt * zombie.MovementSpeed;
            MoveToward(zombie, new Vector3(zombie.Position.X, floorHeight, zombie.Position.Z), ref remaining);
            return;
        }
        if (RecoverSupportedPosition(zombie, floorHeight, dt)) return;
        if (TryLeaveFractionalSupport(zombie)) return;
        bool retreating = IsZombieRetreating(zombie);
        if (retreating && TryCompleteRetreat(zombie, dt)) return;
        // Canonical idle carriers already acted together in UpdateKilos. A
        // genuinely supported fractional body retains its old late action when
        // no clear support recovery or shoreline departure was available.
        if (!retreating && Vector3.DistanceSquared(zombie.Position, zombie.StandAt(zombie.Cell)) > .000001f)
        {
            TryKiloAction(zombie, dt);
            TryMegaAction(zombie, dt);
        }
        var next = current;
        bool yieldingToBuilder = !retreating && TryGetBuilderYieldStep(zombie, out next);
        bool retreatStep = retreating && TryGetRetreatStep(zombie, current, out next);
        if (!retreating && !yieldingToBuilder && FaceNearbyCore(zombie))
        {
            if (zombie.Cooldown <= 0)
            {
                CoreHp -= zombie.Attack;
                if (zombie.Attack > 0) RecordZombieProgress(zombie);
                zombie.Cooldown = 1f;
            }
            return;
        }
        if (!retreating && !yieldingToBuilder && ShouldWaitInCover(zombie)) return;
        if (!retreatStep && !yieldingToBuilder && !TryGetBucketRoute(zombie, current, out next) && !TryGetCoverJoinStep(zombie, current, out next) &&
            !TryGetSizedRoute(zombie, current, out next, includeFallback: false) &&
            !(zombie.Size == ZombieSize.Standard && _routes[zombie.Lane % 4].TryGetValue(current, out next)) &&
            !(AvailableConstructionBlocks(zombie) > 0 && TryGetBridgeStep(current, out next) && CanUseSizedBridgeStep(zombie, current, next)) &&
            !TryGetSizedRoute(zombie, current, out next) &&
            !_spentRoutes.TryGetValue(current, out next))
        {
            // A collapse can temporarily isolate a node; terrain is still destructible.
            // Prefer horizontal exits so a waiting zombie does not dig out its own footing.
            foreach (var direction in Horizontal.Concat(new[] { new Cell(0, 1, 0) }))
                if (AttackObstacle(zombie, current + direction)) break;
            return;
        }
        // A spent builder waits at the best real reachable position until the
        // terrain changes; it cannot execute a route requiring another block.
        if (next == current) return;
        if (!HasZombieSupport(next, zombie: zombie))
        {
            TryBuildBridge(zombie, current, next);
            return;
        }
        if (IsJumpStep(current, next))
        {
            if (Vector3.DistanceSquared(zombie.Position, zombie.StandAt(current)) < .000001f && CanPlanJump(current, next, out var height, zombie: zombie))
            {
                Falls.CancelZombie(zombie);
                zombie.NextCell = next;
                zombie.MoveProgress = 0;
                _stepOrigins[zombie.Id] = current;
                _jumpHeights[zombie.Id] = height;
                zombie.FaceToward(zombie.StandAt(next) - zombie.Position);
            }
            return;
        }
        var clearance = StepClearance(current, next);
        // Large bodies may encounter head-height cover even when the foot cell
        // is empty. Breach the actual swept obstruction selected by navigation.
        var waypoint = zombie.StandAt(clearance ?? next);
        if (TryFindBodyObstacle(zombie.Position, waypoint, out var bodyBlocker, zombie: zombie) ||
            TryFindBodyObstacle(waypoint, zombie.StandAt(next), out bodyBlocker, zombie: zombie))
        {
            AttackObstacle(zombie, bodyBlocker);
            return;
        }
        if (clearance.HasValue && AttackObstacle(zombie, clearance.Value)) return;
        if (AttackObstacle(zombie, next)) return;
        if (IsValidStep(current, next) && HasZombieSupport(current, zombie: zombie) && HasZombieSupport(next, zombie: zombie) && !IsZombieObstacle(next, zombie: zombie) &&
            (!clearance.HasValue || !IsZombieObstacle(clearance.Value, zombie: zombie)))
        {
            zombie.NextCell = next;
            zombie.MoveProgress = 0;
            _stepOrigins[zombie.Id] = current;
            zombie.FaceToward(zombie.StandAt(next) - zombie.Position);
        }
    }

    private void CompleteZombieFall(Zombie zombie, float landingHeight)
    {
        if (!_fallStartHeights.Remove(zombie.Id, out var startHeight)) return;
        var fallenBlocks = MathF.Floor(startHeight - landingHeight + .0001f);
        if (fallenBlocks >= Balance.FallDamageMinimumBlocks)
            DamageZombie(zombie, (fallenBlocks - Balance.FallDamageMinimumBlocks + 1) * Balance.FallDamagePerBlock, DamageKind.Blunt);
    }

    private void UpdateLiquidExposure(Zombie zombie, float dt)
    {
        // Position is the body's center, half a block above its feet. Reaching
        // the breathing point counts as submerged, including an exact liquid surface.
        var head = zombie.Position + Vector3.UnitY * ((Balance.ZombieBreathingHeight - .5f) * zombie.BodyScale);
        if (Liquids.GetLiquidAt(head).HasValue || Liquids.GetLiquidAt(head - Vector3.UnitY * .00001f).HasValue)
        {
            zombie.AirRemaining = Math.Max(0, zombie.AirRemaining - dt);
            if (zombie.AirRemaining <= .0001f)
            {
                ApplyZombieDamage(zombie, zombie.BodyHp, DamageKind.Chemical);
                return;
            }
        }
        else zombie.AirRemaining = Balance.AirSeconds;

        var contact = Liquids.GetLiquidAt(zombie.Position - new Vector3(0, .45f * zombie.BodyScale, 0)) ?? Liquids.GetLiquidAt(zombie.Position);
        var chemical = contact == LiquidKind.Mercury ? Balance.ChemicalDamagePerSecond : 0;
        var thermal = contact == LiquidKind.Lava ? Balance.LavaDamagePerSecond : 0;
        // Exposure only reads the clouds. Damage and bucket spills happen after
        // this loop, so the live read-only span cannot be invalidated here.
        foreach (var hazard in Liquids.ActiveHazards)
        {
            var delta = Vector3.Abs(zombie.Position - hazard.Center.Center);
            var halfExtent = hazard.Radius + .5f;
            if (delta.X > halfExtent || delta.Y > halfExtent || delta.Z > halfExtent) continue;
            if (hazard.Kind == DamageKind.Chemical) chemical += hazard.Dps;
            else if (hazard.Kind == DamageKind.Thermal) thermal += hazard.Dps;
        }
        if (chemical > 0) ApplyZombieDamage(zombie, chemical * dt, DamageKind.Chemical);
        // Multiple simultaneous heat sources consume one second of protection per
        // second exposed, while their damage rates add after protection expires.
        if (thermal > 0 && zombie.Hp > 0)
            ApplyZombieDamage(zombie, thermal * zombie.ConsumeHeatProtection(dt), DamageKind.Thermal);
    }

    private bool UpdateBuoyancy(Zombie zombie, float dt)
    {
        // A dry battlefield needs neither surface probes nor floor searches.
        // Finish existing shoreline steps even if the last liquid disappeared.
        if (!Liquids.HasLiquidCells && !_liquidDestinations.ContainsKey(zombie.Id)) return false;
        // A newly buoyant body takes priority over any cached walking, descending,
        // or shoreline transition. Armor is evaluated from the surviving pieces.
        if (TryBuoyantSurface(zombie, zombie.Position, out var risingSurface) &&
            zombie.Position.Y < BuoyantStandHeight(zombie, zombie.Position, risingSurface) - .001f)
        {
            ClearZombieMovement(zombie);
            var rising = new Vector3(zombie.Position.X, Math.Min(BuoyantStandHeight(zombie, zombie.Position, risingSurface), zombie.Position.Y + dt * 2), zombie.Position.Z);
            if (TryFindBodyObstacle(zombie.Position, rising, out var ceiling, zombie: zombie)) AttackObstacle(zombie, ceiling, rising);
            else if (IsBodyClear(rising, zombie: zombie)) zombie.Position = rising;
            return true;
        }
        if (_liquidDestinations.TryGetValue(zombie.Id, out var target))
        {
            var origin = _liquidOrigins[zombie.Id];
            if ((!TryBuoyantSurface(zombie, origin, out _) && !HasSolidFooting(origin, zombie: zombie)) ||
                (!TryBuoyantSurface(zombie, target, out _) && !HasSolidFooting(target, zombie: zombie)))
            {
                _liquidDestinations.Remove(zombie.Id);
                _liquidOrigins.Remove(zombie.Id);
            }
            else
            {
                // As with dry ledges, ascend before crossing and descend only after
                // crossing. Every segment is swept against the current solid geometry.
                var waypoint = target.Y > origin.Y && zombie.Position.Y < target.Y - .001f
                    ? new Vector3(origin.X, target.Y, origin.Z)
                    : target.Y < origin.Y && (Math.Abs(zombie.Position.X - target.X) > .001f || Math.Abs(zombie.Position.Z - target.Z) > .001f)
                        ? new Vector3(target.X, origin.Y, target.Z) : target;
                var offset = waypoint - zombie.Position;
                var length = offset.Length();
                var next = length <= dt * zombie.MovementSpeed ? waypoint : zombie.Position + offset / length * (dt * zombie.MovementSpeed);
                if (TryFindBodyObstacle(zombie.Position, next, out var blocker, zombie: zombie))
                {
                    // A newly dropped block can invalidate the cached crossing.
                    // Resume real support recovery and route selection instead
                    // of retrying this same obstructed diagonal indefinitely.
                    ClearZombieMovement(zombie);
                    _buoyantSteps.Clear();
                    return TryBuoyantSurface(zombie, zombie.Position, out _);
                }
                if (!IsBodyClear(next, zombie: zombie)) return true;
                zombie.FaceToward(next - zombie.Position);
                zombie.Position = next;
                if (Vector3.DistanceSquared(zombie.Position, target) < .000001f)
                {
                    _liquidDestinations.Remove(zombie.Id);
                    _liquidOrigins.Remove(zombie.Id);
                }
                return true;
            }
        }
        if (!TryBuoyantSurface(zombie, zombie.Position, out var surface))
        {
            // A deep pool has no dry navigation node at the waterline. A light unit
            // can still enter it from a supported bank without stepping into thin air.
            if (!HasSolidFooting(zombie.Position, zombie: zombie)) return false;
            bool nearbySurface = false;
            foreach (var direction in Horizontal)
                if (TryBuoyantSurface(zombie, zombie.Position + new Vector3(direction.X, 0, direction.Z), out _))
                {
                    nearbySurface = true;
                    break;
                }
            if (!nearbySurface) return false;
            if (!zombie.NextCell.HasValue && TrySolidFloor(zombie.Position, out var dryFloor, zombie) &&
                RecoverSupportedPosition(zombie, dryFloor, dt)) return true;
            if (FindBuoyantRouteStep(zombie) is not { } entry) return false;
            zombie.NextCell = null;
            _stepOrigins.Remove(zombie.Id);
            _liquidOrigins[zombie.Id] = zombie.Position;
            _liquidDestinations[zombie.Id] = entry;
            zombie.FaceToward(entry - zombie.Position);
            return true;
        }
        zombie.NextCell = null;
        _stepOrigins.Remove(zombie.Id);
        var desiredY = BuoyantStandHeight(zombie, zombie.Position, surface);
        var vertical = new Vector3(zombie.Position.X, zombie.Position.Y + Math.Clamp(desiredY - zombie.Position.Y, -dt * 2, dt * 2), zombie.Position.Z);
        if (TryFindBodyObstacle(zombie.Position, vertical, out var surfaceBlocker, zombie: zombie))
        {
            AttackObstacle(zombie, surfaceBlocker, vertical);
            return true;
        }
        if (!IsBodyClear(vertical, zombie: zombie)) return true;
        zombie.Position = vertical;
        if (Math.Abs(zombie.Position.Y - desiredY) > .001f) return true;

        if (!IsZombieRetreating(zombie) && FaceNearbyCore(zombie))
        {
            if (zombie.Cooldown <= 0)
            {
                CoreHp -= zombie.Attack;
                if (zombie.Attack > 0) RecordZombieProgress(zombie);
                zombie.Cooldown = 1;
            }
            return true;
        }

        var destination = FindBuoyantRouteStep(zombie);
        if (destination.HasValue)
        {
            _liquidOrigins[zombie.Id] = zombie.Position;
            _liquidDestinations[zombie.Id] = destination.Value;
            zombie.FaceToward(destination.Value - zombie.Position);
        }
        else if (!TryAttackBuoyantRouteBlocker(zombie))
            foreach (var direction in Horizontal)
                if (AttackObstacle(zombie, zombie.Cell + direction)) break;
        return true;
    }

    private float BuoyantStandHeight(Zombie zombie, Vector3 position, float surface)
    {
        var height = surface + .4f * zombie.BodyScale;
        // A shallow puddle cannot pull a large body's lower face into its own
        // supporting floor. Use the same real support as ordinary walking.
        if (TrySolidFloor(position, out var floor, zombie)) height = Math.Max(height, floor);
        return height;
    }

    private bool TryAttackBuoyantRouteBlocker(Zombie zombie)
    {
        // A floating large body can be blocked above/beside its foot cell. Only
        // probe when no clear route exists, then breach the actual first solid
        // touched by a one-cell cardinal sweep at the usual attack cooldown.
        foreach (var direction in Horizontal)
        {
            var toward = zombie.Position + new Vector3(direction.X, 0, direction.Z);
            if (TryFindBodyObstacle(zombie.Position, toward, out var blocker, zombie: zombie) &&
                AttackObstacle(zombie, blocker, toward)) return true;
        }
        return false;
    }

    private void ClearZombieMovement(Zombie zombie)
    {
        Falls.CancelZombie(zombie);
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        _stepOrigins.Remove(zombie.Id);
        _jumpHeights.Remove(zombie.Id);
        _liquidDestinations.Remove(zombie.Id);
        _liquidOrigins.Remove(zombie.Id);
    }

    private static bool FaceNearbyCore(Zombie zombie)
    {
        if (zombie.IsGiant)
        {
            if (!IsCoreApproach(zombie.Cell, zombie)) return false;
            zombie.FaceToward(new Vector3(16, 16, 16) - zombie.Position);
            return true;
        }
        foreach (var direction in Adjacent)
        {
            var target = zombie.Cell + direction;
            if (!WorldGrid.IsCore(target)) continue;
            zombie.FaceToward(target.Center - zombie.Position);
            return true;
        }
        return false;
    }

    private Vector3? FindBuoyantRouteStep(Zombie zombie)
    {
        if (TryGetRetreatBuoyantStep(zombie, out var retreatStep)) return retreatStep;
        // Share successful and failed decisions at identical physical positions.
        // Timer-only liquid ticks must not make waiting crowds repeat the whole BFS.
        // Every geometry, occupancy, or buoyancy change invalidates the relevant result.
        if (_buoyantLiquidRevision != Liquids.Revision || _buoyantWorldRevision != World.Revision ||
            _buoyantTopologyRevision != _topologyRevision)
        {
            _buoyantSteps.Clear();
            _buoyantLiquidRevision = Liquids.Revision;
            _buoyantWorldRevision = World.Revision;
            _buoyantTopologyRevision = _topologyRevision;
        }
        var key = (zombie.Position, zombie.SinksIn(LiquidKind.Lava), zombie.SinksIn(LiquidKind.Mercury), zombie.Size);
        if (_buoyantSteps.TryGetValue(key, out var cached)) return cached;
        BuoyantRouteSearchCount++;
        var start = zombie.Cell;
        var positions = new Dictionary<Cell, Vector3> { [start] = zombie.Position };
        var previous = new Dictionary<Cell, Cell>();
        var frontier = new Queue<Cell>();
        frontier.Enqueue(start);
        var best = start;
        var bestDistance = Vector3.DistanceSquared(zombie.Position, new Vector3(16, 16, 16));
        while (frontier.TryDequeue(out var current))
        {
            if (IsCoreApproach(current, zombie)) { best = current; break; }
            foreach (var direction in Horizontal)
                foreach (var dy in new[] { 0, 1, -1 })
                {
                    var next = current + direction + new Cell(0, dy, 0);
                    if (positions.ContainsKey(next) || !WorldGrid.IsInside(next) || WorldGrid.IsCore(next)) continue;
                    Vector3? stand = null;
                    var nodePosition = zombie.StandAt(next);
                    if (TryBuoyantSurface(zombie, nodePosition, out var surface))
                    {
                        var afloat = new Vector3(nodePosition.X, BuoyantStandHeight(zombie, nodePosition, surface), nodePosition.Z);
                        if (zombie.CellAt(afloat) == next) stand = afloat;
                    }
                    if (!stand.HasValue && HasZombieSupport(next, zombie: zombie) && !IsZombieObstacle(next, zombie: zombie)) stand = nodePosition;
                    if (!stand.HasValue || Math.Abs(stand.Value.Y - positions[current].Y) > 1.001f || !IsBodyClear(stand.Value, zombie: zombie)) continue;
                    var crossingY = Math.Max(positions[current].Y, stand.Value.Y);
                    var aboveOrigin = new Vector3(positions[current].X, crossingY, positions[current].Z);
                    var aboveTarget = new Vector3(stand.Value.X, crossingY, stand.Value.Z);
                    if (!IsBodyClear(aboveOrigin, zombie: zombie) || !IsBodyClear(aboveTarget, zombie: zombie) ||
                        TryFindBodyObstacle(positions[current], aboveOrigin, out _, zombie: zombie) ||
                        TryFindBodyObstacle(aboveOrigin, aboveTarget, out _, zombie: zombie) ||
                        TryFindBodyObstacle(aboveTarget, stand.Value, out _, zombie: zombie)) continue;
                    positions[next] = stand.Value;
                    previous[next] = current;
                    frontier.Enqueue(next);
                    var distance = Vector3.DistanceSquared(stand.Value, new Vector3(16, 16, 16));
                    if (distance < bestDistance) { bestDistance = distance; best = next; }
                }
        }
        if (best == start) { _buoyantSteps[key] = null; return null; }
        while (previous[best] != start) best = previous[best];
        return _buoyantSteps[key] = positions[best];
    }

    private bool TryBuoyantSurface(Zombie zombie, Vector3 position, out float surface)
    {
        surface = float.NegativeInfinity;
        var column = ToCell(position);
        for (var y = Math.Max(0, column.Y - 1); y <= Math.Min(WorldGrid.Size - 1, column.Y + 1); y++)
        {
            var cell = new Cell(column.X, y, column.Z);
            // Empty cells have no surface; avoid creating a volume iterator for
            // each dry neighbor of every member of a crowd.
            if (Liquids.GetTotalUnits(cell) == 0) continue;
            foreach (var volume in Liquids.GetVolumes(cell))
            {
                if (zombie.SinksIn(volume.Kind) || position.X < volume.Min.X || position.X >= volume.Max.X ||
                    position.Z < volume.Min.Z || position.Z >= volume.Max.Z ||
                    position.Y - .45f * zombie.BodyScale > volume.Max.Y + .15f || position.Y + .4f * zombie.BodyScale < volume.Min.Y) continue;
                surface = Math.Max(surface, volume.Max.Y);
            }
        }
        return float.IsFinite(surface);
    }

    private bool HasSolidFooting(Vector3 position, Zombie? zombie = null) => TrySolidFloor(position, out var height, zombie) &&
        Math.Abs(position.Y - height) <= .011f;

    private bool TrySolidFloor(Vector3 position, out float height, Zombie? zombie = null)
    {
        // A body can land on a bank while its center is over the hole. Inspect
        // the whole footprint so the descent target agrees with body collision.
        height = float.NegativeInfinity;
        var body = zombie?.BodyHalfExtents ?? new Vector3(.2f, .39f, .2f);
        float standing = zombie?.StandingHeight ?? .5f;
        for (var x = Math.Max(0, (int)MathF.Floor(position.X - body.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(position.X + body.X) - 1); x++)
            for (var z = Math.Max(0, (int)MathF.Floor(position.Z - body.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(position.Z + body.Z) - 1); z++)
                for (var y = Math.Min((int)MathF.Floor(position.Y), WorldGrid.Size - 1); y >= 0; y--)
                {
                    var cell = new Cell(x, y, z);
                    float? top = null;
                    if (World.GetObsidian(cell) is { } crust)
                    {
                        foreach (var part in crust.Parts)
                            if (position.X + body.X > part.Min.X && position.X - body.X < part.Max.X &&
                                position.Z + body.Z > part.Min.Z && position.Z - body.Z < part.Max.Z &&
                                part.Max.Y + body.Y <= position.Y + .00001f)
                                top = Math.Max(top ?? float.NegativeInfinity, part.Max.Y);
                    }
                    else if (World.GetMaterial(cell) != Material.Air || TrySelectTurret(cell) != null) top = y + 1;
                    // Use the collision body's lower face, not its half-block
                    // standing offset: death drops may appear in that small gap.
                    if (!top.HasValue || top.Value + body.Y > position.Y + .00001f) continue;
                    height = Math.Max(height, top.Value + standing);
                    break;
                }
        return float.IsFinite(height);
    }

    private bool RecoverSupportedPosition(Zombie zombie, float floorHeight, float dt)
    {
        var currentCenter = zombie.StandAt(zombie.Cell);
        if (Math.Abs(zombie.Position.X - currentCenter.X) < .00001f &&
            Math.Abs(zombie.Position.Z - currentCenter.Z) < .00001f) return false;

        // An interrupted traversal may finish with only its edge on the bank,
        // outside any route node. Walk back onto that real support; never snap
        // to a grid center or restart a jump from an unsupported launch point.
        Vector3? destination = null;
        var distance = float.PositiveInfinity;
        for (var x = Math.Max(0, (int)MathF.Floor(zombie.Position.X - zombie.BodyHalfExtents.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(zombie.Position.X + zombie.BodyHalfExtents.X) - 1); x++)
            for (var z = Math.Max(0, (int)MathF.Floor(zombie.Position.Z - zombie.BodyHalfExtents.X)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Ceiling(zombie.Position.Z + zombie.BodyHalfExtents.X) - 1); z++)
            {
                var standing = zombie.StandAt(new Cell(x, zombie.Cell.Y, z));
                var candidate = new Vector3(standing.X, floorHeight, standing.Z);
                var foot = candidate - Vector3.UnitY * (zombie.StandingHeight + .01f);
                if ((!World.IsSolidAt(foot) && TrySelectTurret(ToCell(foot)) == null) || !IsBodyClear(candidate, zombie: zombie)) continue;
                var candidateDistance = Vector3.DistanceSquared(zombie.Position, candidate);
                if (candidateDistance >= distance) continue;
                var samples = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(candidateDistance) / .05f));
                bool clear = true;
                for (var sample = 1; sample <= samples; sample++)
                {
                    var point = Vector3.Lerp(zombie.Position, candidate, sample / (float)samples);
                    if (!IsBodyClear(point, zombie: zombie) || !TrySolidFloor(point, out var supportingHeight, zombie: zombie) ||
                        Math.Abs(supportingHeight - floorHeight) > .001f) { clear = false; break; }
                }
                if (!clear) continue;
                destination = candidate;
                distance = candidateDistance;
            }
        if (!destination.HasValue) return false;
        var remaining = dt * zombie.MovementSpeed;
        MoveToward(zombie, destination.Value, ref remaining);
        return true;
    }

    private bool IsBodyClear(Vector3 position, Zombie? zombie = null)
    {
        var body = zombie?.BodyHalfExtents ?? new Vector3(.2f, .39f, .2f);
        var min = position - body;
        var max = position + body;
        return min.X >= 0 && min.Y >= 0 && min.Z >= 0 && max.X <= WorldGrid.Size && max.Y <= WorldGrid.Size && max.Z <= WorldGrid.Size &&
            !TryFindBodyObstacle(position, position, out _, zombie: zombie);
    }

    private bool TryFindBodyObstacle(Vector3 from, Vector3 to, out Cell obstacle, HashSet<Cell>? candidates = null, Zombie? zombie = null)
    {
        // Slabs can lie entirely between the eight body corners. Sweep the full
        // body bounds against each actual solid volume, including internal crusts.
        var body = zombie?.BodyHalfExtents ?? new Vector3(.2f, .39f, .2f);
        var boundsMin = Vector3.Min(from, to) - body;
        var boundsMax = Vector3.Max(from, to) + body;
        var firstContact = float.PositiveInfinity;
        obstacle = default;
        for (int x = Math.Max(0, (int)MathF.Floor(boundsMin.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(boundsMax.X)); x++)
            for (int y = Math.Max(0, (int)MathF.Floor(boundsMin.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(boundsMax.Y)); y++)
                for (int z = Math.Max(0, (int)MathF.Floor(boundsMin.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(boundsMax.Z)); z++)
                {
                    var cell = new Cell(x, y, z);
                    if (candidates != null && !candidates.Contains(cell)) continue;
                    if (World.GetMaterial(cell) == Material.Air && !HasLiveIndexedTurret(cell)) continue;
                    foreach (var bounds in SolidCollisionBounds(cell))
                    {
                        var min = bounds.Min - body;
                        var max = bounds.Max + body;
                        float enter = 0, leave = 1;
                        var movement = to - from;
                        if (!JumpAxisInterval(from.X, movement.X, min.X + .00001f, max.X - .00001f, ref enter, ref leave) ||
                            !JumpAxisInterval(from.Y, movement.Y, min.Y + .00001f, max.Y - .00001f, ref enter, ref leave) ||
                            !JumpAxisInterval(from.Z, movement.Z, min.Z + .00001f, max.Z - .00001f, ref enter, ref leave) || enter >= firstContact) continue;
                        firstContact = enter;
                        obstacle = cell;
                    }
                }
        return float.IsFinite(firstContact);
    }

    // Body/route sweeps visit millions of ordinary cubes. Pattern-based value
    // enumeration keeps their exact geometry without allocating an iterator
    // for every tested solid, including both leaves of a paired crust.
    private SolidBounds SolidCollisionBounds(Cell cell) => new(cell, World.GetObsidian(cell));

    private readonly struct SolidBounds(Cell cell, ObsidianCell? crust)
    {
        public Enumerator GetEnumerator() => new(cell, crust);

        public struct Enumerator(Cell cell, ObsidianCell? crust)
        {
            private int _index = -1;
            public bool MoveNext() => ++_index < (crust?.OppositeUnits > 0 ? 2 : 1);
            public (Vector3 Min, Vector3 Max) Current
            {
                get
                {
                    if (crust == null)
                    {
                        var min = new Vector3(cell.X, cell.Y, cell.Z);
                        return (min, min + Vector3.One);
                    }
                    if (_index == 0) return (crust.Min, crust.Max);
                    var face = ObsidianCell.OppositeFace(crust.Face);
                    float width = crust.OppositeUnits / 10f;
                    return (new Vector3(cell.X + (face == CrustFace.East ? 1 - width : 0),
                            cell.Y, cell.Z + (face == CrustFace.South ? 1 - width : 0)),
                        new Vector3(cell.X + (face == CrustFace.West ? width : 1),
                            cell.Y + 1, cell.Z + (face == CrustFace.North ? width : 1)));
                }
            }
        }
    }

    private bool TryLeaveFractionalSupport(Zombie zombie)
    {
        if (Math.Abs(zombie.Position.Y - zombie.StandAt(zombie.Cell).Y) <= .001f && HasZombieSupport(zombie.Cell, zombie: zombie)) return false;
        bool retreating = IsZombieRetreating(zombie);
        Vector3? destination = null;
        var bestDistance = float.PositiveInfinity;
        foreach (var direction in Horizontal)
            foreach (var dy in new[] { 0, -1, 1 })
            {
                var next = zombie.Cell + direction + new Cell(0, dy, 0);
                if (!HasZombieSupport(next, zombie: zombie) || IsZombieObstacle(next, zombie: zombie)) continue;
                if (retreating)
                {
                    if (!TryGetRetreatStep(zombie, next, out var outward) ||
                        outward == next && !AtSupplyBoundary(zombie, zombie.StandAt(next))) continue;
                }
                else if (!_routes[zombie.Lane % 4].ContainsKey(next) && !TryGetBridgeStep(next, out _)) continue;
                var candidate = zombie.StandAt(next);
                if (Math.Abs(candidate.Y - zombie.Position.Y) > 1.001f || !IsBodyClear(candidate, zombie: zombie)) continue;
                var crossingY = Math.Max(zombie.Position.Y, candidate.Y);
                var aboveOrigin = new Vector3(zombie.Position.X, crossingY, zombie.Position.Z);
                var aboveTarget = new Vector3(candidate.X, crossingY, candidate.Z);
                if (TryFindBodyObstacle(zombie.Position, aboveOrigin, out _, zombie: zombie) ||
                    TryFindBodyObstacle(aboveOrigin, aboveTarget, out _, zombie: zombie) || TryFindBodyObstacle(aboveTarget, candidate, out _, zombie: zombie)) continue;
                var distance = retreating ? RetreatEdgeDistance(zombie, candidate) : Vector3.DistanceSquared(candidate, new Vector3(16, 16, 16));
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                destination = candidate;
            }
        if (!destination.HasValue) return false;
        // Reuse the physical climb/cross/descend movement used by shoreline steps.
        // The starting slab is valid footing even when its top is between grid levels.
        _liquidOrigins[zombie.Id] = zombie.Position;
        _liquidDestinations[zombie.Id] = destination.Value;
        zombie.FaceToward(destination.Value - zombie.Position);
        return true;
    }

    private bool MoveToward(Zombie zombie, Vector3 destination, ref float remaining)
    {
        var offset = destination - zombie.Position;
        var distance = offset.Length();
        var next = distance <= remaining || distance <= .000001f ? destination :
            remaining > 0 ? zombie.Position + offset / distance * remaining : zombie.Position;
        if (TryFindBodyObstacle(zombie.Position, next, out var blocker, zombie: zombie))
        {
            AttackObstacle(zombie, blocker, next);
            remaining = 0;
            return false;
        }
        if (remaining > 0) zombie.FaceToward(offset);
        if (distance <= remaining)
        {
            zombie.Position = destination;
            remaining -= distance;
            return true;
        }
        if (remaining > 0) zombie.Position += offset / distance * remaining;
        remaining = 0;
        return false;
    }

    private bool AttackObstacle(Zombie zombie, Cell cell, Vector3? contactPosition = null)
    {
        var barrier = World.GetBarrier(cell);
        var turret = TrySelectTurret(cell);
        var terrain = World.GetMaterial(cell);
        var breakableTerrain = Balance.IsTerrainMaterial(terrain);
        var obsidian = terrain == Material.Obsidian;
        if (barrier == null && turret == null && !breakableTerrain && !obsidian) return false;
        zombie.FaceToward(cell.Center - zombie.Position);
        if (zombie.Cooldown > 0) return true;
        zombie.Cooldown = 1f;
        if (zombie.Attack > 0) RecordZombieProgress(zombie);
        if (barrier != null)
        {
            barrier.Hp -= zombie.Attack;
            if (barrier.Hp <= 0)
            {
                World.RemoveBarrier(cell);
                SettleDefenses();
            }
        }
        else if (turret != null)
        {
            turret.Hp -= zombie.Attack;
            if (turret.Hp <= 0)
            {
                Turrets.Remove(turret);
                _turretsByCell.Remove(turret.Cell);
                _topologyRevision++;
                SettleDefenses();
            }
        }
        else if (breakableTerrain && World.DamageTerrain(cell, zombie.Attack))
            SettleDefenses();
        else if (obsidian && World.DamageObsidianPart(cell, World.GetObsidian(cell)!.Parts
                     .MinBy(part => Vector3.DistanceSquared(contactPosition ?? zombie.Position,
                         Vector3.Clamp(contactPosition ?? zombie.Position, part.Min, part.Max)))!.Face,
                     zombie.Attack))
        {
            ShatterObsidian(cell);
            SettleDefenses();
        }
        return true;
    }

    private void ShatterObsidian(Cell cell)
    {
        const float damage = 20;
        var center = cell.Center;
        var min = center - new Vector3(1.5f);
        var max = center + new Vector3(1.5f);
        foreach (var target in Zombies)
        {
            if (target.Hp <= 0) continue;
            var halfBody = target.BodyHalfExtents;
            var bodyMin = target.Position - halfBody;
            var bodyMax = target.Position + halfBody;
            if (bodyMax.X <= min.X || bodyMin.X >= max.X || bodyMax.Y <= min.Y || bodyMin.Y >= max.Y ||
                bodyMax.Z <= min.Z || bodyMin.Z >= max.Z) continue;
            // Shards travel from the broken cell into each body. A body at the
            // source has no incident direction, so its shield has no coverage.
            DamageZombie(target, damage, DamageKind.Pierce, ShotKind.Pellet, target.Position - center);
        }
    }

    private void SettleDefenses()
    {
        // Health is public state and can be changed outside AttackObstacle. Retire
        // every dead piece before trusting the topology/settlement revision cache.
        if (Turrets.RemoveAll(turret => turret.Hp <= 0) > 0) _topologyRevision++;
        Falls.Prune(World, Turrets);
        if (_settledWorldRevision == World.Revision && _settledTopologyRevision == _topologyRevision) return;
        // Blocks retain holes, overhangs, and ceilings whenever any orthogonal block
        // occupies a neighboring cell. Towers rely only on vertical support and settle bottom first.
        var movedSolids = new HashSet<Cell>();
        var movedCrusts = new List<ObsidianCell>();
        bool moved;
        do
        {
            moved = SettleUnsupportedObsidianStacks(movedSolids, movedCrusts);
            foreach (var origin in World.MovableBlocks().ToArray())
            {
                if (World.GetObsidian(origin) is { Units: < LiquidSystem.UnitsPerBlock }) continue;
                var cell = origin;
                while (cell.Y > 0 && !World.HasOrthogonalBlockSupport(cell))
                {
                    var below = cell + new Cell(0, -1, 0);
                    if (TrySelectTurret(below) != null || !World.MoveBlock(cell, below)) break;
                    Liquids.SwapDisplacedLiquid(cell, below);
                    cell = below;
                    moved = true;
                }
                Falls.RecordBlockDrop(origin, cell, World);
                if (origin != cell) movedSolids.Add(cell);
            }
            foreach (var turret in Turrets.Where(t => t.Hp > 0).OrderBy(t => t.Cell.Y).ThenBy(t => t.Id).ToArray())
            {
                var origin = turret.Cell;
                while (turret.Cell.Y > 0)
                {
                    var below = turret.Cell + new Cell(0, -1, 0);
                    if (World.GetMaterial(below) != Material.Air || TrySelectTurret(below) != null) break;
                    Liquids.SwapDisplacedLiquid(turret.Cell, below);
                    turret.Cell = below;
                    _topologyRevision++;
                    moved = true;
                }
                Falls.RecordTurretDrop(turret, origin);
                if (origin != turret.Cell) movedSolids.Add(turret.Cell);
            }
        } while (moved);
        _turretsByCell.Clear();
        foreach (var turret in Turrets)
            if (turret.Hp > 0) _turretsByCell[turret.Cell] = turret;
        // Preserve crushing by solids that actually fell. A stationary crust
        // growing around a unit is an obstruction it can attack, not an instant kill.
        foreach (var zombie in Zombies)
            if (zombie.Hp > 0 &&
                (movedSolids.Count > 0 && TryFindBodyObstacle(zombie.Position, zombie.Position, out _, movedSolids, zombie) ||
                 movedCrusts.Any(part => World.GetObsidian(part.Cell)?.Parts.Any(live => live.Face == part.Face) == true &&
                     BoundsOverlap(part.Min, part.Max, zombie.Position - zombie.BodyHalfExtents, zombie.Position + zombie.BodyHalfExtents))))
                DamageZombie(zombie, zombie.Hp);
        _settledWorldRevision = World.Revision;
        _settledTopologyRevision = _topologyRevision;
    }

    private void RebuildNavigation()
    {
        _turretsByCell.Clear();
        foreach (var turret in Turrets)
            if (turret.Hp > 0) _turretsByCell[turret.Cell] = turret;
        const int edgesPerNode = 16;
        // Rows are one-based so a zero lookup means no node. Keep the original
        // x/y/z insertion order: it also determines equal-cost goal tie ordering.
        var workspace = _navigationWorkspace ??= new NavigationWorkspace();
        var nodeRows = workspace.Rows;
        var clearNodes = workspace.ClearNodes;
        var nodes = workspace.Nodes;
        var goals = workspace.Goals;
        Array.Clear(nodeRows);
        Array.Clear(clearNodes);
        nodes.Clear();
        goals.Clear();
        static int Index(Cell cell) => (cell.X * WorldGrid.Size + cell.Y) * WorldGrid.Size + cell.Z;
        bool IsNode(Cell cell) => WorldGrid.IsInside(cell) && nodeRows[Index(cell)] != 0;
        for (var x = 0; x < WorldGrid.Size; x++)
            for (var y = 1; y < WorldGrid.Size; y++)
                for (var z = 0; z < WorldGrid.Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (!HasZombieSupport(cell) || World.GetMaterial(cell) == Material.Core) continue;
                    nodeRows[Index(cell)] = nodes.Count + 1;
                    clearNodes[Index(cell)] = World.GetMaterial(cell) == Material.Air && !HasLiveIndexedTurret(cell);
                    nodes.Add(cell);
                    foreach (var direction in Adjacent)
                        if (WorldGrid.IsCore(cell + direction)) { goals.Add(cell); break; }
                }
        // All lanes use the same physical edges. Evaluate clearance and breach
        // costs once, using fixed slots instead of a dictionary/list per cell.
        // Slots retain Horizontal/dy/jump order from the original incoming graph.
        var edgeCosts = workspace.Edges;
        Array.Fill(edgeCosts, float.NaN, 0, nodes.Count * edgesPerNode);
        var edgeOffsets = workspace.EdgeOffsets;
        int offsetSlot = 0;
        foreach (var horizontal in Horizontal)
        {
            for (int dy = -1; dy <= 1; dy++) edgeOffsets[offsetSlot++] = Index(horizontal + new Cell(0, dy, 0));
            edgeOffsets[offsetSlot++] = Index(horizontal + horizontal);
        }
        var breachCosts = workspace.Breaches;
        Array.Fill(breachCosts, float.NaN);
        // Four approach fields remain shared. The strongest configured zombie is
        // an optimistic level-wide digging estimate, not per-unit risk learning.
        var referenceAttack = Level.Waves.SelectMany(wave => wave.Groups)
            .Select(group => LevelCatalog.ZombieAttack(group.Kind, group.Tier)).DefaultIfEmpty(Balance.BaseAttack).Max();
        float CachedBreachCost(Cell cell)
        {
            ref var cost = ref breachCosts[Index(cell)];
            if (float.IsNaN(cost)) cost = BreachCost(cell, referenceAttack);
            return cost;
        }
        for (int row = 0; row < nodes.Count; row++)
        {
            var cell = nodes[row];
            int slot = row * edgesPerNode;
            foreach (var horizontal in Horizontal)
            {
                for (var dy = -1; dy <= 1; dy++, slot++)
                {
                    var previous = cell + horizontal + new Cell(0, dy, 0);
                    // Node membership already guarantees both bounds, support,
                    // and a non-core destination; these offsets are valid steps.
                    if (!IsNode(previous)) continue;
                    var clearance = StepClearance(previous, cell);
                    if (clearance.HasValue && World.GetMaterial(clearance.Value) == Material.Core) continue;
                    edgeCosts[slot] = (cell.Y == previous.Y ? 1f : 2f) + CachedBreachCost(cell) +
                        (clearance.HasValue ? CachedBreachCost(clearance.Value) : 0f);
                }
                var launch = cell + horizontal + horizontal;
                // Solid underground nodes are valid breach paths, but can never
                // launch or receive a jump. Avoid repeating full support queries
                // for every such node while retaining the exact arc check.
                if (clearNodes[Index(cell)] && IsNode(launch) && clearNodes[Index(launch)] && CanPlanJump(launch, cell, out _))
                    // Two metres at the same horizontal speed as walking.
                    edgeCosts[slot] = 2f;
                slot++;
            }
        }
        // Lanes share immutable edge geometry, but have independent queues and
        // results. Finish all four before publishing a completed navigation
        // revision or letting any zombie act on the new terrain.
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, BuildLane);
        void BuildLane(int lane)
        {
            var laneWorkspace = workspace.Lanes[lane];
            var costs = laneWorkspace.Costs;
            var nextIndices = laneWorkspace.Next;
            var routeOrder = laneWorkspace.Order;
            var frontier = laneWorkspace.Frontier;
            _routes[lane].Clear();
            Array.Fill(costs, float.NaN);
            Array.Fill(nextIndices, -1);
            routeOrder.Clear();
            frontier.Clear();
            foreach (var goal in goals)
            {
                var preferred = lane switch { 0 => goal.X == 14, 1 => goal.X == 17, 2 => goal.Z == 14, _ => goal.Z == 17 };
                var cost = preferred ? 0f : 8f;
                int index = Index(goal);
                costs[index] = cost;
                frontier.Enqueue(index, cost);
            }
            while (frontier.TryDequeue(out var index, out var distance))
            {
                if (distance > costs[index]) continue;
                int edgeStart = (nodeRows[index] - 1) * edgesPerNode;
                for (int slot = 0; slot < edgesPerNode; slot++)
                {
                    float edgeCost = edgeCosts[edgeStart + slot];
                    if (float.IsNaN(edgeCost)) continue;
                    int previous = index + edgeOffsets[slot];
                    var cost = distance + edgeCost;
                    var known = costs[previous];
                    if (!float.IsNaN(known) && known <= cost) continue;
                    costs[previous] = cost;
                    if (nextIndices[previous] < 0) routeOrder.Add(previous);
                    nextIndices[previous] = index;
                    frontier.Enqueue(previous, cost);
                }
            }
            // Publish once per reached cell, preserving first-discovery insertion
            // order and avoiding dictionary writes during every relaxation.
            foreach (var previous in routeOrder)
                _routes[lane][nodes[nodeRows[previous] - 1]] = nodes[nodeRows[nextIndices[previous]] - 1];
        }
        BuildSpentRoutes(nodes, nodeRows, edgeCosts, edgeOffsets, edgesPerNode);
        _navigationRevision = _topologyRevision;
        _navigationWorldRevision = World.Revision;
    }

    private static bool IsValidStep(Cell from, Cell to)
    {
        var delta = to - from;
        return WorldGrid.IsInside(from) && WorldGrid.IsInside(to) &&
            Math.Abs(delta.X) + Math.Abs(delta.Z) == 1 && Math.Abs(delta.Y) <= 1;
    }

    private static Cell? StepClearance(Cell from, Cell to) => to.Y > from.Y
        ? from + new Cell(0, 1, 0) : to.Y < from.Y ? to + new Cell(0, 1, 0) : null;

    private static bool IsJumpStep(Cell from, Cell to)
    {
        var delta = to - from;
        return WorldGrid.IsInside(from) && WorldGrid.IsInside(to) && delta.Y == 0 &&
            (Math.Abs(delta.X) == 2 && delta.Z == 0 || Math.Abs(delta.Z) == 2 && delta.X == 0);
    }

    private bool CanPlanJump(Cell from, Cell to, out float height, Zombie? zombie = null)
    {
        height = 0;
        if (!IsJumpStep(from, to) || !HasZombieSupport(from, zombie: zombie) || !HasZombieSupport(to, zombie: zombie) ||
            IsZombieObstacle(from, zombie: zombie) || IsZombieObstacle(to, zombie: zombie)) return false;
        var middle = new Cell((from.X + to.X) / 2, from.Y, (from.Z + to.Z) / 2);
        if (HasLiveIndexedTurret(middle)) height = 2f;
        else if (!IsZombieObstacle(middle, zombie: zombie) && !HasZombieSupport(middle, zombie: zombie)) height = 1f;
        else return false;
        return IsJumpArcClear(from, to, height, 0, 1, zombie: zombie);
    }

    private static Vector3 JumpPosition(Cell from, Cell to, float height, float progress, Zombie? zombie = null) =>
        Vector3.Lerp(zombie?.StandAt(from) ?? from.Center, zombie?.StandAt(to) ?? to.Center, progress) +
        Vector3.UnitY * (4 * height * progress * (1 - progress));

    private bool IsJumpArcClear(Cell from, Cell to, float height, float start, float end, Zombie? zombie = null)
    {
        // Sweep the whole body through the parabola. Testing only frame endpoints or
        // body corners could skip a thin crust, a tower edge, or a low ceiling.
        var body = zombie?.BodyHalfExtents ?? new Vector3(.2f, .39f, .2f);
        var first = JumpPosition(from, to, height, start, zombie: zombie);
        var last = JumpPosition(from, to, height, end, zombie: zombie);
        var boundsMin = Vector3.Min(first, last) - body;
        var boundsMax = Vector3.Max(first, last) + body;
        if (start <= .5f && end >= .5f) boundsMax.Y = (zombie?.StandAt(from) ?? from.Center).Y + height + body.Y;
        if (boundsMin.X < 0 || boundsMin.Y < 0 || boundsMin.Z < 0 ||
            boundsMax.X > WorldGrid.Size || boundsMax.Y > WorldGrid.Size || boundsMax.Z > WorldGrid.Size) return false;
        for (int x = (int)MathF.Floor(boundsMin.X); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(boundsMax.X)); x++)
            for (int y = (int)MathF.Floor(boundsMin.Y); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(boundsMax.Y)); y++)
                for (int z = (int)MathF.Floor(boundsMin.Z); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(boundsMax.Z)); z++)
                {
                    var cell = new Cell(x, y, z);
                    var material = World.GetMaterial(cell);
                    if (material == Material.Air && !HasLiveIndexedTurret(cell)) continue;
                    foreach (var bounds in SolidCollisionBounds(cell))
                    {
                        var min = bounds.Min - body;
                        var max = bounds.Max + body;
                        float a = start, b = end;
                        if (!JumpAxisInterval(from.Center.X, to.X - from.X, min.X, max.X, ref a, ref b) ||
                            !JumpAxisInterval(from.Center.Z, to.Z - from.Z, min.Z, max.Z, ref a, ref b)) continue;
                        float ya = JumpPosition(from, to, height, a, zombie: zombie).Y, yb = JumpPosition(from, to, height, b, zombie: zombie).Y;
                        float lowest = Math.Min(ya, yb), highest = Math.Max(ya, yb);
                        if (a <= .5f && b >= .5f) highest = (zombie?.StandAt(from) ?? from.Center).Y + height;
                        if (lowest < max.Y - .00001f && highest > min.Y + .00001f) return false;
                    }
                }
        return true;
    }

    private static bool JumpAxisInterval(float origin, float delta, float min, float max, ref float a, ref float b)
    {
        if (delta == 0) return origin > min && origin < max;
        float first = (min - origin) / delta, last = (max - origin) / delta;
        a = Math.Max(a, Math.Min(first, last));
        b = Math.Min(b, Math.Max(first, last));
        return a <= b;
    }

    private bool CanPlanStep(Cell from, Cell to)
    {
        if (!IsValidStep(from, to) || World.GetMaterial(to) == Material.Core) return false;
        var clearance = StepClearance(from, to);
        // Dirt, stone, placed blocks, and pieces can be breached. The objective
        // remains an attack goal, never a body-occupiable navigation node.
        return !clearance.HasValue || (WorldGrid.IsInside(clearance.Value) && World.GetMaterial(clearance.Value) != Material.Core);
    }

    private float BreachCost(Cell cell, int referenceAttack)
    {
        var hp = World.GetBarrier(cell)?.Hp ?? World.GetObsidian(cell)?.Hp ?? World.GetTerrainHp(cell);
        if (_turretsByCell.TryGetValue(cell, out var turret)) hp += turret.Hp;
        // Destination blocks and required headroom are charged independently by
        // the edge builder. All finite breaches remain available when needed.
        return NavigationCosts.Breach(hp, referenceAttack);
    }

    private List<Cell> FindSpawnCells()
    {
        var cells = new List<Cell>();
        for (var x = 0; x < WorldGrid.Size; x++)
            for (var z = 0; z < WorldGrid.Size; z++)
                for (var y = 1; y < WorldGrid.Size; y++)
                {
                    var cell = new Cell(x, y, z);
                    if (WorldGrid.IsSpawnZone(cell) && World.HasSupport(cell) && IsEmpty(cell)) cells.Add(cell);
                }
        return cells;
    }

    private bool HasLineOfSight(Vector3 from, Vector3 to)
    {
        var count = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to) / .1f));
        for (var i = 1; i < count; i++)
        {
            if (IsShotObstacle(Vector3.Lerp(from, to, i / (float)count))) return false;
        }
        return true;
    }

    private bool IsShotObstacle(Vector3 point) => World.IsSolidAt(point);
    private bool HasLiveIndexedTurret(Cell cell) => _turretsByCell.TryGetValue(cell, out var turret) && turret.Hp > 0 && turret.Cell == cell;
    private bool IsZombieObstacle(Cell cell, Zombie? zombie = null) => zombie == null
        ? World.GetMaterial(cell) != Material.Air || HasLiveIndexedTurret(cell)
        : !IsBodyClear(zombie.StandAt(cell), zombie);
    private bool HasZombieSupport(Cell cell, Zombie? zombie = null) => zombie?.IsGiant == true
        ? HasGiantGridSupport(cell, zombie.GridFootprint) : zombie?.Size == ZombieSize.Milli
        ? HasMilliGridSupport(cell) : zombie != null
        ? WorldGrid.IsInside(cell) && TrySolidFloor(zombie.StandAt(cell), out var height, zombie) && Math.Abs(height - zombie.StandAt(cell).Y) <= .011f
        : WorldGrid.IsInside(cell) && (World.IsSolidAt(cell.Center - new Vector3(0, .51f, 0)) ||
            cell.Y > 0 && HasLiveIndexedTurret(cell + new Cell(0, -1, 0)));
    private bool IsEmpty(Cell cell) => World.GetMaterial(cell) == Material.Air && TrySelectTurret(cell) == null && Liquids.GetTotalUnits(cell) == 0;
    private bool CanUseTowers() => CanInteractWithTowers || Reject("Retry the level before interacting with towers.");
    private bool CanBuild() => Phase == GamePhase.Building || Reject("Terrain changes and supply purchases are available between levels.");
    private bool Reject(string message) { Message = message; return false; }
    private static Cell ToCell(Vector3 position) => new((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), (int)MathF.Floor(position.Z));
}
