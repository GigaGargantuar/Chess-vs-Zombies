namespace ChessVsZombies.Core;

/// <summary>Purchased excavation tools and reusable blocks recovered from the terrain.</summary>
public sealed class BlockInventory
{
    public bool HasShovel { get; private set; }
    public bool HasPickaxe { get; private set; }
    public int DirtCount { get; private set; }
    public int StoneCount { get; private set; }

    public int GetCount(Material material) => material switch
    {
        Material.Dirt => DirtCount,
        Material.Stone => StoneCount,
        _ => 0
    };

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
            case Material.Stone when HasPickaxe:
                error = string.Empty;
                return true;
            case Material.Dirt:
                error = $"Buy a shovel for {Balance.ShovelCost} currency before excavating dirt.";
                return false;
            case Material.Stone:
                error = $"Buy a pickaxe for {Balance.PickaxeCost} currency before excavating stone.";
                return false;
            default:
                error = "Only dirt and stone terrain can be excavated.";
                return false;
        }
    }

    /// <summary>Returns false without changing inventory for invalid or overflowing additions.</summary>
    public bool Add(Material material, int count = 1)
    {
        if (!IsBlockMaterial(material) || count < 0) return false;
        long total = (long)GetCount(material) + count;
        if (total > int.MaxValue) return false;
        if (material == Material.Dirt) DirtCount = (int)total;
        else StoneCount = (int)total;
        return true;
    }

    public bool TrySpend(Material material, int count = 1)
    {
        if (!IsBlockMaterial(material) || count < 0 || count > GetCount(material)) return false;
        if (material == Material.Dirt) DirtCount -= count;
        else StoneCount -= count;
        return true;
    }

    public void Reset()
    {
        HasShovel = false;
        HasPickaxe = false;
        DirtCount = 0;
        StoneCount = 0;
    }

    internal void Restore(int dirt, int stone, bool shovel, bool pick)
    {
        if (dirt < 0 || stone < 0)
            throw new InvalidDataException("Saved dirt and stone inventory counts cannot be negative.");
        DirtCount = dirt;
        StoneCount = stone;
        HasShovel = shovel;
        HasPickaxe = pick;
    }

    private static bool IsBlockMaterial(Material material) => material is Material.Dirt or Material.Stone;
}
