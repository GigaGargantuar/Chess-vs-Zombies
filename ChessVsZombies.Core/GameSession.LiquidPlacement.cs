namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private bool CanPlaceDisplacingSolid(Cell cell) => WorldGrid.IsInside(cell) &&
        World.GetMaterial(cell) == Material.Air && TrySelectTurret(cell) == null &&
        (Liquids.GetTotalUnits(cell) == 0 || Liquids.CanDisplaceLiquid(cell));

    public bool CanDeploy(PieceKind kind, Cell cell) => DeployPlacementError(kind, cell) is null;

    private string? DeployPlacementError(PieceKind kind, Cell cell, bool prepareTerrain = false)
    {
        if (!CanInteractWithTowers) return "Retry the level before interacting with towers.";
        if (!Enum.IsDefined(kind)) return "Choose a valid piece.";
        if (!WorldGrid.IsTurretZone(cell)) return "Pieces belong inside the 30-cube zone, outside the entire 8-cube block enclosure.";
        if ((!prepareTerrain && World.GetMaterial(cell) != Material.Air) || TrySelectTurret(cell) != null) return "That cell is occupied.";
        if (IsOccupiedByZombie(cell)) return "A zombie is occupying that cell.";
        if (!prepareTerrain && !HasTowerSupport(cell)) return "Pieces need a solid block or another living tower directly below them.";
        if (Economy.Currency < Balance.PieceCost(kind)) return "Not enough currency to deploy this piece.";
        if (Phase == GamePhase.Combat && !CanHoldSurvivorReward((long)Economy.Currency - Balance.PieceCost(kind),
                Turrets.Count(turret => turret.Hp > 0) + 1, SurvivorRewardPerTower))
            return "The wallet cannot hold this tower's eventual survivor reward.";
        if (!prepareTerrain && !Liquids.CanDisplaceLiquid(cell, LiquidSystem.TurretCapacityUnits)) return "There is no room for the displaced liquid.";
        return null;
    }

    public bool CanBuildBarrier(Cell cell, Material material = Material.Dirt) => BarrierPlacementError(cell, material) is null;

    private string? BarrierPlacementError(Cell cell, Material material)
    {
        if (Phase != GamePhase.Building) return "Terrain changes and supply purchases are available between levels.";
        if (!Balance.IsTerrainMaterial(material)) return "Choose a terrain material from inventory.";
        if (!WorldGrid.IsFortificationZone(cell)) return "Blocks can be built in the core enclosure and turret area, outside the core and spawn shell.";
        if (World.GetMaterial(cell) != Material.Air || TrySelectTurret(cell) != null) return "That cell is occupied.";
        if (!World.HasOrthogonalBlockSupport(cell)) return "Blocks need a supporting block on one of their six faces.";
        if (Inventory.GetCount(material) == 0) return $"No {material.ToString().ToLowerInvariant()} blocks available. Excavate some first.";
        if (!Liquids.CanDisplaceLiquid(cell)) return "There is no room for the displaced liquid.";
        return null;
    }
}
