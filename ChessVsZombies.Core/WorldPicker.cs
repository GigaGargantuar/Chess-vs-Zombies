using System.Numerics;

namespace ChessVsZombies.Core;

public readonly record struct AimResult(Cell Hit, Cell? Placement, bool IsLiquid, CrustFace? ObsidianFace = null)
{
    public Cell? Highlight => IsLiquid ? Placement : Hit;
}

/// <summary>Shared crosshair picking for every tool, using solid and liquid geometry.</summary>
public static class WorldPicker
{
    public static AimResult? Pick(GameSession session, Vector3 origin, Vector3 direction, float maxDistance = 64, bool includeLiquids = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Finite(origin) || !Finite(direction) || !float.IsFinite(maxDistance) || maxDistance <= 0) return null;
        double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y + (double)direction.Z * direction.Z);
        if (length == 0) return null;
        double dx = direction.X / length, dy = direction.Y / length, dz = direction.Z / length;
        if (!Intersect(origin, dx, dy, dz, Vector3.Zero, new Vector3(WorldGrid.Size), out var enter, out var exit)) return null;
        enter = Math.Max(0, enter);
        exit = Math.Min(maxDistance, exit);
        if (enter > exit) return null;

        int sx = Math.Sign(dx), sy = Math.Sign(dy), sz = Math.Sign(dz);
        var cell = new Cell(EntryCell(origin.X, dx, enter), EntryCell(origin.Y, dy, enter), EntryCell(origin.Z, dz, enter));
        var previous = cell;
        if (enter > 0)
        {
            // The cell immediately outside the world remains the placement for an entry-face hit.
            previous -= new Cell(AtEntryFace(origin.X, dx, enter) ? sx : 0,
                AtEntryFace(origin.Y, dy, enter) ? sy : 0, AtEntryFace(origin.Z, dz, enter) ? sz : 0);
        }

        while (WorldGrid.IsInside(cell) && enter <= exit)
        {
            double tx = NextBoundary(origin.X, dx, cell.X), ty = NextBoundary(origin.Y, dy, cell.Y), tz = NextBoundary(origin.Z, dz, cell.Z);
            double next = Math.Min(tx, Math.Min(ty, tz));
            double nearest = double.PositiveInfinity;
            bool liquid = false;
            CrustFace? obsidianFace = null;
            var min = new Vector3(cell.X, cell.Y, cell.Z);
            var max = min + Vector3.One;
            var material = session.World.GetMaterial(cell);
            if (session.TrySelectTurret(cell) is not null || material is not (Material.Air or Material.Obsidian))
                Consider(min, max, false);
            else if (session.World.GetObsidian(cell) is { } crust)
                foreach (var part in crust.Parts) Consider(part.Min, part.Max, false, part.Face);
            if (includeLiquids)
                foreach (var volume in session.Liquids.GetVolumes(cell)) Consider(volume.Min, volume.Max, true);
            if (double.IsFinite(nearest))
            {
                var placement = liquid ? cell + new Cell(0, 1, 0) : previous;
                return new AimResult(cell, WorldGrid.IsInside(placement) ? placement : null, liquid, obsidianFace);
            }
            if (next > exit) break;
            previous = cell;
            // Simultaneous crossings enter the diagonal cell without inventing an edge-only visit.
            cell += new Cell(tx == next ? sx : 0, ty == next ? sy : 0, tz == next ? sz : 0);
            enter = next;

            void Consider(Vector3 boxMin, Vector3 boxMax, bool isLiquid, CrustFace? face = null)
            {
                if (!Intersect(origin, dx, dy, dz, boxMin, boxMax, out var near, out var far) || far <= enter) return;
                near = Math.Max(enter, near);
                // Solids were considered first and keep precedence at a shared surface.
                if (near > next || near > exit || near >= nearest) return;
                nearest = near;
                liquid = isLiquid;
                obsidianFace = face;
            }
        }
        return null;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static int EntryCell(double origin, double direction, double distance)
    {
        double position = origin + direction * distance;
        int cell = (int)Math.Floor(position);
        if (direction < 0 && position == cell) cell--;
        return Math.Clamp(cell, 0, WorldGrid.Size - 1);
    }

    private static bool AtEntryFace(double origin, double direction, double distance) => direction != 0 &&
        (direction > 0 ? -origin / direction : (WorldGrid.Size - origin) / direction) == distance;

    private static double NextBoundary(double origin, double direction, int cell) => direction == 0
        ? double.PositiveInfinity : ((direction > 0 ? cell + 1 : cell) - origin) / direction;

    private static bool Intersect(Vector3 origin, double dx, double dy, double dz, Vector3 min, Vector3 max,
        out double near, out double far)
    {
        near = double.NegativeInfinity;
        far = double.PositiveInfinity;
        return Slab(origin.X, dx, min.X, max.X, ref near, ref far) &&
            Slab(origin.Y, dy, min.Y, max.Y, ref near, ref far) &&
            Slab(origin.Z, dz, min.Z, max.Z, ref near, ref far) && far > Math.Max(near, 0);
    }

    private static bool Slab(double origin, double direction, double min, double max, ref double near, ref double far)
    {
        if (direction == 0) return origin >= min && origin < max;
        double a = (min - origin) / direction, b = (max - origin) / direction;
        near = Math.Max(near, Math.Min(a, b));
        far = Math.Min(far, Math.Max(a, b));
        return near < far;
    }
}
