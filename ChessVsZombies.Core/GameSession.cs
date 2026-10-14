using System.Numerics;

namespace ChessVsZombies.Core;

/// <summary>Rendering-independent building, wave scheduling, navigation, and combat.</summary>
public sealed class GameSession
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
    private readonly Dictionary<int, Vector3> _liquidDestinations = [];
    private readonly Dictionary<int, Vector3> _liquidOrigins = [];
    private readonly Dictionary<LiquidKind, int> _liquidInventory = Enum.GetValues<LiquidKind>().ToDictionary(kind => kind, _ => 0);
    private List<Cell> _spawnCells = [];

    public WorldGrid World { get; private set; } = new();
    public LiquidSystem Liquids { get; private set; }
    public IReadOnlyDictionary<LiquidKind, int> LiquidInventory => _liquidInventory;
    public Economy Economy { get; }
    public BlockInventory Inventory { get; } = new();
    public List<Turret> Turrets { get; } = [];
    public List<Zombie> Zombies { get; } = [];
    public List<Projectile> Projectiles { get; } = [];
    public List<WaveState> Waves { get; } = [];
    public float CoreHp { get; private set; } = Balance.CoreHp;
    public int LevelNumber { get; private set; } = 1;
    public LevelDefinition Level { get; private set; }
    public GamePhase Phase { get; private set; } = GamePhase.Building;
    public string Message { get; private set; } = "Build defenses, collect upgrades, then start the level.";
    public string MigrationNotice { get; internal set; } = string.Empty;

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
        Turrets.Clear();
        Zombies.Clear();
        Projectiles.Clear();
        Waves.Clear();
        _turretsByCell.Clear();
        _stepOrigins.Clear();
        _liquidDestinations.Clear();
        _liquidOrigins.Clear();
        _spawnCells.Clear();
        _random = new Random(_seed);
        _nextId = 1;
        _topologyRevision++;
        _navigationRevision = -1;
        LevelNumber = 1;
        Level = _levels.Get(1);
        CoreHp = Balance.CoreHp;
        Phase = GamePhase.Building;
        Message = "New defense: 1000 currency. Place your pieces and protect the core.";
        MigrationNotice = string.Empty;
    }

    public Turret? TrySelectTurret(Cell cell) => Turrets.FirstOrDefault(turret => turret.Cell == cell && turret.Hp > 0);
    public bool HasTowerSupport(Cell cell) => WorldGrid.IsInside(cell) && cell.Y > 0 &&
        (World.GetMaterial(cell + new Cell(0, -1, 0)) != Material.Air || TrySelectTurret(cell + new Cell(0, -1, 0)) != null);

    public bool ApplyUpgradeTemplate(Cell cell, UpgradeTemplate template)
    {
        if (!CanBuild()) return false;
        var turret = TrySelectTurret(cell);
        if (turret == null) return Reject("Aim at a living deployed piece to apply the upgrade template.");
        if (!Economy.TryApplyTemplate(turret, template, out var error)) return Reject(error);
        Message = Economy.LastMessage;
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
        return new CampaignState
        {
            LevelNumber = LevelNumber,
            CoreHp = CoreHp,
            Economy = Economy.ExportState(),
            Inventory = new BlockInventoryState
            {
                DirtCount = Inventory.DirtCount,
                StoneCount = Inventory.StoneCount,
                HasShovel = Inventory.HasShovel,
                HasPickaxe = Inventory.HasPickaxe
            },
            Excavated = excavated,
            TerrainPlacements = World.MovableBlocks().Where(cell => World.GetNaturalMaterial(cell) is Material.Dirt or Material.Stone &&
                World.GetNaturalMaterial(cell) != WorldGrid.InitialMaterial(cell))
                .Select(cell => new SavedTerrainPlacement { Cell = cell, Material = World.GetNaturalMaterial(cell) }).ToList(),
            DamagedTerrain = World.DamagedTerrain.Select(pair => new SavedTerrainDamage { Cell = pair.Key, Hp = pair.Value }).ToList(),
            Liquids = Liquids.ExportState(),
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
        var inventory = state.Inventory!;
        Inventory.Restore(inventory.DirtCount, inventory.StoneCount, inventory.HasShovel, inventory.HasPickaxe);
        LevelNumber = state.LevelNumber;
        Level = _levels.Get(LevelNumber);
        CoreHp = state.CoreHp;
        foreach (var cell in state.Excavated) World.Dig(cell);
        foreach (var placement in state.TerrainPlacements!) World.RestoreTerrainPlacement(placement.Cell, placement.Material);
        foreach (var damage in state.DamagedTerrain!) World.RestoreTerrainHp(damage.Cell, damage.Hp);
        foreach (var barrier in state.Barriers) World.AddBarrier(barrier.Cell, barrier.Content!.Value, barrier.Hp);
        Liquids.RestoreState(state.Liquids!);
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
        if (!CanBuild()) return false;
        if (!Enum.IsDefined(kind)) return Reject("Choose a valid piece.");
        if (!WorldGrid.IsTurretZone(cell)) return Reject("Pieces belong inside the 30-cube zone, outside the entire 8-cube block enclosure.");
        if (!IsEmpty(cell)) return Reject("That cell is occupied.");
        if (!HasTowerSupport(cell)) return Reject("Pieces need a solid block or another living tower directly below them.");
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
        if (material is not (Material.Dirt or Material.Stone)) return Reject("Choose a dirt or stone block from inventory.");
        if (!WorldGrid.IsBuildZone(cell) || WorldGrid.IsCore(cell)) return Reject("Blocks belong inside the centered 8 x 8 x 8 core enclosure.");
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
        if (material is not (Material.Dirt or Material.Stone)) return Reject("Choose a dirt or stone block to excavate.");
        if (!Inventory.CanExcavate(material, out var error)) return Reject(error);
        if (!Inventory.Add(material)) return Reject("Block inventory is full.");
        if (!World.Dig(cell))
        {
            Inventory.TrySpend(material);
            return Reject("That block could not be excavated.");
        }
        SettleDefenses();
        Message = $"Excavated {cell}; collected 1 {material.ToString().ToLowerInvariant()} block.";
        return true;
    }

    public bool Remove(Cell cell)
    {
        if (!CanBuild()) return false;
        var turret = TrySelectTurret(cell);
        if (turret != null)
        {
            Turrets.Remove(turret);
            Economy.Credit(Balance.PieceCost(turret.Kind));
            _topologyRevision++;
            SettleDefenses();
            Message = $"Recovered {Balance.PieceCost(turret.Kind)} currency. Equipped components were consumed.";
            return true;
        }
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
        LevelCatalog.Validate(Level);
        RebuildNavigation();
        _spawnCells = FindSpawnCells();
        _spawnCells.RemoveAll(cell => !_routes.Any(route => route.ContainsKey(cell)));
        if (_spawnCells.Count == 0) return Reject("The outer spawn shell needs a supported route to the core. Zombies can climb one tile and breach terrain.");
        Waves.Clear();
        for (var i = 0; i < Level.Waves.Count; i++)
        {
            var definition = Level.Waves[i];
            Waves.Add(new WaveState
            {
                Index = i,
                TotalCount = definition.Groups.Sum(group => group.Count),
                TotalHp = definition.Groups.Sum(group => (float)group.Count * LevelCatalog.ZombieMaxHp(group.Kind, group.Tier)),
                Started = i == 0
            });
        }
        Projectiles.Clear();
        Phase = GamePhase.Combat;
        Message = $"Level {LevelNumber}: {Level.Name}. Defend the core!";
        return true;
    }

    public void Update(float deltaSeconds)
    {
        if (Phase == GamePhase.Defeat || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0) return;
        // Substeps preserve collision and wave boundaries after a slow rendered frame.
        while (deltaSeconds > 0 && Phase != GamePhase.Defeat)
        {
            var step = Math.Min(deltaSeconds, .05f);
            SettleDefenses();
            Liquids.Update(step, cell => TrySelectTurret(cell) != null);
            SettleDefenses();
            if (Phase == GamePhase.Combat) Step(step);
            deltaSeconds -= step;
        }
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
        var sankInLava = zombie.SinksIn(LiquidKind.Lava);
        var sankInMercury = zombie.SinksIn(LiquidKind.Mercury);
        zombie.ApplyDamage(damage, kind, shot, incomingDirection);
        if (zombie.Hp > 0 && (sankInLava != zombie.SinksIn(LiquidKind.Lava) || sankInMercury != zombie.SinksIn(LiquidKind.Mercury)))
            ClearZombieMovement(zombie);
        // Body death discards any remaining armor from the wave's live HP pool.
        var actual = before - zombie.Hp;
        if (zombie.WaveIndex >= 0 && zombie.WaveIndex < Waves.Count)
        {
            var wave = Waves[zombie.WaveIndex];
            wave.DamageTaken = Math.Min(wave.TotalHp, wave.DamageTaken + actual);
        }
        zombie.DamageSinceLaneSwitch += actual;
        var threshold = zombie.MaxHp * Balance.LaneThreshold(LevelNumber);
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
            if (!Waves[i].Started && Waves[i - 1].Started && Waves[i - 1].DamageTaken >= Waves[i - 1].TotalHp * .5f)
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
        foreach (var zombie in Zombies)
            if (zombie.Hp > 0)
            {
                UpdateLiquidExposure(zombie, dt);
                if (zombie.Hp > 0) UpdateZombie(zombie, dt);
            }
        foreach (var zombie in Zombies)
            if (zombie.Hp <= 0)
            {
                _stepOrigins.Remove(zombie.Id);
                _liquidDestinations.Remove(zombie.Id);
                _liquidOrigins.Remove(zombie.Id);
            }
        Zombies.RemoveAll(zombie => zombie.Hp <= 0);

        if (CoreHp <= 0)
        {
            CoreHp = 0;
            Phase = GamePhase.Defeat;
            Message = $"The core fell on level {LevelNumber}. Start a new defense to try again.";
            return;
        }
        if (Zombies.Count == 0 && Waves.All(wave => wave.Started && wave.Spawned >= wave.TotalCount))
        {
            var reward = Balance.SurvivorIncome * Turrets.Count(turret => turret.Hp > 0);
            Economy.Credit(reward);
            LevelNumber++;
            Level = _levels.Get(LevelNumber);
            Phase = GamePhase.Building;
            Projectiles.Clear();
            Message = $"Level complete! {reward} currency from surviving pieces. Prepare level {LevelNumber}.";
        }
    }

    private bool Spawn(WaveState wave)
    {
        var group = WaveSpawnPlanner.SelectGroup(Level.Waves[wave.Index], wave.Spawned);
        var available = _spawnCells.Where(cell => World.GetMaterial(cell) == Material.Air && TrySelectTurret(cell) == null && World.HasSupport(cell)).ToArray();
        if (available.Length == 0) return false;
        var cell = available[_random.Next(available.Length)];
        var hp = LevelCatalog.ZombieMaxHp(group.Kind, group.Tier);
        var zombie = new Zombie
        {
            Id = _nextId++,
            WaveIndex = wave.Index,
            Kind = group.Kind,
            Tier = group.Tier,
            Position = cell.Center,
            Hp = hp,
            MaxHp = hp,
            Attack = LevelCatalog.ZombieAttack(group.Kind, group.Tier),
            Lane = cell.X == 0 ? 0 : cell.X == 31 ? 1 : cell.Z == 0 ? 2 : 3
        };
        zombie.FaceToward(new Vector3(16, zombie.Position.Y, 16) - zombie.Position);
        Zombies.Add(zombie);
        wave.Spawned++;
        return true;
    }

    private void UpdateTurrets(float dt)
    {
        foreach (var turret in Turrets)
        {
            if (turret.Hp <= 0) continue;
            turret.Cooldown = Math.Max(0, turret.Cooldown - dt);
            if (turret.Cooldown > 0) continue;
            Zombie? target = null;
            ShotKind shot = ShotKind.Melee;
            var nearest = float.PositiveInfinity;
            foreach (var zombie in Zombies)
            {
                if (zombie.Hp <= 0) continue;
                var offset = zombie.Cell - turret.Cell;
                var isJump = Economy.IsJumpOffset(turret, offset);
                if (!isJump && !Economy.IsDirectionalOffset(turret, offset)) continue;
                var kind = isJump ? ShotKind.Lob : turret.Kind == PieceKind.Pawn && offset.Chebyshev == 1
                    ? ShotKind.Melee : ShotKind.Pellet;
                if (kind != ShotKind.Lob && !HasLineOfSight(turret.Cell.Center, zombie.Position)) continue;
                var distance = Vector3.DistanceSquared(turret.Cell.Center, zombie.Position);
                if (distance >= nearest) continue;
                target = zombie;
                shot = kind;
                nearest = distance;
            }
            if (target == null) continue;
            turret.Cooldown = shot == ShotKind.Lob ? 1f : .7f;
            if (shot == ShotKind.Melee)
            {
                DamageZombie(target, Economy.GetAttack(turret), DamageKind.Blunt, ShotKind.Melee, target.Position - turret.Cell.Center);
                // A brief strike segment is also visible to the renderer.
                Projectiles.Add(new Projectile
                {
                    Origin = turret.Cell.Center,
                    Target = target.Position,
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
                    Target = target.Position,
                    TargetId = target.Id,
                    Damage = Economy.GetAttack(turret),
                    Kind = shot,
                    Duration = Math.Max(.12f, MathF.Sqrt(nearest) / (shot == ShotKind.Lob ? 5f : 16f))
                });
        }
    }

    private void UpdateProjectiles(float dt)
    {
        for (var i = Projectiles.Count - 1; i >= 0; i--)
        {
            var projectile = Projectiles[i];
            var startAge = projectile.Age;
            var start = projectile.Position;
            projectile.Age += dt;
            var end = projectile.Position;
            if (projectile.Kind == ShotKind.Melee)
            {
                if (projectile.Age >= projectile.Duration) Projectiles.RemoveAt(i);
                continue;
            }
            // Sample the swept segment, including the arc, rather than testing only its endpoint.
            var steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(start, end) / .12f));
            var stopped = false;
            for (var sample = 1; sample <= steps && !stopped; sample++)
            {
                var position = Vector3.Lerp(start, end, sample / (float)steps);
                var cell = ToCell(position);
                if (!WorldGrid.IsInside(cell) || IsShotObstacle(position))
                {
                    stopped = true;
                    break;
                }
                foreach (var zombie in Zombies)
                    if (zombie.Hp > 0 && Vector3.DistanceSquared(position, zombie.Position) <= .25f)
                    {
                        var hitAge = startAge + (Math.Min(projectile.Age, projectile.Duration) - startAge) * sample / steps;
                        DamageZombie(zombie, projectile.Damage, projectile.DamageKind, projectile.Kind, projectile.GetTravelDirection(hitAge));
                        stopped = true;
                        break;
                    }
            }
            if (stopped || projectile.Age >= projectile.Duration) Projectiles.RemoveAt(i);
        }
    }

    private void UpdateZombie(Zombie zombie, float dt)
    {
        // Another zombie may have destroyed a support or obstruction earlier in
        // this same simulation slice. Do not select a new step from that old field.
        if (_navigationRevision != _topologyRevision || _navigationWorldRevision != World.Revision) RebuildNavigation();
        zombie.Cooldown = Math.Max(0, zombie.Cooldown - dt);
        if (UpdateBuoyancy(zombie, dt)) return;
        if (zombie.NextCell is { } movingTo)
        {
            if (!_stepOrigins.TryGetValue(zombie.Id, out var movingFrom))
                _stepOrigins[zombie.Id] = movingFrom = zombie.Cell;
            var intermediate = StepClearance(movingFrom, movingTo);
            if (!IsValidStep(movingFrom, movingTo) || !HasZombieSupport(movingFrom) || !HasZombieSupport(movingTo) || IsZombieObstacle(movingTo) ||
                (intermediate.HasValue && IsZombieObstacle(intermediate.Value)))
            {
                ClearZombieMovement(zombie);
            }
            else
            {
                var remaining = dt * .85f;
                // Climb vertically before crossing the ledge; descend only after crossing
                // above the lower destination. A diagonal chord would cut through its corner.
                if (intermediate.HasValue && zombie.MoveProgress < 1)
                {
                    if (!MoveToward(zombie, intermediate.Value.Center, ref remaining)) return;
                    zombie.MoveProgress = 1;
                }
                if (!MoveToward(zombie, movingTo.Center, ref remaining)) return;
                zombie.NextCell = null;
                _stepOrigins.Remove(zombie.Id);
                return;
            }
        }

        _stepOrigins.Remove(zombie.Id);
        var current = zombie.Cell;
        if (!TrySolidFloor(zombie.Position, out var floorHeight))
        {
            DamageZombie(zombie, zombie.Hp);
            return;
        }
        if (zombie.Position.Y > floorHeight + .0001f)
        {
            // Slabs land bodies on their actual upper face; a side crust outside
            // the feet cannot suspend a unit over the remaining empty space.
            zombie.Position = new Vector3(zombie.Position.X, Math.Max(floorHeight, zombie.Position.Y - dt * 6), zombie.Position.Z);
            return;
        }
        if (FaceNearbyCore(zombie))
        {
            if (zombie.Cooldown <= 0)
            {
                CoreHp -= zombie.Attack;
                zombie.Cooldown = 1f;
            }
            return;
        }
        if (!_routes[zombie.Lane % 4].TryGetValue(current, out var next))
        {
            // A collapse can temporarily isolate a node; terrain is still destructible.
            // Prefer horizontal exits so a waiting zombie does not dig out its own footing.
            foreach (var direction in Horizontal.Concat(new[] { new Cell(0, 1, 0) }))
                if (AttackObstacle(zombie, current + direction)) break;
            return;
        }
        var clearance = StepClearance(current, next);
        if (clearance.HasValue && AttackObstacle(zombie, clearance.Value)) return;
        if (AttackObstacle(zombie, next)) return;
        if (IsValidStep(current, next) && HasZombieSupport(current) && HasZombieSupport(next) && !IsZombieObstacle(next) &&
            (!clearance.HasValue || !IsZombieObstacle(clearance.Value)))
        {
            zombie.NextCell = next;
            zombie.MoveProgress = 0;
            _stepOrigins[zombie.Id] = current;
            zombie.FaceToward(next.Center - zombie.Position);
        }
    }

    private void UpdateLiquidExposure(Zombie zombie, float dt)
    {
        var head = zombie.Position + new Vector3(0, .4f, 0);
        if (Liquids.GetLiquidAt(head).HasValue)
        {
            zombie.AirRemaining = Math.Max(0, zombie.AirRemaining - dt);
            if (zombie.AirRemaining <= .0001f)
            {
                ApplyZombieDamage(zombie, zombie.BodyHp, DamageKind.Chemical);
                return;
            }
        }
        else zombie.AirRemaining = Balance.AirSeconds;

        var contact = Liquids.GetLiquidAt(zombie.Position - new Vector3(0, .45f, 0)) ?? Liquids.GetLiquidAt(zombie.Position);
        var chemical = contact == LiquidKind.Mercury ? Balance.ChemicalDamagePerSecond : 0;
        var thermal = contact == LiquidKind.Lava ? Balance.LavaDamagePerSecond : 0;
        foreach (var hazard in Liquids.Hazards)
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
        // A newly buoyant body takes priority over any cached walking, descending,
        // or shoreline transition. Armor is evaluated from the surviving pieces.
        if (TryBuoyantSurface(zombie, zombie.Position, out var risingSurface) && zombie.Position.Y < risingSurface + .4f - .001f)
        {
            ClearZombieMovement(zombie);
            var rising = new Vector3(zombie.Position.X, Math.Min(risingSurface + .4f, zombie.Position.Y + dt * 2), zombie.Position.Z);
            if (IsBodyClear(rising)) zombie.Position = rising;
            return true;
        }
        if (_liquidDestinations.TryGetValue(zombie.Id, out var target))
        {
            var origin = _liquidOrigins[zombie.Id];
            if ((!TryBuoyantSurface(zombie, origin, out _) && !HasSolidFooting(origin)) ||
                (!TryBuoyantSurface(zombie, target, out _) && !HasSolidFooting(target)))
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
                var next = length <= dt * .85f ? waypoint : zombie.Position + offset / length * (dt * .85f);
                if (!IsBodyClear(next))
                {
                    _liquidDestinations.Remove(zombie.Id);
                    _liquidOrigins.Remove(zombie.Id);
                    return true;
                }
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
            if (!HasSolidFooting(zombie.Position) || !Horizontal.Any(direction =>
                TryBuoyantSurface(zombie, zombie.Position + new Vector3(direction.X, 0, direction.Z), out _))) return false;
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
        var desiredY = surface + .4f;
        var vertical = new Vector3(zombie.Position.X, zombie.Position.Y + Math.Clamp(desiredY - zombie.Position.Y, -dt * 2, dt * 2), zombie.Position.Z);
        if (!IsBodyClear(vertical)) return true;
        zombie.Position = vertical;
        if (Math.Abs(zombie.Position.Y - desiredY) > .001f) return true;

        if (FaceNearbyCore(zombie))
        {
            if (zombie.Cooldown <= 0) { CoreHp -= zombie.Attack; zombie.Cooldown = 1; }
            return true;
        }

        var destination = FindBuoyantRouteStep(zombie);
        if (destination.HasValue)
        {
            _liquidOrigins[zombie.Id] = zombie.Position;
            _liquidDestinations[zombie.Id] = destination.Value;
            zombie.FaceToward(destination.Value - zombie.Position);
        }
        else
            foreach (var direction in Horizontal)
                if (AttackObstacle(zombie, zombie.Cell + direction)) break;
        return true;
    }

    private void ClearZombieMovement(Zombie zombie)
    {
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        _stepOrigins.Remove(zombie.Id);
        _liquidDestinations.Remove(zombie.Id);
        _liquidOrigins.Remove(zombie.Id);
    }

    private static bool FaceNearbyCore(Zombie zombie)
    {
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
        var start = zombie.Cell;
        var positions = new Dictionary<Cell, Vector3> { [start] = zombie.Position };
        var previous = new Dictionary<Cell, Cell>();
        var frontier = new Queue<Cell>();
        frontier.Enqueue(start);
        var best = start;
        var bestDistance = Vector3.DistanceSquared(zombie.Position, new Vector3(16, 16, 16));
        while (frontier.TryDequeue(out var current))
        {
            if (Adjacent.Any(direction => WorldGrid.IsCore(current + direction))) { best = current; break; }
            foreach (var direction in Horizontal)
                foreach (var dy in new[] { 0, 1, -1 })
                {
                    var next = current + direction + new Cell(0, dy, 0);
                    if (positions.ContainsKey(next) || !WorldGrid.IsInside(next) || WorldGrid.IsCore(next)) continue;
                    Vector3? stand = null;
                    if (TryBuoyantSurface(zombie, next.Center, out var surface) && ToCell(new Vector3(next.Center.X, surface + .4f, next.Center.Z)) == next)
                        stand = new Vector3(next.Center.X, surface + .4f, next.Center.Z);
                    else if (HasZombieSupport(next) && !IsZombieObstacle(next)) stand = next.Center;
                    if (!stand.HasValue || Math.Abs(stand.Value.Y - positions[current].Y) > 1.001f || !IsBodyClear(stand.Value)) continue;
                    var crossingY = Math.Max(positions[current].Y, stand.Value.Y);
                    if (!IsBodyClear(new Vector3(positions[current].X, crossingY, positions[current].Z)) ||
                        !IsBodyClear(new Vector3(stand.Value.X, crossingY, stand.Value.Z))) continue;
                    positions[next] = stand.Value;
                    previous[next] = current;
                    frontier.Enqueue(next);
                    var distance = Vector3.DistanceSquared(stand.Value, new Vector3(16, 16, 16));
                    if (distance < bestDistance) { bestDistance = distance; best = next; }
                }
        }
        if (best == start) return null;
        while (previous[best] != start) best = previous[best];
        return positions[best];
    }

    private bool TryBuoyantSurface(Zombie zombie, Vector3 position, out float surface)
    {
        surface = float.NegativeInfinity;
        var column = ToCell(position);
        for (var y = Math.Max(0, column.Y - 1); y <= Math.Min(WorldGrid.Size - 1, column.Y + 1); y++)
            foreach (var volume in Liquids.GetVolumes(new Cell(column.X, y, column.Z)))
            {
                if (zombie.SinksIn(volume.Kind) || position.X < volume.Min.X || position.X >= volume.Max.X ||
                    position.Z < volume.Min.Z || position.Z >= volume.Max.Z ||
                    position.Y - .45f > volume.Max.Y + .15f || position.Y + .4f < volume.Min.Y) continue;
                surface = Math.Max(surface, volume.Max.Y);
            }
        return float.IsFinite(surface);
    }

    private bool HasSolidFooting(Vector3 position) => World.IsSolidAt(position - new Vector3(0, .51f, 0)) ||
        TrySelectTurret(ToCell(position - new Vector3(0, .51f, 0))) != null;

    private bool TrySolidFloor(Vector3 position, out float height)
    {
        var column = ToCell(position);
        for (var y = Math.Min(column.Y, WorldGrid.Size - 1); y >= 0; y--)
        {
            var cell = new Cell(column.X, y, column.Z);
            float? top = null;
            if (World.GetObsidian(cell) is { } crust)
            {
                if (position.X >= crust.Min.X && position.X < crust.Max.X && position.Z >= crust.Min.Z && position.Z < crust.Max.Z)
                    top = crust.Max.Y;
            }
            else if (World.GetMaterial(cell) != Material.Air || TrySelectTurret(cell) != null) top = y + 1;
            if (top.HasValue && top.Value + .5f <= position.Y + .001f)
            { height = top.Value + .5f; return true; }
        }
        height = 0;
        return false;
    }

    private bool IsBodyClear(Vector3 position)
    {
        foreach (var x in new[] { -.2f, .2f })
            foreach (var z in new[] { -.2f, .2f })
                foreach (var y in new[] { -.39f, .39f })
                {
                    var point = position + new Vector3(x, y, z);
                    if (!WorldGrid.IsInside(ToCell(point)) || World.IsSolidAt(point) || TrySelectTurret(ToCell(point)) != null) return false;
                }
        return true;
    }

    private static bool MoveToward(Zombie zombie, Vector3 destination, ref float remaining)
    {
        var offset = destination - zombie.Position;
        var distance = offset.Length();
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

    private bool AttackObstacle(Zombie zombie, Cell cell)
    {
        var barrier = World.GetBarrier(cell);
        var turret = TrySelectTurret(cell);
        var terrain = World.GetMaterial(cell);
        var breakableTerrain = terrain is Material.Dirt or Material.Stone;
        var obsidian = terrain == Material.Obsidian;
        if (barrier == null && turret == null && !breakableTerrain && !obsidian) return false;
        zombie.FaceToward(cell.Center - zombie.Position);
        if (zombie.Cooldown > 0) return true;
        zombie.Cooldown = 1f;
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
        else if (obsidian && World.DamageObsidian(cell, zombie.Attack))
            SettleDefenses();
        return true;
    }

    private void SettleDefenses()
    {
        // Health is public state and can be changed outside AttackObstacle. Retire
        // every dead piece before trusting the topology/settlement revision cache.
        if (Turrets.RemoveAll(turret => turret.Hp <= 0) > 0) _topologyRevision++;
        if (_settledWorldRevision == World.Revision && _settledTopologyRevision == _topologyRevision) return;
        // Blocks retain holes, overhangs, and ceilings whenever any orthogonal block
        // occupies a neighboring cell. Towers rely only on vertical support and settle bottom first.
        bool moved;
        do
        {
            moved = false;
            foreach (var origin in World.MovableBlocks().ToArray())
            {
                var cell = origin;
                while (cell.Y > 0 && !World.HasOrthogonalBlockSupport(cell))
                {
                    var below = cell + new Cell(0, -1, 0);
                    if (TrySelectTurret(below) != null || !World.MoveBlock(cell, below)) break;
                    Liquids.SwapDisplacedLiquid(cell, below);
                    cell = below;
                    moved = true;
                }
            }
            foreach (var turret in Turrets.Where(t => t.Hp > 0).OrderBy(t => t.Cell.Y).ThenBy(t => t.Id).ToArray())
            {
                while (turret.Cell.Y > 0)
                {
                    var below = turret.Cell + new Cell(0, -1, 0);
                    if (World.GetMaterial(below) != Material.Air || TrySelectTurret(below) != null) break;
                    Liquids.SwapDisplacedLiquid(turret.Cell, below);
                    turret.Cell = below;
                    _topologyRevision++;
                    moved = true;
                }
            }
        } while (moved);
        _turretsByCell.Clear();
        foreach (var turret in Turrets)
            if (turret.Hp > 0) _turretsByCell[turret.Cell] = turret;
        // Falling blocks and pieces crush units beneath them instead of leaving a
        // live zombie embedded inside an intact obstruction.
        foreach (var zombie in Zombies)
            if (zombie.Hp > 0 && !IsBodyClear(zombie.Position)) DamageZombie(zombie, zombie.Hp);
        _settledWorldRevision = World.Revision;
        _settledTopologyRevision = _topologyRevision;
    }

    private void RebuildNavigation()
    {
        _turretsByCell.Clear();
        foreach (var turret in Turrets)
            if (turret.Hp > 0) _turretsByCell[turret.Cell] = turret;
        var nodes = new HashSet<Cell>();
        for (var x = 0; x < WorldGrid.Size; x++)
            for (var y = 1; y < WorldGrid.Size; y++)
                for (var z = 0; z < WorldGrid.Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (HasZombieSupport(cell) && World.GetMaterial(cell) != Material.Core) nodes.Add(cell);
                }
        var goals = nodes.Where(cell => Adjacent.Any(direction => WorldGrid.IsCore(cell + direction))).ToArray();
        // All lanes use the same physical edges. Evaluate clearance and breach
        // costs once per topology rebuild rather than four times during search.
        var incoming = new Dictionary<Cell, List<(Cell Previous, float Cost)>>(nodes.Count);
        var breachCosts = new Dictionary<Cell, float>();
        float CachedBreachCost(Cell cell)
        {
            if (!breachCosts.TryGetValue(cell, out var cost)) breachCosts[cell] = cost = BreachCost(cell);
            return cost;
        }
        foreach (var cell in nodes)
        {
            var edges = new List<(Cell Previous, float Cost)>(12);
            foreach (var horizontal in Horizontal)
                for (var dy = -1; dy <= 1; dy++)
                {
                    var previous = cell + horizontal + new Cell(0, dy, 0);
                    if (!nodes.Contains(previous) || !CanPlanStep(previous, cell)) continue;
                    var clearance = StepClearance(previous, cell);
                    var cost = (cell.Y == previous.Y ? 1f : 2f) + CachedBreachCost(cell) +
                        (clearance.HasValue ? CachedBreachCost(clearance.Value) : 0f);
                    edges.Add((previous, cost));
                }
            incoming.Add(cell, edges);
        }
        for (var lane = 0; lane < 4; lane++)
        {
            _routes[lane].Clear();
            var costs = new Dictionary<Cell, float>();
            var frontier = new PriorityQueue<Cell, float>();
            foreach (var goal in goals)
            {
                var preferred = lane switch { 0 => goal.X == 14, 1 => goal.X == 17, 2 => goal.Z == 14, _ => goal.Z == 17 };
                var cost = preferred ? 0f : 8f;
                costs[goal] = cost;
                frontier.Enqueue(goal, cost);
            }
            while (frontier.TryDequeue(out var cell, out var distance))
            {
                if (distance > costs[cell]) continue;
                foreach (var (previous, edgeCost) in incoming[cell])
                {
                    var cost = distance + edgeCost;
                    if (costs.TryGetValue(previous, out var known) && known <= cost) continue;
                    costs[previous] = cost;
                    _routes[lane][previous] = cell;
                    frontier.Enqueue(previous, cost);
                }
            }
        }
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

    private bool CanPlanStep(Cell from, Cell to)
    {
        if (!IsValidStep(from, to) || World.GetMaterial(to) == Material.Core) return false;
        var clearance = StepClearance(from, to);
        // Dirt, stone, placed blocks, and pieces can be breached. The objective
        // remains an attack goal, never a body-occupiable navigation node.
        return !clearance.HasValue || (WorldGrid.IsInside(clearance.Value) && World.GetMaterial(clearance.Value) != Material.Core);
    }

    private float BreachCost(Cell cell)
    {
        var hp = World.GetBarrier(cell)?.Hp ?? World.GetObsidian(cell)?.Hp ?? World.GetTerrainHp(cell);
        if (_turretsByCell.TryGetValue(cell, out var turret)) hp += turret.Hp;
        // Shared route fields estimate breach effort from remaining durability.
        // Open detours stay available; stronger stone is costlier to tunnel through.
        return hp * .1f;
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
    private bool IsZombieObstacle(Cell cell) => World.GetMaterial(cell) != Material.Air || HasLiveIndexedTurret(cell);
    private bool HasZombieSupport(Cell cell) => WorldGrid.IsInside(cell) && World.IsSolidAt(cell.Center - new Vector3(0, .51f, 0)) ||
        (WorldGrid.IsInside(cell) && cell.Y > 0 && HasLiveIndexedTurret(cell + new Cell(0, -1, 0)));
    private bool IsEmpty(Cell cell) => World.GetMaterial(cell) == Material.Air && TrySelectTurret(cell) == null && Liquids.GetTotalUnits(cell) == 0;
    private bool CanBuild() => Phase == GamePhase.Building || Reject("Construction and excavation are available between levels.");
    private bool Reject(string message) { Message = message; return false; }
    private static Cell ToCell(Vector3 position) => new((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), (int)MathF.Floor(position.Z));
}
