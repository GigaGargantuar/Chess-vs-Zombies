using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private static readonly Cell[] MegaChunkDirections =
        [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
    private static readonly IReadOnlyList<Cell[]>[] MegaChunkShapes = CreateMegaChunkShapes();

    private static IReadOnlyList<Cell[]>[] CreateMegaChunkShapes()
    {
        var result = new IReadOnlyList<Cell[]>[5];
        result[0] = [];
        result[1] = [new Cell[] { new(0, 0, 0) }];
        for (int count = 2; count <= 4; count++)
        {
            var shapes = new Dictionary<string, Cell[]>();
            foreach (var previous in result[count - 1])
                foreach (var cell in previous)
                    foreach (var direction in MegaChunkDirections)
                    {
                        var added = cell + direction;
                        if (previous.Contains(added)) continue;
                        var expanded = previous.Append(added).ToArray();
                        var minimum = new Cell(expanded.Min(part => part.X), expanded.Min(part => part.Y), expanded.Min(part => part.Z));
                        var normalized = expanded.Select(part => part - minimum)
                            .OrderBy(part => part.X).ThenBy(part => part.Y).ThenBy(part => part.Z).ToArray();
                        // A connected chunk must fit within the Mega's real three-cube body
                        // while it transfers the chunk into its two hands.
                        if (normalized.Any(part => part.X > 2 || part.Y > 2 || part.Z > 2)) continue;
                        shapes.TryAdd(string.Join(";", normalized), normalized);
                    }
            result[count] = shapes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray();
        }
        return result;
    }

    private bool TryExtractMegaChunk(Zombie mega)
    {
        _kiloBlocks.TryGetValue(mega.Id, out var held);
        int needed = 4 - (held?.Count ?? 0);
        if (mega.Size != ZombieSize.Mega || mega.Hp <= 0 || needed <= 0 || needed > 4 || IsKiloUsingBuckets(mega) ||
            mega.NextCell.HasValue || !KiloHasFooting(mega, mega.Position) ||
            TryFindBodyObstacle(mega.Position, mega.Position, out _, zombie: mega)) return false;
        var bodyMin = mega.Position - mega.BodyHalfExtents;
        var bodyMax = mega.Position + mega.BodyHalfExtents;
        var available = new HashSet<Cell>();
        // A two-cell horizontal reach allows a full 2x2 surface chunk beside
        // the body, without excavating any of the nine cells supporting it.
        for (int x = (int)MathF.Floor(bodyMin.X) - 2; x <= (int)MathF.Ceiling(bodyMax.X) + 1; x++)
            for (int y = Math.Max(0, (int)MathF.Floor(bodyMin.Y) - 1); y <= (int)MathF.Floor(bodyMax.Y); y++)
                for (int z = (int)MathF.Floor(bodyMin.Z) - 2; z <= (int)MathF.Ceiling(bodyMax.Z) + 1; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (!WorldGrid.IsInside(cell) || WorldGrid.IsCore(cell) || World.GetObsidian(cell) != null ||
                        TrySelectTurret(cell) != null ||
                        !Balance.IsTerrainMaterial(World.GetBarrier(cell)?.Content ?? World.GetNaturalMaterial(cell))) continue;
                    bool bodyOrFooting = Zombies.Any(ally => ally.Hp > 0 &&
                        cell.X + 1 > ally.Position.X - ally.BodyHalfExtents.X && cell.X < ally.Position.X + ally.BodyHalfExtents.X &&
                        cell.Z + 1 > ally.Position.Z - ally.BodyHalfExtents.Z && cell.Z < ally.Position.Z + ally.BodyHalfExtents.Z &&
                        cell.Y + 1 >= ally.Position.Y - Math.Max(ally.BodyHalfExtents.Y, ally.StandingHeight) - .00001f &&
                        cell.Y < ally.Position.Y + ally.BodyHalfExtents.Y);
                    if (!bodyOrFooting) available.Add(cell);
                }
        if (available.Count < needed) return false;
        var attempted = new HashSet<string>();
        // Prefer a single flat 2x2 face. Smaller connected shapes only repair
        // missing capacity; an empty Mega never acquires fewer than four blocks.
        foreach (bool squareOnly in needed == 4 ? new[] { true, false } : new[] { false })
        {
            var candidates = new List<Cell[]>();
            foreach (var shape in MegaChunkShapes[needed])
            {
                bool square = needed == 4 && shape.Max(cell => cell.X) <= 1 && shape.Max(cell => cell.Y) <= 1 &&
                    shape.Max(cell => cell.Z) <= 1 &&
                    (shape.All(cell => cell.X == 0) || shape.All(cell => cell.Y == 0) || shape.All(cell => cell.Z == 0));
                if (needed == 4 && square != squareOnly) continue;
                foreach (var first in available)
                {
                    var origin = first - shape[0];
                    if (!shape.All(part => available.Contains(origin + part))) continue;
                    var cells = shape.Select(part => origin + part).ToArray();
                    if (attempted.Add(string.Join(";", cells))) candidates.Add(cells);
                }
            }
            foreach (var cells in candidates.OrderByDescending(cells => cells.Count(cell => World.GetBarrier(cell) != null))
                         .ThenBy(cells => cells.Count(cell => cell.Y < bodyMin.Y))
                         .ThenBy(cells => cells.Sum(cell => Vector3.DistanceSquared(cell.Center, mega.Position)))
                         .ThenBy(cells => cells[0].X).ThenBy(cells => cells[0].Y).ThenBy(cells => cells[0].Z))
                if (TryLiftMegaChunk(mega, cells, held)) return true;
        }
        return false;
    }

    private bool TryLiftMegaChunk(Zombie mega, Cell[] cells, List<HeldKiloBlock>? held)
    {
        var ignored = cells.ToHashSet();
        var half = new Vector3(.49999f);
        var minimum = new Vector3(cells.Min(cell => cell.X), cells.Min(cell => cell.Y), cells.Min(cell => cell.Z));
        var maximum = new Vector3(cells.Max(cell => cell.X) + 1, cells.Max(cell => cell.Y) + 1, cells.Max(cell => cell.Z) + 1);
        var origin = (minimum + maximum) * .5f;
        var destination = mega.Position;
        Vector3[] waypoints =
        [new(origin.X, destination.Y, origin.Z), new(destination.X, origin.Y, destination.Z), destination];
        bool extractionClear = waypoints.Any(waypoint => cells.All(cell =>
            MegaChunkSweepClear(mega, cell.Center, cell.Center + waypoint - origin, half, ignored) &&
            MegaChunkSweepClear(mega, cell.Center + waypoint - origin, cell.Center + destination - origin, half, ignored)));
        if (!extractionClear) return false;

        // Validate every final hand slot and every source before removing any
        // material. Settling/navigation work happens after the whole extraction.
        var freeIndices = Enumerable.Range(0, 4).Where(index => held == null || held.All(block => block.BlockIndex != index)).ToArray();
        for (int index = 0; index < 4; index++)
        {
            var center = mega.Position + MegaHeldOffset(mega, index);
            if (!MegaChunkSweepClear(mega, center, center, half, ignored)) return false;
        }
        var extracted = cells.Select((cell, index) =>
        {
            var barrier = World.GetBarrier(cell);
            return (Cell: cell, Block: new HeldKiloBlock(freeIndices[index],
                barrier?.Content ?? World.GetNaturalMaterial(cell), barrier?.Hp ?? World.GetTerrainHp(cell), barrier != null));
        }).ToArray();
        if (extracted.Any(part => !Balance.IsTerrainMaterial(part.Block.Material) || part.Block.Hp <= 0 ||
            part.Block.WasBarrier != (World.GetBarrier(part.Cell) != null))) return false;
        foreach (var part in extracted)
        {
            // Distinct, validated cells cannot fail between these synchronous
            // removals: no simulation callback or settlement runs mid-commit.
            if (part.Block.WasBarrier) World.RemoveBarrier(part.Cell);
            else World.Dig(part.Cell);
        }
        if (held == null) _kiloBlocks[mega.Id] = held = [];
        held.AddRange(extracted.Select(part => part.Block));
        foreach (var block in held) block.Offset = MegaHeldOffset(mega, block.BlockIndex);
        RecordZombieProgress(mega);
        SettleDefenses();
        return true;
    }

    private bool MegaChunkSweepClear(Zombie mega, Vector3 from, Vector3 to, Vector3 half, HashSet<Cell> ignored)
    {
        var low = Vector3.Min(from, to) - half;
        var high = Vector3.Max(from, to) + half;
        if (low.X < 0 || low.Y < 0 || low.Z < 0 || high.X > WorldGrid.Size || high.Y > WorldGrid.Size || high.Z > WorldGrid.Size) return false;
        for (int x = Math.Max(0, (int)MathF.Floor(low.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(high.X)); x++)
            for (int y = Math.Max(0, (int)MathF.Floor(low.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(high.Y)); y++)
                for (int z = Math.Max(0, (int)MathF.Floor(low.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(high.Z)); z++)
                {
                    var cell = new Cell(x, y, z);
                    if (ignored.Contains(cell) || World.GetMaterial(cell) == Material.Air && !HasLiveIndexedTurret(cell)) continue;
                    foreach (var bounds in SolidCollisionBounds(cell))
                        if (ProjectileBoxIntersection(from, to - from, bounds.Min - half + new Vector3(.00001f),
                            bounds.Max + half - new Vector3(.00001f), out _)) return false;
                }
        return !Zombies.Any(ally => ally.Id != mega.Id && ally.Hp > 0 &&
            ProjectileBoxIntersection(from, to - from, ally.Position - ally.BodyHalfExtents - half,
                ally.Position + ally.BodyHalfExtents + half, out _));
    }
}
