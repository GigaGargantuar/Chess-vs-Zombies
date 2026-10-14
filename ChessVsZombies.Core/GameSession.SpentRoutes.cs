using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private readonly Dictionary<Cell, Cell> _spentRoutes = [];
    private readonly Dictionary<int, BuilderYield> _builderYields = [];

    private void ResetSpentRoutes()
    {
        _spentRoutes.Clear();
        _builderYields.Clear();
    }

    private void BuildSpentRoutes(List<Cell> nodes, int[] nodeRows, float[] edgeCosts, int[] edgeOffsets, int edgesPerNode)
    {
        const int count = WorldGrid.Size * WorldGrid.Size * WorldGrid.Size;
        static int Index(Cell cell) => (cell.X * WorldGrid.Size + cell.Y) * WorldGrid.Size + cell.Z;
        static int CoreDistance(int coordinate) => Math.Abs(coordinate - Math.Clamp(coordinate, 15, 16));

        var horizontalDistances = new int[count];
        var verticalDistances = new int[count];
        var travelCosts = new float[count];
        var nextIndices = new int[count];
        Array.Fill(horizontalDistances, int.MaxValue);
        Array.Fill(verticalDistances, int.MaxValue);
        Array.Fill(travelCosts, float.PositiveInfinity);
        Array.Fill(nextIndices, -1);
        var frontier = new PriorityQueue<int, (int Horizontal, int Vertical, float Travel)>(nodes.Count);
        _spentRoutes.Clear();
        if (_builderYields.Count > 0)
        {
            var living = Zombies.Where(zombie => zombie.Hp > 0).Select(zombie => zombie.Id).ToHashSet();
            foreach (var entry in _builderYields.ToArray())
                if (!living.Contains(entry.Key) || !IsBuilderYieldActive(entry.Value)) _builderYields.Remove(entry.Key);
        }

        // Every empty supported cell is a possible place to stop. Propagating
        // these priorities through real edges selects the best reachable goal
        // in each component, including routes that initially move away from it.
        // Horizontal approach takes priority so a builder on a bridge stump can
        // descend into the core's excavation instead of waiting for free blocks.
        foreach (var cell in nodes)
        {
            if (World.GetMaterial(cell) != Material.Air || HasLiveIndexedTurret(cell)) continue;
            int index = Index(cell);
            int dx = CoreDistance(cell.X), dz = CoreDistance(cell.Z);
            int horizontal = dx * dx + dz * dz;
            int vertical = CoreDistance(cell.Y);
            horizontalDistances[index] = horizontal;
            verticalDistances[index] = vertical;
            travelCosts[index] = 0;
            nextIndices[index] = index;
            frontier.Enqueue(index, (horizontal, vertical, 0));
        }

        while (frontier.TryDequeue(out var index, out var priority))
        {
            var current = (horizontalDistances[index], verticalDistances[index], travelCosts[index]);
            if (priority.CompareTo(current) != 0) continue;
            int edgeStart = (nodeRows[index] - 1) * edgesPerNode;
            for (int slot = 0; slot < edgesPerNode; slot++)
            {
                float edgeCost = edgeCosts[edgeStart + slot];
                if (float.IsNaN(edgeCost)) continue;
                int previous = index + edgeOffsets[slot];
                var candidate = (Horizontal: priority.Horizontal, Vertical: priority.Vertical, Travel: priority.Travel + edgeCost);
                var known = (horizontalDistances[previous], verticalDistances[previous], travelCosts[previous]);
                if (candidate.CompareTo(known) >= 0) continue;
                horizontalDistances[previous] = candidate.Horizontal;
                verticalDistances[previous] = candidate.Vertical;
                travelCosts[previous] = candidate.Travel;
                nextIndices[previous] = index;
                frontier.Enqueue(previous, candidate);
            }
        }

        // Real movement/breach costs are positive, so every nonterminal edge
        // reduces the selected goal's travel cost. Self routes are explicit
        // waits; they must not fall through to construction or arbitrary digging.
        foreach (var cell in nodes)
        {
            int next = nextIndices[Index(cell)];
            if (next >= 0) _spentRoutes[cell] = nodes[nodeRows[next] - 1];
        }
    }

    private sealed record BuilderYield(Zombie Builder, Cell BuilderCell, Cell Floor, Cell Origin, Cell Destination);

    private bool IsBuilderYieldActive(BuilderYield request) => request.Builder.Hp > 0 && request.Builder.CarriedBlocks > 0 &&
        request.Builder.Cell == request.BuilderCell && !request.Builder.NextCell.HasValue &&
        !_liquidDestinations.ContainsKey(request.Builder.Id) &&
        Vector3.DistanceSquared(request.Builder.Position, request.BuilderCell.Center) < .000001f &&
        HasZombieSupport(request.BuilderCell) && World.HasOrthogonalBlockSupport(request.Floor) &&
        World.GetMaterial(request.Floor) == Material.Air &&
        !HasLiveIndexedTurret(request.Floor) && Liquids.GetTotalUnits(request.Floor) == 0;

    private void RequestBuilderYield(Zombie builder, Cell from, Cell floor)
    {
        foreach (var blocker in Zombies)
        {
            if (blocker.Hp <= 0 || blocker.CarriedBlocks != 0 || !BodyOverlapsCell(blocker.Position, floor)) continue;
            // Finish existing physical movement first. In particular, do not
            // redirect a jumper or a unit currently leaving the requested cell.
            if (blocker.NextCell.HasValue || _liquidDestinations.ContainsKey(blocker.Id) ||
                !HasSolidFooting(blocker.Position) || Vector3.DistanceSquared(blocker.Position, blocker.Cell.Center) > .000001f) continue;
            if (_builderYields.TryGetValue(blocker.Id, out var existing) && IsBuilderYieldActive(existing)) continue;
            _builderYields.Remove(blocker.Id);

            Cell? destination = null;
            int bestCost = int.MaxValue;
            foreach (var horizontal in Horizontal)
                foreach (int dy in new[] { 0, -1, 1 })
                {
                    var next = blocker.Cell + horizontal + new Cell(0, dy, 0);
                    if (!CanYieldStep(blocker.Cell, next) || BodyOverlapsCell(next.Center, floor) || IsOccupiedByZombie(next)) continue;
                    if (_builderYields.Values.Any(request => IsBuilderYieldActive(request) && BodyOverlapsCell(next.Center, request.Floor))) continue;
                    int cost = dy == 0 ? 1 : 2;
                    if (cost >= bestCost) continue;
                    bestCost = cost;
                    destination = next;
                }
            if (destination.HasValue)
                _builderYields[blocker.Id] = new BuilderYield(builder, from, floor, blocker.Cell, destination.Value);
        }
    }

    private bool TryGetBuilderYieldStep(Zombie zombie, out Cell next)
    {
        next = default;
        if (!_builderYields.TryGetValue(zombie.Id, out var request)) return false;
        if (zombie.CarriedBlocks != 0 || !IsBuilderYieldActive(request))
        {
            _builderYields.Remove(zombie.Id);
            return false;
        }
        if (zombie.NextCell.HasValue || _liquidDestinations.ContainsKey(zombie.Id)) return false;
        if (zombie.Cell == request.Destination && Vector3.DistanceSquared(zombie.Position, request.Destination.Center) < .000001f)
        {
            // Keep the floor free until this particular carrier has finished or
            // abandoned construction; ordinary fallback must not send us back.
            next = zombie.Cell;
            return true;
        }
        if (zombie.Cell != request.Origin || Vector3.DistanceSquared(zombie.Position, request.Origin.Center) > .000001f ||
            !CanYieldStep(zombie.Cell, request.Destination))
        {
            _builderYields.Remove(zombie.Id);
            return false;
        }
        next = request.Destination;
        return true;
    }

    private bool CanYieldStep(Cell from, Cell to)
    {
        if (!IsValidStep(from, to) || !HasZombieSupport(from) || !HasZombieSupport(to) ||
            IsZombieObstacle(to) || !IsBodyClear(to.Center)) return false;
        var clearance = StepClearance(from, to);
        if (clearance.HasValue && IsZombieObstacle(clearance.Value)) return false;
        var waypoint = clearance?.Center ?? to.Center;
        return !TryFindBodyObstacle(from.Center, waypoint, out _) && !TryFindBodyObstacle(waypoint, to.Center, out _);
    }

    private static bool BodyOverlapsCell(Vector3 position, Cell cell) =>
        position.X + .2f > cell.X && position.X - .2f < cell.X + 1 &&
        position.Y + .39f > cell.Y && position.Y - .39f < cell.Y + 1 &&
        position.Z + .2f > cell.Z && position.Z - .2f < cell.Z + 1;
}
