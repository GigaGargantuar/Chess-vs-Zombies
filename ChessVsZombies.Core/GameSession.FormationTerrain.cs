namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private static readonly Material[] FormationSupportMaterials =
        [Material.Dirt, Material.Stone, Material.Wood, Material.Copper, Material.Iron, Material.Gold, Material.Diamond, Material.Osmium];

    // Terrain work belongs to the same construction as the tower. In particular,
    // replacing a temporary support must not settle the tower above it mid-swap.
    private bool TryDeployFormationTower(FormationTower tower, out string? error)
    {
        var cell = tower.Cell;
        var material = World.GetMaterial(cell);
        var barrier = World.GetBarrier(cell);
        var crust = World.GetObsidian(cell);
        var recoveredMaterial = barrier?.Content ?? material;
        int recoveredUnits = material == Material.Air ? 0 : crust?.Units ?? 1;
        if (recoveredUnits > 0 && barrier == null && !Inventory.CanExcavate(material, out var toolError))
        {
            error = toolError;
            return false;
        }

        var available = FormationSupportMaterials.ToDictionary(kind => kind, kind => (long)Inventory.GetCount(kind));
        if ((long)Inventory.GetCount(recoveredMaterial) + recoveredUnits > int.MaxValue)
        {
            error = "Block inventory is full.";
            return false;
        }
        if (available.ContainsKey(recoveredMaterial)) available[recoveredMaterial] += recoveredUnits;

        var supportCells = new List<Cell>();
        for (var support = cell + new Cell(0, -1, 0); support.Y >= 0; support += new Cell(0, -1, 0))
        {
            if (World.GetMaterial(support) != Material.Air || TrySelectTurret(support) != null) break;
            if (IsOccupiedByZombie(support))
            {
                error = $"A zombie is occupying the required support at {support}.";
                return false;
            }
            if (_formationPlacementAllowed?.Invoke(support) == false)
            {
                error = $"Move away from the required support at {support} first.";
                return false;
            }
            supportCells.Add(support);
            // The target block is about to be excavated, so it cannot anchor its
            // own replacement's pillar. Side anchors and the bottom layer can.
            if (support.Y == 0 || Adjacent.Any(direction => support + direction != cell &&
                    World.GetMaterial(support + direction) != Material.Air)) break;
        }

        var supports = new List<(Cell Cell, Material Material)>();
        foreach (var support in supportCells.AsEnumerable().Reverse())
        {
            var supportMaterial = FormationSupportMaterials.FirstOrDefault(kind => available[kind] > 0);
            if (supportMaterial == Material.Air)
            {
                error = "Not enough stored blocks to support this saved tower.";
                return false;
            }
            available[supportMaterial]--;
            supports.Add((support, supportMaterial));
        }

        if (recoveredUnits == 0 && supports.Count == 0)
        {
            bool deployed = Deploy(tower.Kind, cell);
            error = deployed ? null : Message;
            return deployed;
        }

        var inventoryBefore = Inventory.ExportState();
        var liquidsBefore = Liquids.ExportState();
        float terrainHp = World.GetTerrainHp(cell);
        bool cleared = false;
        var placed = new List<Cell>();

        if (recoveredUnits > 0)
        {
            cleared = barrier != null ? World.RemoveBarrier(cell) : crust != null
                ? World.DamageObsidian(cell, crust.Hp) : World.Dig(cell);
            if (!cleared || !Inventory.Add(recoveredMaterial, recoveredUnits))
            {
                error = "The saved tower's obstructing block could not be recovered.";
                RollBack();
                return false;
            }
        }

        // Preflight each displacement against the solids placed before it. A
        // later placement can run out of liquid space; roll the whole job back.
        foreach (var support in supports)
        {
            if (!Liquids.TryDisplaceLiquid(support.Cell))
            {
                error = $"There is no room for liquid displaced by the support at {support.Cell}.";
                RollBack();
                return false;
            }
            if (!World.AddBarrier(support.Cell, support.Material))
            {
                error = $"The support at {support.Cell} could not be placed.";
                RollBack();
                return false;
            }
            placed.Add(support.Cell);
            Inventory.TrySpend(support.Material);
        }
        if (!Deploy(tower.Kind, cell))
        {
            error = Message;
            RollBack();
            return false;
        }
        error = null;
        return true;

        void RollBack()
        {
            foreach (var support in placed.AsEnumerable().Reverse()) World.RemoveBarrier(support);
            if (cleared && barrier != null) World.AddBarrier(cell, barrier.Content, barrier.Hp);
            else if (cleared && Balance.IsTerrainMaterial(material))
            {
                World.RestoreTerrainPlacement(cell, material);
                if (terrainHp < Balance.BlockHp(material)) World.RestoreTerrainHp(cell, terrainHp);
            }
            Inventory.Restore(inventoryBefore);
            Liquids.RestoreState(liquidsBefore);
        }
    }
}
