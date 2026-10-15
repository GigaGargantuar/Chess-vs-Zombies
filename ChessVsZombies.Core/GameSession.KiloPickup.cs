using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private bool TryGrabNearbyKiloCover(Zombie zombie, Vector3 destination, int blockIndex)
    {
        var min = zombie.Position - zombie.BodyHalfExtents;
        var max = zombie.Position + zombie.BodyHalfExtents;
        List<(Cell Cell, float Distance)>? candidates = null;
        // Only the adjacent shell is within reach. Do not turn a nearby-block
        // search into free excavation of the carrier's own supporting floor.
        for (int x = (int)MathF.Floor(min.X) - 1; x < (int)MathF.Ceiling(max.X) + 1; x++)
            for (int y = (int)MathF.Ceiling(min.Y - .00001f); y <= (int)MathF.Floor(max.Y + .00001f); y++)
                for (int z = (int)MathF.Floor(min.Z) - 1; z < (int)MathF.Ceiling(max.Z) + 1; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (!WorldGrid.IsInside(cell) || WorldGrid.IsCore(cell)) continue;
                    var material = World.GetBarrier(cell)?.Content ?? World.GetNaturalMaterial(cell);
                    if (!Balance.IsTerrainMaterial(material) || World.GetObsidian(cell) != null || TrySelectTurret(cell) != null) continue;
                    candidates ??= [];
                    candidates.Add((cell, Vector3.DistanceSquared(cell.Center, destination)));
                }
        if (candidates == null) return false;
        candidates.Sort((left, right) =>
        {
            int order = left.Distance.CompareTo(right.Distance);
            if (order == 0) order = left.Cell.X.CompareTo(right.Cell.X);
            if (order == 0) order = left.Cell.Y.CompareTo(right.Cell.Y);
            if (order == 0) order = left.Cell.Z.CompareTo(right.Cell.Z);
            return order;
        });
        foreach (var candidate in candidates)
            if (TryTakeKiloCover(zombie, candidate.Cell, destination, blockIndex)) return true;
        return false;
    }

    private bool TryTakeKiloCover(Zombie zombie, Cell source, Vector3 destination, int blockIndex)
    {
        var barrier = World.GetBarrier(source);
        var material = barrier?.Content ?? World.GetNaturalMaterial(source);
        if (!WorldGrid.IsInside(source) || !Balance.IsTerrainMaterial(material) || World.GetObsidian(source) != null ||
            WorldGrid.IsCore(source) || TrySelectTurret(source) != null) return false;
        var lifted = new Vector3(source.Center.X, destination.Y, source.Center.Z);
        var half = new Vector3(.5f);
        if (KiloBoxSweep(source.Center, lifted, half, out _, source) ||
            KiloBoxSweep(lifted, destination, half, out _, source)) return false;
        foreach (var ally in Zombies)
        {
            if (ally.Id == zombie.Id || ally.Hp <= 0) continue;
            var low = ally.Position - ally.BodyHalfExtents - half + new Vector3(.00001f);
            var high = ally.Position + ally.BodyHalfExtents + half - new Vector3(.00001f);
            if (ProjectileBoxIntersection(source.Center, lifted - source.Center, low, high, out _) ||
                ProjectileBoxIntersection(lifted, destination - lifted, low, high, out _)) return false;
        }
        var hp = barrier?.Hp ?? World.GetTerrainHp(source);
        if (!(barrier != null ? World.RemoveBarrier(source) : World.Dig(source))) return false;
        if (!_kiloBlocks.TryGetValue(zombie.Id, out var blocks)) _kiloBlocks.Add(zombie.Id, blocks = []);
        var acquired = CreateHeldKiloBlock(zombie, blockIndex, material, hp, barrier != null);
        if (blockIndex == 0) blocks.Insert(0, acquired);
        else blocks.Add(acquired);
        TryRaiseKiloBackpackCover(zombie);
        _kiloActionCooldowns[zombie.Id] = 1;
        SettleDefenses();
        return true;
    }
}
