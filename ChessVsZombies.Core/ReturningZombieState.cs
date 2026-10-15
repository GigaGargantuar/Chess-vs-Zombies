using System.Text.Json.Serialization;

namespace ChessVsZombies.Core;

public sealed class SavedReturningZombie
{
    [JsonRequired] public int TargetLevel { get; set; }
    [JsonRequired] public int TargetWaveIndex { get; set; }
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public ZombieKind Kind { get; set; }
    [JsonRequired] public GearTier Tier { get; set; }
    [JsonRequired] public ZombieSize Size { get; set; }
    [JsonRequired] public bool IsBoss { get; set; }
    [JsonRequired] public float BodyHp { get; set; }
    [JsonRequired] public int RetreatReturns { get; set; }
    [JsonRequired] public int OriginalBudgetCost { get; set; }
    [JsonRequired] public bool CarriesBucket { get; set; }
    [JsonRequired] public int CarriedBlocks { get; set; }
    [JsonRequired] public List<SavedZombieArmor> Armor { get; set; } = [];
    [JsonRequired] public List<SavedZombieBucket> Buckets { get; set; } = [];
    [JsonRequired] public List<SavedKiloBlock> HeldBlocks { get; set; } = [];

    internal SavedReturningZombie Copy() => new()
    {
        TargetLevel = TargetLevel, TargetWaveIndex = TargetWaveIndex, Id = Id,
        Kind = Kind, Tier = Tier, Size = Size, IsBoss = IsBoss, BodyHp = BodyHp,
        RetreatReturns = RetreatReturns, OriginalBudgetCost = OriginalBudgetCost,
        CarriesBucket = (Size is ZombieSize.Kilo or ZombieSize.Mega) || CarriesBucket, CarriedBlocks = CarriedBlocks,
        Armor = Armor.Select(piece => new SavedZombieArmor
        { Slot = piece.Slot, Tier = piece.Tier, Hp = piece.Hp, HeatProtectionRemaining = piece.HeatProtectionRemaining }).ToList(),
        // Earlier Kilos exchanged their entire block loadout for buckets. Give
        // those saved block carriers the now-standard two empty buckets while
        // retaining every existing block and every actual liquid payload.
        Buckets = (Size is ZombieSize.Kilo or ZombieSize.Mega) && !CarriesBucket && Buckets.Count == 0
            ? [new(), new()]
            : Buckets.Select(load => new SavedZombieBucket { Units = load.Units, Kind = load.Kind }).ToList(),
        HeldBlocks = HeldBlocks.Select(block => new SavedKiloBlock
        {
            BlockIndex = block.BlockIndex, Material = block.Material, Hp = block.Hp, WasBarrier = block.WasBarrier,
            OffsetX = block.OffsetX, OffsetY = block.OffsetY, OffsetZ = block.OffsetZ
        }).ToList()
    };
}

public sealed class SavedZombieArmor
{
    [JsonRequired] public ArmorSlot Slot { get; set; }
    [JsonRequired] public GearTier Tier { get; set; }
    [JsonRequired] public float Hp { get; set; }
    [JsonRequired] public float HeatProtectionRemaining { get; set; }
}

public sealed class SavedZombieBucket
{
    [JsonRequired] public int Units { get; set; }
    [JsonRequired] public LiquidKind? Kind { get; set; }
}

public sealed class SavedKiloBlock
{
    [JsonRequired] public int BlockIndex { get; set; }
    [JsonRequired] public Material Material { get; set; }
    [JsonRequired] public float Hp { get; set; }
    [JsonRequired] public bool WasBarrier { get; set; }
    [JsonRequired] public float OffsetX { get; set; }
    [JsonRequired] public float OffsetY { get; set; }
    [JsonRequired] public float OffsetZ { get; set; }
}
