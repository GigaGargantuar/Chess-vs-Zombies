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
        static int Index(Cell cell) => (cell.X * WorldGrid.Size + cell.Y) * WorldGrid.Size + cell.Z;
        static int CoreDistance(int coordinate) => Math.Abs(coordinate - Math.Clamp(coordinate, 15, 16));

        var workspace = _navigationWorkspace ??= new NavigationWorkspace();
        var horizontalDistances = workspace.Horizontal;
        var verticalDistances = workspace.Vertical;
        var travelCosts = workspace.Travel;
        var nextIndices = workspace.Next;
        Array.Fill(horizontalDistances, int.MaxValue);
        Array.Fill(verticalDistances, int.MaxValue);
        Array.Fill(travelCosts, float.PositiveInfinity);
        Array.Fill(nextIndices, -1);
        var frontier = workspace.SpentFrontier;
        frontier.Clear();
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

    private bool IsBuilderYieldActive(BuilderYield request) => request.Builder.Hp > 0 && AvailableConstructionBlocks(request.Builder) > 0 &&
        request.Builder.Cell == request.BuilderCell && !request.Builder.NextCell.HasValue &&
        !IsZombieHurled(request.Builder) &&
        !_liquidDestinations.ContainsKey(request.Builder.Id) &&
        Vector3.DistanceSquared(request.Builder.Position, request.Builder.StandAt(request.BuilderCell)) < .000001f &&
        HasZombieSupport(request.BuilderCell) && World.HasOrthogonalBlockSupport(request.Floor) &&
        World.GetMaterial(request.Floor) == Material.Air &&
        !HasLiveIndexedTurret(request.Floor) && Liquids.GetTotalUnits(request.Floor) == 0;

    private void RequestBuilderYield(Zombie builder, Cell from, Cell floor)
    {
        foreach (var blocker in Zombies)
        {
            if (blocker.Hp <= 0 || AvailableConstructionBlocks(blocker) != 0 || !BodyOverlapsCell(blocker.Position, floor, blocker)) continue;
            // Finish existing physical movement first. In particular, do not
            // redirect a jumper or a unit currently leaving the requested cell.
            if (blocker.NextCell.HasValue || IsZombieHurled(blocker) || _liquidDestinations.ContainsKey(blocker.Id) ||
                !HasSolidFooting(blocker.Position, blocker) || Vector3.DistanceSquared(blocker.Position, blocker.StandAt(blocker.Cell)) > .000001f) continue;
            if (_builderYields.TryGetValue(blocker.Id, out var existing) && IsBuilderYieldActive(existing)) continue;
            _builderYields.Remove(blocker.Id);

            Cell? destination = null;
            int bestCost = int.MaxValue;
            foreach (var horizontal in Horizontal)
                foreach (int dy in new[] { 0, -1, 1 })
                {
                    var next = blocker.Cell + horizontal + new Cell(0, dy, 0);
                    var standing = blocker.StandAt(next);
                    if (!CanYieldStep(blocker.Cell, next, blocker) || BodyOverlapsCell(standing, floor, blocker) || IsOccupiedByZombie(next)) continue;
                    if (_builderYields.Values.Any(request => IsBuilderYieldActive(request) && BodyOverlapsCell(standing, request.Floor, blocker))) continue;
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
        if (AvailableConstructionBlocks(zombie) != 0 || !IsBuilderYieldActive(request))
        {
            _builderYields.Remove(zombie.Id);
            return false;
        }
        if (zombie.NextCell.HasValue || _liquidDestinations.ContainsKey(zombie.Id)) return false;
        if (zombie.Cell == request.Destination && Vector3.DistanceSquared(zombie.Position, zombie.StandAt(request.Destination)) < .000001f)
        {
            // Keep the floor free until this particular carrier has finished or
            // abandoned construction; ordinary fallback must not send us back.
            next = zombie.Cell;
            return true;
        }
        if (zombie.Cell != request.Origin || Vector3.DistanceSquared(zombie.Position, zombie.StandAt(request.Origin)) > .000001f ||
            !CanYieldStep(zombie.Cell, request.Destination, zombie))
        {
            _builderYields.Remove(zombie.Id);
            return false;
        }
        next = request.Destination;
        return true;
    }

    private bool CanYieldStep(Cell from, Cell to, Zombie? zombie = null)
    {
        if (!IsValidStep(from, to) || !HasZombieSupport(from, zombie) || !HasZombieSupport(to, zombie) ||
            IsZombieObstacle(to, zombie) || !IsBodyClear(zombie?.StandAt(to) ?? to.Center, zombie)) return false;
        var clearance = StepClearance(from, to);
        if (clearance.HasValue && IsZombieObstacle(clearance.Value, zombie)) return false;
        var start = zombie?.StandAt(from) ?? from.Center;
        var end = zombie?.StandAt(to) ?? to.Center;
        var waypoint = clearance.HasValue ? zombie?.StandAt(clearance.Value) ?? clearance.Value.Center : end;
        return !TryFindBodyObstacle(start, waypoint, out _, zombie: zombie) && !TryFindBodyObstacle(waypoint, end, out _, zombie: zombie);
    }

    private static bool BodyOverlapsCell(Vector3 position, Cell cell, Zombie? zombie = null)
    {
        var body = zombie?.BodyHalfExtents ?? new Vector3(.2f, .39f, .2f);
        return position.X + body.X > cell.X && position.X - body.X < cell.X + 1 &&
            position.Y + body.Y > cell.Y && position.Y - body.Y < cell.Y + 1 &&
            position.Z + body.Z > cell.Z && position.Z - body.Z < cell.Z + 1;
    }
}
