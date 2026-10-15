using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private readonly Dictionary<Cell, Cell> _bridgeRoutes = [];
    private readonly List<BlockDrop> _pendingBlockDrops = [];
    private int _bridgeWorldRevision = -1, _bridgeTopologyRevision = -1;
    private long _bridgeLiquidRevision = -1;
    private int _spawnWorldRevision = -1, _spawnTopologyRevision = -1;
    private long _spawnLiquidRevision = -1;
    public IReadOnlyList<BlockDrop> PendingBlockDrops => _pendingBlockDrops.AsReadOnly();

    private void ResetBridges()
    {
        _bridgeRoutes.Clear();
        _pendingBlockDrops.Clear();
        _bridgeWorldRevision = _bridgeTopologyRevision = _spawnWorldRevision = _spawnTopologyRevision = -1;
        _bridgeLiquidRevision = _spawnLiquidRevision = -1;
    }

    private void RefreshSpawnCells()
    {
        // Spawning needs a real empty supported cell, but never a complete route.
        _spawnCells = FindSpawnCells();
        _spawnWorldRevision = World.Revision;
        _spawnTopologyRevision = _topologyRevision;
        _spawnLiquidRevision = Liquids.Revision;
    }

    private bool TryGetBridgeStep(Cell current, out Cell next)
    {
        if (_bridgeWorldRevision != World.Revision || _bridgeTopologyRevision != _topologyRevision ||
            _bridgeLiquidRevision != Liquids.Revision) RebuildBridgeRoutes();
        return _bridgeRoutes.TryGetValue(current, out next);
    }

    private void RebuildBridgeRoutes()
    {
        // Prospective floor cells exist only in this planning field. Collision and
        // walking continue to use real terrain, and each placement spends a block.
        // Sharing this field lets successive carriers extend the same crossing.
        const int count = WorldGrid.Size * WorldGrid.Size * WorldGrid.Size;
        static int Index(Cell cell) => (cell.X * WorldGrid.Size + cell.Y) * WorldGrid.Size + cell.Z;
        bool Empty(Cell cell) => World.GetMaterial(cell) == Material.Air && !HasLiveIndexedTurret(cell) && Liquids.GetTotalUnits(cell) == 0;
        var types = new byte[count]; // 1: needs a floor block; 2: physically supported.
        var distances = new float[count];
        Array.Fill(distances, float.PositiveInfinity);
        var breaches = new float[count];
        Array.Fill(breaches, float.NaN);
        var frontier = new PriorityQueue<Cell, float>();
        _bridgeRoutes.Clear();
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 1; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (WorldGrid.IsCore(cell)) continue;
                    var floor = cell + new Cell(0, -1, 0);
                    byte type = HasZombieSupport(cell) ? (byte)2 :
                        Empty(cell) && Empty(floor) ? (byte)1 : (byte)0;
                    types[Index(cell)] = type;
                    if (type == 0 || !Adjacent.Any(direction => WorldGrid.IsCore(cell + direction))) continue;
                    distances[Index(cell)] = 0;
                    frontier.Enqueue(cell, 0);
                }
        var referenceAttack = Level.Waves.SelectMany(wave => wave.Groups)
            .Select(group => LevelCatalog.ZombieAttack(group.Kind, group.Tier)).DefaultIfEmpty(Balance.BaseAttack).Max();
        float Breach(Cell cell)
        {
            ref var value = ref breaches[Index(cell)];
            if (float.IsNaN(value)) value = BreachCost(cell, referenceAttack);
            return value;
        }
        void Relax(Cell previous, Cell next, float distance, float cost)
        {
            int index = Index(previous);
            float total = distance + cost;
            if (total >= distances[index]) return;
            distances[index] = total;
            _bridgeRoutes[previous] = next;
            frontier.Enqueue(previous, total);
        }
        while (frontier.TryDequeue(out var cell, out var distance))
        {
            if (distance > distances[Index(cell)]) continue;
            foreach (var direction in Horizontal)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    var previous = cell + direction + new Cell(0, dy, 0);
                    if (!WorldGrid.IsInside(previous) || types[Index(previous)] == 0 || !CanPlanStep(previous, cell)) continue;
                    bool construction = types[Index(cell)] == 1;
                    if (construction)
                    {
                        // One block reaches the adjacent floor at the same height.
                        // A virtual previous floor would itself have been built first.
                        if (dy != 0) continue;
                        var floor = cell + new Cell(0, -1, 0);
                        if (types[Index(previous)] == 2 && !World.HasOrthogonalBlockSupport(floor)) continue;
                    }
                    var clearance = StepClearance(previous, cell);
                    float cost = (dy == 0 ? 1 : 2) + Breach(cell) +
                        (clearance.HasValue ? Breach(clearance.Value) : 0) + (construction ? 4 : 0);
                    Relax(previous, cell, distance, cost);
                }
                var launch = cell + direction + direction;
                if (WorldGrid.IsInside(launch) && types[Index(launch)] == 2 && types[Index(cell)] == 2 &&
                    CanPlanJump(launch, cell, out _)) Relax(launch, cell, distance, 2);
            }
        }
        _bridgeWorldRevision = World.Revision;
        _bridgeTopologyRevision = _topologyRevision;
        _bridgeLiquidRevision = Liquids.Revision;
    }

    private bool TryBuildBridge(Zombie zombie, Cell from, Cell next)
    {
        if (AvailableConstructionBlocks(zombie) == 0 || zombie.Cooldown > 0 || from.Y != next.Y ||
            !IsValidStep(from, next) || !HasZombieSupport(from, zombie) ||
            Vector3.DistanceSquared(zombie.Position, zombie.StandAt(from)) > .000001f) return false;
        var floor = next + new Cell(0, -1, 0);
        if (!IsEmpty(floor) || !World.HasOrthogonalBlockSupport(floor) || !IsEmpty(next)) return false;
        if (IsOccupiedByZombie(floor))
        {
            RequestBuilderYield(zombie, from, floor);
            return false;
        }
        if (zombie.CarriedBlocks > 0)
        {
            if (!World.RestoreTerrainPlacement(floor, zombie.CarriedBlockMaterial)) return false;
            zombie.CarriedBlocks--;
        }
        else
        {
            if (!TryPeekKiloConstructionBlock(zombie, out var block)) return false;
            bool placed = block.WasBarrier ? World.AddBarrier(floor, block.Content, block.Hp) : World.RestoreTerrainPlacement(floor, block.Content);
            if (!placed) return false;
            if (!block.WasBarrier && block.Hp is { } hp && hp < Balance.BlockHp(block.Content)) World.RestoreTerrainHp(floor, hp);
            ConsumeKiloConstructionBlock(zombie);
        }
        zombie.FaceToward(zombie.StandAt(next) - zombie.Position);
        RecordZombieProgress(zombie);
        zombie.Cooldown = 1;
        SettleDefenses();
        return true;
    }

    private int AvailableConstructionBlocks(Zombie zombie) => zombie.CarriedBlocks + GetKiloConstructionBlocks(zombie);

    private void QueueDeathBlock(Zombie zombie)
    {
        if (zombie.Hp > 0 || zombie.CarriedBlocks == 0) return;
        var cell = zombie.Cell;
        cell = new Cell(Math.Clamp(cell.X, 0, WorldGrid.Size - 1), Math.Clamp(cell.Y, 0, WorldGrid.Size - 1), Math.Clamp(cell.Z, 0, WorldGrid.Size - 1));
        for (int index = 0; index < zombie.CarriedBlocks; index++)
            _pendingBlockDrops.Add(new BlockDrop(cell, zombie.CarriedBlockMaterial));
        zombie.CarriedBlocks = 0;
    }

    private void ResolveBlockDrops()
    {
        bool placed = false;
        for (int i = 0; i < _pendingBlockDrops.Count;)
        {
            var drop = _pendingBlockDrops[i];
            Cell? destination = null;
            // Keep the death column. If occupied (including liquid), release the
            // solid above it and let normal gravity/displacement settle it. This
            // never overwrites terrain, traps a live body, or deletes liquid.
            for (int y = drop.Cell.Y; y < WorldGrid.Size; y++)
            {
                var candidate = new Cell(drop.Cell.X, y, drop.Cell.Z);
                if (!IsEmpty(candidate) || IsOccupiedByZombie(candidate)) continue;
                destination = candidate;
                break;
            }
            // A completely obstructed column retains the finite block for a
            // later geometry change; pending drops are included in checkpoints.
            if (!destination.HasValue) { i++; continue; }
            bool restored = drop.WasBarrier
                ? World.AddBarrier(destination.Value, drop.Content, drop.Hp)
                : World.RestoreTerrainPlacement(destination.Value, drop.Content);
            if (!restored) { i++; continue; }
            if (!drop.WasBarrier && drop.Hp is { } hp && hp < Balance.BlockHp(drop.Content))
                World.RestoreTerrainHp(destination.Value, hp);
            _pendingBlockDrops.RemoveAt(i);
            placed = true;
        }
        if (placed) SettleDefenses();
    }
}
