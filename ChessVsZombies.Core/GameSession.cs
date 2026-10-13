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
    private readonly Dictionary<Cell, Cell>[] _routes = [[], [], [], []];
    private readonly Dictionary<Cell, Turret> _turretsByCell = [];
    private List<Cell> _spawnCells = [];

    public WorldGrid World { get; private set; } = new();
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
        Level = _levels.Get(1);
    }

    public void Reset()
    {
        World = new WorldGrid();
        Economy.Reset();
        Inventory.Reset();
        Turrets.Clear();
        Zombies.Clear();
        Projectiles.Clear();
        Waves.Clear();
        _turretsByCell.Clear();
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
                    if (!WorldGrid.IsCore(cell) && World.GetMaterial(cell) is Material.Air or Material.Barrier) excavated.Add(cell);
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
        foreach (var barrier in state.Barriers) World.AddBarrier(barrier.Cell, barrier.Content!.Value, barrier.Hp);
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
        if (!World.HasSupport(cell)) return Reject("Pieces need a solid block directly below them.");
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

    public bool BuildBarrier(Cell cell, Material material = Material.Dirt)
    {
        if (!CanBuild()) return false;
        if (material is not (Material.Dirt or Material.Stone)) return Reject("Choose a dirt or stone block from inventory.");
        if (!WorldGrid.IsBuildZone(cell) || WorldGrid.IsCore(cell)) return Reject("Blocks belong inside the centered 8 x 8 x 8 core enclosure.");
        if (!IsEmpty(cell)) return Reject("That cell is occupied.");
        if (!World.HasSupport(cell)) return Reject("Blocks need a solid block directly below them.");
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
        if (SupportsDefense(cell)) return Reject("Remove the defense immediately above this block first.");
        if (!Inventory.CanExcavate(material, out var error)) return Reject(error);
        if (!Inventory.Add(material)) return Reject("Block inventory is full.");
        if (!World.Dig(cell))
        {
            Inventory.TrySpend(material);
            return Reject("That block could not be excavated.");
        }
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
            Message = $"Recovered {Balance.PieceCost(turret.Kind)} currency. Equipped components were consumed.";
            return true;
        }
        var barrier = World.GetBarrier(cell);
        if (barrier == null) return Reject("Choose a placed piece or defensive block to remove.");
        if (SupportsDefense(cell)) return Reject("Remove the defense immediately above this block first.");
        if (!Inventory.Add(barrier.Content)) return Reject("Block inventory is full.");
        World.RemoveBarrier(cell);
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
        if (_spawnCells.Count == 0) return Reject("Leave a walkable route from the outer spawn shell to the core. Zombies can climb one tile.");
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
        if (Phase != GamePhase.Combat || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0) return;
        // Substeps preserve collision and wave boundaries after a slow rendered frame.
        while (deltaSeconds > 0 && Phase == GamePhase.Combat)
        {
            var step = Math.Min(deltaSeconds, .05f);
            Step(step);
            deltaSeconds -= step;
        }
    }

    public void DamageZombie(Zombie zombie, float damage)
    {
        if (damage <= 0 || !float.IsFinite(damage) || zombie.Hp <= 0) return;
        var actual = Math.Min(zombie.Hp, damage);
        zombie.Hp -= actual;
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
                Spawn(wave);
                wave.SpawnTimer += Level.Waves[wave.Index].SpawnInterval;
            }
        }

        if (_navigationRevision != _topologyRevision || _navigationWorldRevision != World.Revision) RebuildNavigation();
        UpdateTurrets(dt);
        UpdateProjectiles(dt);
        foreach (var zombie in Zombies)
            if (zombie.Hp > 0) UpdateZombie(zombie, dt);
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

    private void Spawn(WaveState wave)
    {
        var index = wave.Spawned;
        var group = Level.Waves[wave.Index].Groups[0];
        foreach (var candidate in Level.Waves[wave.Index].Groups)
        {
            group = candidate;
            if (index < candidate.Count) break;
            index -= candidate.Count;
        }
        var cell = _spawnCells[_random.Next(_spawnCells.Count)];
        var hp = LevelCatalog.ZombieMaxHp(group.Kind, group.Tier);
        Zombies.Add(new Zombie
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
        });
        wave.Spawned++;
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
                if (kind != ShotKind.Lob && !HasLineOfSight(turret.Cell.Center, zombie.Position, turret.Cell)) continue;
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
                DamageZombie(target, Economy.GetAttack(turret));
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
                if (!WorldGrid.IsInside(cell) || (cell != ToCell(projectile.Origin) && IsShotObstacle(cell)))
                {
                    stopped = true;
                    break;
                }
                foreach (var zombie in Zombies)
                    if (zombie.Hp > 0 && Vector3.DistanceSquared(position, zombie.Position) <= .25f)
                    {
                        DamageZombie(zombie, projectile.Damage);
                        stopped = true;
                        break;
                    }
            }
            if (stopped || projectile.Age >= projectile.Duration) Projectiles.RemoveAt(i);
        }
    }

    private void UpdateZombie(Zombie zombie, float dt)
    {
        zombie.Cooldown = Math.Max(0, zombie.Cooldown - dt);
        if (zombie.NextCell is { } movingTo)
        {
            if (IsZombieObstacle(movingTo))
            {
                zombie.NextCell = null;
                return;
            }
            var offset = movingTo.Center - zombie.Position;
            var distance = offset.Length();
            var step = dt * .85f;
            if (distance <= step)
            {
                zombie.Position = movingTo.Center;
                zombie.NextCell = null;
            }
            else zombie.Position += offset / distance * step;
            return;
        }

        var current = zombie.Cell;
        if (!World.HasSupport(current) && TrySelectTurret(current + new Cell(0, -1, 0)) == null)
        {
            // A destroyed support can leave a unit on a ledge that no longer exists.
            // Fall toward the next supported cell instead of leaving it suspended without a route.
            var landing = current;
            while (landing.Y > 0 && !World.HasSupport(landing) && TrySelectTurret(landing + new Cell(0, -1, 0)) == null)
                landing += new Cell(0, -1, 0);
            if (landing.Y == 0)
            {
                DamageZombie(zombie, zombie.Hp);
                return;
            }
            zombie.Position = new Vector3(zombie.Position.X, Math.Max(landing.Center.Y, zombie.Position.Y - dt * 6), zombie.Position.Z);
            return;
        }
        if (Adjacent.Any(direction => WorldGrid.IsCore(current + direction)))
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
            // An isolated ledge can arise after defenses collapse; attack an adjacent defense if any.
            foreach (var direction in Adjacent)
                if (AttackObstacle(zombie, current + direction)) break;
            return;
        }
        if (AttackObstacle(zombie, next)) return;
        if (!IsZombieObstacle(next)) zombie.NextCell = next;
    }

    private bool AttackObstacle(Zombie zombie, Cell cell)
    {
        var barrier = World.GetBarrier(cell);
        var turret = TrySelectTurret(cell);
        if (barrier == null && turret == null) return false;
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
                _topologyRevision++;
                SettleDefenses();
            }
        }
        return true;
    }

    private void SettleDefenses()
    {
        foreach (var barrier in World.Barriers.OrderBy(barrier => barrier.Cell.Y).ToArray())
        {
            var destination = barrier.Cell;
            while (destination.Y > 0 && World.GetMaterial(destination + new Cell(0, -1, 0)) == Material.Air &&
                   TrySelectTurret(destination + new Cell(0, -1, 0)) == null)
                destination += new Cell(0, -1, 0);
            if (destination == barrier.Cell) continue;
            World.RemoveBarrier(barrier.Cell);
            if (destination.Y > 0) World.AddBarrier(destination, barrier.Content, barrier.Hp);
        }
        foreach (var turret in Turrets.OrderBy(turret => turret.Cell.Y).ToArray())
        {
            var destination = turret.Cell;
            while (destination.Y > 0 && World.GetMaterial(destination + new Cell(0, -1, 0)) == Material.Air &&
                   TrySelectTurret(destination + new Cell(0, -1, 0)) == null)
                destination += new Cell(0, -1, 0);
            if (destination == turret.Cell) continue;
            turret.Cell = destination;
            // A collapsing support destroys a piece if it falls into the block-only
            // enclosure or outside the playable deployment zone.
            if (!WorldGrid.IsTurretZone(destination)) Turrets.Remove(turret);
            _topologyRevision++;
        }
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
                    if (World.HasSupport(cell) && World.GetMaterial(cell) is Material.Air or Material.Barrier) nodes.Add(cell);
                }
        var goals = nodes.Where(cell => Adjacent.Any(direction => WorldGrid.IsCore(cell + direction))).ToArray();
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
                foreach (var horizontal in Horizontal)
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var previous = cell + horizontal + new Cell(0, dy, 0);
                        if (!nodes.Contains(previous) || !CanStep(previous, cell)) continue;
                        var cost = distance + (cell.Y == previous.Y ? 1f : 1.5f) + (IsZombieObstacle(cell) ? 5f : 0f);
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

    private bool CanStep(Cell from, Cell to)
    {
        // A one-cell-tall unit needs clearance above the lower endpoint to climb or descend.
        if (to.Y > from.Y && World.GetMaterial(from + new Cell(0, 1, 0)) != Material.Air) return false;
        if (to.Y < from.Y && World.GetMaterial(to + new Cell(0, 1, 0)) != Material.Air) return false;
        return true;
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

    private bool HasLineOfSight(Vector3 from, Vector3 to, Cell origin)
    {
        var count = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to) / .1f));
        for (var i = 1; i < count; i++)
        {
            var cell = ToCell(Vector3.Lerp(from, to, i / (float)count));
            if (cell != origin && IsShotObstacle(cell)) return false;
        }
        return true;
    }

    private bool IsShotObstacle(Cell cell) => World.GetMaterial(cell) != Material.Air || TrySelectTurret(cell) != null;
    private bool IsZombieObstacle(Cell cell) => World.GetMaterial(cell) != Material.Air || _turretsByCell.ContainsKey(cell);
    private bool IsEmpty(Cell cell) => World.GetMaterial(cell) == Material.Air && TrySelectTurret(cell) == null;
    private bool SupportsDefense(Cell cell) => World.GetBarrier(cell + new Cell(0, 1, 0)) != null || TrySelectTurret(cell + new Cell(0, 1, 0)) != null;
    private bool CanBuild() => Phase == GamePhase.Building || Reject("Construction and excavation are available between levels.");
    private bool Reject(string message) { Message = message; return false; }
    private static Cell ToCell(Vector3 position) => new((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), (int)MathF.Floor(position.Z));
}
