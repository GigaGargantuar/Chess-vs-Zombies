namespace ChessVsZombies.Core;

/// <summary>Purchased excavation tools and reusable blocks recovered from the terrain.</summary>
public sealed class BlockInventory
{
    private readonly Dictionary<Material, int> _counts = Enum.GetValues<Material>().Where(Balance.IsTerrainMaterial).ToDictionary(material => material, _ => 0);
    public bool HasShovel { get; private set; }
    public bool HasPickaxe { get; private set; }
    public int DirtCount => GetCount(Material.Dirt);
    public int StoneCount => GetCount(Material.Stone);
    public int ObsidianLayers { get; private set; }

    /// <summary>Obsidian is counted in tenth-block layers; other materials are counted in blocks.</summary>
    public int GetCount(Material material) => material == Material.Obsidian ? ObsidianLayers : _counts.GetValueOrDefault(material);

    public bool TryPurchase(ToolKind tool, Economy economy, out string error)
    {
        ArgumentNullException.ThrowIfNull(economy);
        if (!Enum.IsDefined(tool))
        {
            error = "Choose a shovel or pickaxe.";
            return false;
        }
        if (tool == ToolKind.Shovel ? HasShovel : HasPickaxe)
        {
            error = $"You already own the {tool.ToString().ToLowerInvariant()}.";
            return false;
        }

        int cost = tool == ToolKind.Shovel ? Balance.ShovelCost : Balance.PickaxeCost;
        if (!economy.TrySpend(cost))
        {
            error = economy.LastMessage;
            return false;
        }
        if (tool == ToolKind.Shovel) HasShovel = true;
        else HasPickaxe = true;
        error = string.Empty;
        return true;
    }

    public bool CanExcavate(Material material, out string error)
    {
        switch (material)
        {
            case Material.Dirt when HasShovel:
            case Material.Obsidian when HasPickaxe:
            case var equippedMaterial when Balance.IsTerrainMaterial(equippedMaterial) && equippedMaterial != Material.Dirt && HasPickaxe:
                error = string.Empty;
                return true;
            case Material.Dirt:
                error = $"Buy a shovel for {Balance.ShovelCost} currency before excavating dirt.";
                return false;
            case Material.Obsidian:
                error = $"Buy a pickaxe for {Balance.PickaxeCost} currency before mining obsidian.";
                return false;
            case var terrainMaterial when Balance.IsTerrainMaterial(terrainMaterial):
                error = $"Buy a pickaxe for {Balance.PickaxeCost} currency before excavating {terrainMaterial.ToString().ToLowerInvariant()}.";
                return false;
            default:
                error = "Only terrain blocks can be excavated.";
                return false;
        }
    }

    /// <summary>Returns false without changing inventory for invalid or overflowing additions.</summary>
    public bool Add(Material material, int count = 1)
    {
        if (!(Balance.IsTerrainMaterial(material) || material == Material.Obsidian) || count < 0) return false;
        long total = (long)GetCount(material) + count;
        if (total > int.MaxValue) return false;
        if (material == Material.Obsidian) ObsidianLayers = (int)total;
        else _counts[material] = (int)total;
        return true;
    }

    public bool TrySpend(Material material, int count = 1)
    {
        if (!(Balance.IsTerrainMaterial(material) || material == Material.Obsidian) || count < 0 || count > GetCount(material)) return false;
        if (material == Material.Obsidian) ObsidianLayers -= count;
        else _counts[material] -= count;
        return true;
    }

    public void Reset()
    {
        HasShovel = false;
        HasPickaxe = false;
        ObsidianLayers = 0;
        foreach (var material in _counts.Keys) _counts[material] = 0;
    }

    public BlockInventoryState ExportState() => new()
    {
        DirtCount = DirtCount,
        StoneCount = StoneCount,
        HasShovel = HasShovel,
        HasPickaxe = HasPickaxe,
        ObsidianLayers = ObsidianLayers,
        AdditionalMaterials = _counts.Where(pair => pair.Key is not (Material.Dirt or Material.Stone)).ToDictionary(pair => pair.Key, pair => pair.Value)
    };

    internal void Restore(int dirt, int stone, bool shovel, bool pick) => Restore(new BlockInventoryState
    {
        DirtCount = dirt, StoneCount = stone, HasShovel = shovel, HasPickaxe = pick,
        AdditionalMaterials = _counts.Keys.Where(material => material is not (Material.Dirt or Material.Stone)).ToDictionary(material => material, _ => 0)
    });

    internal void Restore(BlockInventoryState state)
    {
        if (state is null || state.DirtCount < 0 || state.StoneCount < 0 || state.ObsidianLayers < 0 || state.AdditionalMaterials is null ||
            state.AdditionalMaterials.Count != _counts.Count - 2 ||
            state.AdditionalMaterials.Any(pair => !Balance.IsTerrainMaterial(pair.Key) || pair.Key is Material.Dirt or Material.Stone || pair.Value < 0))
            throw new InvalidDataException("Saved inventory requires nonnegative counts for every terrain material.");
        _counts[Material.Dirt] = state.DirtCount;
        _counts[Material.Stone] = state.StoneCount;
        ObsidianLayers = state.ObsidianLayers;
        foreach (var pair in state.AdditionalMaterials) _counts[pair.Key] = pair.Value;
        HasShovel = state.HasShovel;
        HasPickaxe = state.HasPickaxe;
    }
}
