using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    public bool CanBuildObsidian(Cell cell, CrustFace face) => ObsidianPlacementError(cell, face) is null;

    public bool BuildObsidian(Cell cell, CrustFace face)
    {
        if (ObsidianPlacementError(cell, face) is { } error) return Reject(error);
        var existing = World.GetObsidian(cell);
        var selected = PlacementPart(existing, face);
        var actualFace = selected?.Face ?? face;
        var offset = selected?.OffsetUnits ?? 0;
        var proposed = PlacementCrust(cell, face);
        if (!Inventory.TrySpend(Material.Obsidian)) return Reject("No recovered obsidian layers remain.");
        if (!Liquids.TryDisplaceLiquid(cell, LiquidSystem.UnitsPerBlock - proposed.Units, proposed))
        {
            Inventory.Add(Material.Obsidian);
            return Reject("There is no room for the displaced liquid.");
        }
        if (!World.AddObsidian(cell, 1, actualFace, offset))
        {
            Inventory.Add(Material.Obsidian);
            return Reject("That obsidian layer could not be placed.");
        }
        Message = $"Placed an obsidian layer at {cell} ({World.GetObsidian(cell)!.Units}/10 layers).";
        return true;
    }

    private string? ObsidianPlacementError(Cell cell, CrustFace face)
    {
        if (Phase != GamePhase.Building) return "Obsidian layers can be placed between levels.";
        if (!Enum.IsDefined(face)) return "Choose a valid supporting face for the obsidian layer.";
        if (!WorldGrid.IsFortificationZone(cell)) return "Place obsidian in the 30-wide, 32-tall area, outside the core.";
        if (Inventory.GetCount(Material.Obsidian) == 0) return "No recovered obsidian layers remain.";
        var existing = World.GetObsidian(cell);
        if (World.GetMaterial(cell) is not (Material.Air or Material.Obsidian) || TrySelectTurret(cell) != null)
            return "That cell is occupied by a block or tower.";
        var selected = PlacementPart(existing, face);
        if (!World.CanAddObsidian(cell, 1, selected?.Face ?? face, selected?.OffsetUnits ?? 0))
            return "This obsidian crust has no room for another layer.";
        var cellCrust = PlacementCrust(cell, face);
        if (!Liquids.CanDisplaceLiquid(cell, LiquidSystem.UnitsPerBlock - cellCrust.Units, cellCrust))
            return "There is no room for the displaced liquid.";
        var proposed = selected is null
            ? new ObsidianCell(cell, 1, face, LiquidSystem.ObsidianHpPerUnit)
            : selected with { Units = selected.Units + 1, Hp = selected.Hp + LiquidSystem.ObsidianHpPerUnit };
        if (existing is null && !(cell.Y == 0 && face == CrustFace.Bottom) &&
            !TouchesNeighborBlock(proposed.Min, proposed.Max, cell + CrustDirection(face)))
            return "The first layer needs a solid block touching its supporting face.";
        if (Zombies.Any(zombie => zombie.Hp > 0 && BoundsOverlap(proposed.Min, proposed.Max,
                zombie.Position - zombie.BodyHalfExtents, zombie.Position + zombie.BodyHalfExtents)))
            return "That layer would overlap a living zombie.";
        return null;
    }

    private static ObsidianCell? PlacementPart(ObsidianCell? crust, CrustFace face) =>
        crust?.Parts.FirstOrDefault(part => part.Face == face) ?? crust?.Parts.First();

    private ObsidianCell PlacementCrust(Cell cell, CrustFace face)
    {
        var existing = World.GetObsidian(cell);
        if (existing is null) return new ObsidianCell(cell, 1, face, LiquidSystem.ObsidianHpPerUnit);
        var selected = PlacementPart(existing, face)!;
        bool opposite = selected.Face != existing.Face;
        return existing with
        {
            Units = existing.Units + 1,
            Hp = existing.Hp + LiquidSystem.ObsidianHpPerUnit,
            OppositeUnits = existing.OppositeUnits + (opposite ? 1 : 0),
            OppositeHp = existing.OppositeHp + (opposite ? LiquidSystem.ObsidianHpPerUnit : 0)
        };
    }

    /// <summary>Settle slabs by actual contact, including disjoint slabs sharing one cell.</summary>
    internal bool SettleUnsupportedObsidianStacks(HashSet<Cell> movedSolids, List<ObsidianCell> movedCrusts)
    {
        bool changed = SettlePairedObsidianLoads(movedSolids, movedCrusts);
        foreach (var originalCrust in World.ObsidianCells.Where(crust => crust.Units < LiquidSystem.UnitsPerBlock)
                     .SelectMany(crust => crust.Parts).OrderBy(crust => crust.Cell.Y).ToArray())
        {
            var origin = originalCrust.Cell;
            if (IsMixedClusterPartHandled(originalCrust) || TrySelectTurret(origin) != null ||
                World.GetObsidian(origin) is not { } originalCell || originalCell.Units == LiquidSystem.UnitsPerBlock ||
                !originalCell.Parts.Contains(originalCrust)) continue;
            var load = new List<Cell>();
            var next = origin + new Cell(0, 1, 0);
            while (IsFullMovableBlock(next))
            {
                load.Add(next);
                next += new Cell(0, 1, 0);
            }
            if (HasStackAnchor(originalCrust, load.Count) || HasCrustFooting(originalCrust)) continue;

            var current = origin;
            int movingLoadCount = load.Count;
            int fallenCells = 0;
            int loadDrop = 0;
            bool shattered = false;
            bool loadRegistered = false;
            void RegisterMovingLoad()
            {
                if (loadRegistered || movingLoadCount == 0) return;
                RegisterFallingBlockCluster(load);
                loadRegistered = true;
            }
            while (World.GetObsidian(current)?.Parts.FirstOrDefault(part => part.Face == originalCrust.Face) is { } crust)
            {
                if (World.GetObsidian(current)!.Units == LiquidSystem.UnitsPerBlock) break;
                var below = current + new Cell(0, -1, 0);
                if (!WorldGrid.IsInside(below)) break;
                // Only boxes with overlapping footprints can halt this fall.
                // An opposite wall slab can fit beside us in the destination cell.
                if (TrySelectTurret(below) != null || HasHardSurfaceBelow(crust, below))
                {
                    // A top/internal crust may already have a real fractional gap
                    // beneath it. A bottom crust resting on ground was excluded above.
                    bool fell = fallenCells > 0 || crust.Min.Y > current.Y + .00001f;
                    if (movingLoadCount > 0 && fell)
                    {
                        World.DamageObsidianPart(current, crust.Face, crust.Hp);
                        ShatterObsidian(current);
                        // An opposite slab may remain under the load. It is real
                        // support, and must not be overwritten by the falling block.
                        if (World.GetMaterial(current) == Material.Air)
                        {
                            RegisterMovingLoad();
                            ShiftLoadDown(current, movingLoadCount);
                            loadDrop++;
                        }
                        shattered = true;
                        changed = true;
                    }
                    break;
                }
                // A grounded partner can take over the load while this disjoint
                // slab keeps falling. Keep the load at that real contact instead
                // of dragging it through the stationary partner's occupied cell.
                if (movingLoadCount > 0 && World.GetObsidian(current)!.OppositeUnits > 0)
                {
                    bool partnerSupportsLoad = World.GetObsidian(current)!.Parts.Any(part =>
                        part.Face != crust.Face && IsGroundedObsidianPart(part) &&
                        Math.Abs(part.Max.Y - (current.Y + 1)) <= .00001f);
                    if (!partnerSupportsLoad) break;
                    movingLoadCount = 0;
                }
                RegisterMovingLoad();
                if (!MoveFallingObsidianPart(crust, below)) break;
                if (crust.Face is CrustFace.Bottom or CrustFace.Top) Liquids.SwapDisplacedLiquid(current, below);
                else Liquids.DisplaceForObsidianMove(current, below);
                ShiftLoadDown(current, movingLoadCount);
                current = below;
                fallenCells++;
                if (movingLoadCount > 0) loadDrop++;
                changed = true;
            }

            if (!shattered && fallenCells > 0)
            {
                var moved = World.GetObsidian(current)!.Parts.First(part => part.Face == originalCrust.Face);
                Falls.RecordObsidianDrop(origin, current, moved);
                movedCrusts.Add(moved);
            }
            if (loadDrop == 0) continue;
            foreach (var original in load)
            {
                var destination = original - new Cell(0, loadDrop, 0);
                Falls.RecordBlockDrop(original, destination, World);
                movedSolids.Add(destination);
            }
        }
        return changed;
    }

    private bool SettlePairedObsidianLoads(HashSet<Cell> movedSolids, List<ObsidianCell> movedCrusts)
    {
        bool changed = false;
        foreach (var original in World.ObsidianCells.Where(crust => crust.OppositeUnits > 0 &&
                     crust.Units < LiquidSystem.UnitsPerBlock).OrderBy(crust => crust.Cell.Y).ToArray())
        {
            if (original.Parts.Any(IsMixedClusterPartHandled) || TrySelectTurret(original.Cell) != null ||
                World.GetObsidian(original.Cell) != original) continue;
            int loadCount = 0;
            while (IsFullMovableBlock(original.Cell + new Cell(0, loadCount + 1, 0))) loadCount++;
            if (loadCount == 0 || original.Parts.Any(part => HasCrustFooting(part) || HasStackAnchor(part, loadCount))) continue;
            var current = original.Cell;
            int drop = 0;
            bool loadRegistered = false;
            void RegisterMovingLoad()
            {
                if (loadRegistered) return;
                RegisterFallingBlockCluster(Enumerable.Range(1, loadCount)
                    .Select(height => original.Cell + new Cell(0, height, 0)));
                loadRegistered = true;
            }
            while (World.GetObsidian(current) is { } crust)
            {
                var below = current - new Cell(0, 1, 0);
                if (!WorldGrid.IsInside(below)) break;
                if (World.GetMaterial(below) != Material.Air || TrySelectTurret(below) != null)
                {
                    if (drop > 0 && crust.Parts.Any(part => HasHardSurfaceBelow(part, below)))
                    {
                        foreach (var impact in crust.Parts.Where(part => HasHardSurfaceBelow(part, below)).ToArray())
                            World.DamageObsidianPart(current, impact.Face, impact.Hp);
                        ShatterObsidian(current);
                        if (World.GetMaterial(current) == Material.Air)
                        {
                            RegisterMovingLoad();
                            ShiftLoadDown(current, loadCount);
                            drop++;
                        }
                        changed = true;
                    }
                    break;
                }
                RegisterMovingLoad();
                if (!MoveFallingBlock(current, below)) break;
                Liquids.DisplaceForObsidianMove(current, below);
                ShiftLoadDown(current, loadCount);
                current = below;
                drop++;
                changed = true;
            }
            if (drop == 0) continue;
            if (World.GetObsidian(current) is { } remaining)
                foreach (var part in remaining.Parts)
                {
                    Falls.RecordObsidianDrop(original.Cell, current, part);
                    movedCrusts.Add(part);
                }
            for (int height = 1; height <= loadCount; height++)
            {
                var from = original.Cell + new Cell(0, height, 0);
                var to = from - new Cell(0, drop, 0);
                Falls.RecordBlockDrop(from, to, World);
                movedSolids.Add(to);
            }
        }
        return changed;
    }

    private void ShiftLoadDown(Cell bottom, int count)
    {
        for (int height = 1; height <= count; height++)
        {
            var from = bottom + new Cell(0, height, 0);
            var to = from - new Cell(0, 1, 0);
            // The preceding lower object has already vacated this cell. The
            // synchronous column move cannot acquire new occupancy between steps.
            if (!MoveFallingBlock(from, to)) throw new InvalidOperationException("An obsidian load lost its contiguous block geometry.");
            Liquids.SwapDisplacedLiquid(from, to);
        }
    }

    private bool IsFullMovableBlock(Cell cell) => Balance.IsTerrainMaterial(World.GetMaterial(cell)) ||
        World.GetMaterial(cell) == Material.Barrier || World.GetObsidian(cell)?.Units == LiquidSystem.UnitsPerBlock;

    // Decide attachment before a fall begins. A detached slab or loaded pair
    // cannot acquire a new side/ceiling anchor while passing it on the way down.
    private bool HasStackAnchor(ObsidianCell crust, int loadCount) => IsGroundedObsidianPart(crust);

    private bool HasCrustFooting(ObsidianCell crust) =>
        TouchesNeighborBlock(crust.Min, crust.Max, crust.Cell - new Cell(0, 1, 0));

    private bool HasHardSurfaceBelow(ObsidianCell crust, Cell below)
    {
        Vector3 min, max;
        if (TrySelectTurret(below) != null)
        {
            min = new Vector3(below.X, below.Y, below.Z);
            max = min + Vector3.One;
        }
        else return BlockBounds(below).Any(bounds => FootprintsOverlap(crust.Min, crust.Max, bounds.Min, bounds.Max));
        return FootprintsOverlap(crust.Min, crust.Max, min, max);
    }

    private bool TouchesNeighborBlock(Vector3 min, Vector3 max, Cell neighbor)
    {
        return BlockBounds(neighbor).Any(bounds => TouchesBounds(min, max, bounds.Min, bounds.Max));
    }

    private IEnumerable<(Vector3 Min, Vector3 Max)> BlockBounds(Cell cell)
    {
        if (!WorldGrid.IsInside(cell) || World.GetMaterial(cell) == Material.Air) yield break;
        if (World.GetObsidian(cell) is { } crust)
        {
            foreach (var part in crust.Parts) yield return (part.Min, part.Max);
        }
        else
        {
            var min = new Vector3(cell.X, cell.Y, cell.Z);
            yield return (min, min + Vector3.One);
        }
    }

    private static bool FootprintsOverlap(Vector3 min, Vector3 max, Vector3 otherMin, Vector3 otherMax) =>
        MathF.Min(max.X, otherMax.X) - MathF.Max(min.X, otherMin.X) > .00001f &&
        MathF.Min(max.Z, otherMax.Z) - MathF.Max(min.Z, otherMin.Z) > .00001f;

    private static bool TouchesBounds(Vector3 min, Vector3 max, Vector3 otherMin, Vector3 otherMax)
    {
        const float epsilon = .00001f;
        bool overlapX = MathF.Min(max.X, otherMax.X) - MathF.Max(min.X, otherMin.X) > epsilon;
        bool overlapY = MathF.Min(max.Y, otherMax.Y) - MathF.Max(min.Y, otherMin.Y) > epsilon;
        bool overlapZ = MathF.Min(max.Z, otherMax.Z) - MathF.Max(min.Z, otherMin.Z) > epsilon;
        return overlapX && overlapZ && (Math.Abs(min.Y - otherMax.Y) <= epsilon || Math.Abs(max.Y - otherMin.Y) <= epsilon) ||
            overlapY && overlapZ && (Math.Abs(min.X - otherMax.X) <= epsilon || Math.Abs(max.X - otherMin.X) <= epsilon) ||
            overlapX && overlapY && (Math.Abs(min.Z - otherMax.Z) <= epsilon || Math.Abs(max.Z - otherMin.Z) <= epsilon);
    }

    private static bool BoundsOverlap(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMin.X < bMax.X && aMax.X > bMin.X && aMin.Y < bMax.Y && aMax.Y > bMin.Y && aMin.Z < bMax.Z && aMax.Z > bMin.Z;

    private static Cell CrustDirection(CrustFace face) => face switch
    {
        CrustFace.West => new(-1, 0, 0), CrustFace.East => new(1, 0, 0),
        CrustFace.Bottom => new(0, -1, 0), CrustFace.Top => new(0, 1, 0),
        CrustFace.North => new(0, 0, -1), CrustFace.South => new(0, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(face))
    };
}
