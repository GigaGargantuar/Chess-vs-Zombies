using System.Numerics;
using System.Text.Json.Serialization;

namespace ChessVsZombies.Core;

public readonly record struct Cell(int X, int Y, int Z)
{
    public Vector3 Center => new(X + .5f, Y + .5f, Z + .5f);
    public static Cell operator +(Cell a, Cell b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Cell operator -(Cell a, Cell b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public int Chebyshev => Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
    public int Manhattan => Math.Abs(X) + Math.Abs(Y) + Math.Abs(Z);
    public override string ToString() => $"{X}, {Y}, {Z}";
}

public enum Material { Air, Stone, Dirt, Core, Barrier, Obsidian, Wood, Copper, Iron, Gold, Diamond, Osmium }
public enum ToolKind { Shovel, Pickaxe }
public enum PieceKind { Pawn, Knight, Bishop, Rook }
public enum UpgradeKind { HP, ATK, Orthogonal, PlaneDiagonal, SpaceDiagonal, Jump }
public enum ZombieKind { Basic, Armed, Armored, ArmedArmored }
public enum ZombieSize { Standard, Milli, Kilo }
public enum KiloEquipmentMode { Covers, Buckets }
public enum GearTier { Wood, Stone, Copper, Iron, Gold, Diamond, Osmium }
public enum GamePhase { Building, Combat, Defeat }
public enum ShotKind { Melee, Pellet, Lob }
public enum DamageKind { Pierce, Blunt, Chemical, Thermal }
public enum ArmorSlot { Shield, Chestplate, Helmet }
public sealed record BlockDrop([property: JsonRequired] Cell Cell, [property: JsonRequired] Material Content,
    float? Hp = null, bool WasBarrier = false);

public static class Balance
{
    public const int StartingCurrency = 1000, PullCost = 5, SurvivorIncome = 2;
    public const int TurretHp = 30, BaseAttack = 1, ZombieHp = 10, CoreHp = 200;
    public const int ShovelCost = 100, PickaxeCost = 1000;
    public const int DirtBlockHp = 100, StoneBlockHp = 500;
    public const int WoodBlockHp = 250, CopperBlockHp = 1000, IronBlockHp = 2000;
    public const int GoldBlockHp = 3000, DiamondBlockHp = 5000, OsmiumBlockHp = 8000;
    public const int LiquidPurchaseUnits = 10;
    public const float AirSeconds = 10, HeatProtectionSecondsPerPiece = 10;
    public const float ZombieBreathingHeight = .7f;
    public const int FallDamageMinimumBlocks = 4, FallDamagePerBlock = 5;
    public const float ChemicalDamagePerSecond = 5, LavaDamagePerSecond = 10, SteamDamagePerSecond = 3;
    public const int DuplicatesPerRank = 10;
    public const int BucketCapacity = 10, BucketCarrierInterval = 5;
    public const float BucketReach = 2, BucketActionSeconds = 1;
    public const int HpUpgradePerPoint = 10, AtkUpgradePerPoint = 1;
    // Retained constant names for source compatibility; ranks set caps, never stat bonuses.
    public const int HpUpgradePerRank = HpUpgradePerPoint, AtkUpgradePerRank = AtkUpgradePerPoint;
    public static int PieceCost(PieceKind kind) => kind switch { PieceKind.Pawn => 1, PieceKind.Knight => 2, _ => 4 };
    public static bool IsTerrainMaterial(Material material) => material is Material.Dirt or Material.Stone or Material.Wood or
        Material.Copper or Material.Iron or Material.Gold or Material.Diamond or Material.Osmium;
    public static int BlockHp(Material material) => material switch
    {
        Material.Dirt => DirtBlockHp,
        Material.Stone => StoneBlockHp,
        Material.Wood => WoodBlockHp,
        Material.Copper => CopperBlockHp,
        Material.Iron => IronBlockHp,
        Material.Gold => GoldBlockHp,
        Material.Diamond => DiamondBlockHp,
        Material.Osmium => OsmiumBlockHp,
        _ => throw new ArgumentOutOfRangeException(nameof(material), "Only terrain block materials have block durability.")
    };
    public static int BlockSellValue(Material material) => material switch
    {
        Material.Obsidian => 250,
        Material.Wood => 100,
        Material.Stone => 200,
        Material.Copper => 300,
        Material.Iron => 500,
        Material.Gold => 800,
        Material.Diamond => 1300,
        Material.Osmium => 2100,
        _ => 0
    };
    public static int GearHp(GearTier tier) => new[] { 10, 20, 30, 50, 80, 130, 210 }[(int)tier];
    public static int LiquidCost(LiquidKind kind) => kind switch
    {
        LiquidKind.Water => 100,
        LiquidKind.Mercury => 800,
        LiquidKind.Lava => 1000,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static int WeaponAttack(GearTier tier) => new[] { 2, 3, 5, 8, 13, 21, 34 }[(int)tier];
    public static float LaneThreshold(int level) => Math.Max(0f, .5f - .05f * (Math.Max(0, level - 1) / 5));
}

public sealed class UpgradeProgress
{
    public int PullCount { get; set; }
    public int Available { get; set; }
    public int Rank => 1 + Math.Max(0, PullCount - 1) / Balance.DuplicatesPerRank;
    public int Duplicates => Math.Max(0, PullCount - 1);
}

public sealed class Turret
{
    public int Id { get; init; }
    public PieceKind Kind { get; init; }
    public Cell Cell { get; set; }
    public float Hp { get; set; } = Balance.TurretHp;
    public float Cooldown { get; set; }
    public Dictionary<UpgradeKind, int> UpgradePoints { get; } = [];
    public List<Cell> JumpPattern { get; } = [new(2, 0, 1), new(2, 0, -1), new(-2, 0, 1), new(-2, 0, -1), new(1, 0, 2), new(-1, 0, 2), new(1, 0, -2), new(-1, 0, -2)];
}

/// <summary>A reusable request; each click spends only the points the aimed turret can accept.</summary>
public sealed class UpgradeTemplate
{
    public Dictionary<UpgradeKind, int> Points { get; set; } = [];
    public bool UseJumpPattern { get; set; }
    public List<Cell> JumpPattern { get; set; } = [new(2, 0, 1), new(2, 0, -1), new(-2, 0, 1), new(-2, 0, -1), new(1, 0, 2), new(-1, 0, 2), new(1, 0, -2), new(-1, 0, -2)];
}

public sealed class Barrier
{
    public Cell Cell { get; init; }
    public Material Content { get; init; } = Material.Dirt;
    public float Hp { get; set; } = Balance.DirtBlockHp;
}

public sealed class ArmorPiece
{
    public ArmorSlot Slot { get; init; }
    public GearTier Tier { get; init; }
    public float Hp { get; internal set; }
    public float HeatProtectionRemaining { get; internal set; }
    public float DurabilityMultiplier { get; init; } = 1;
    public float MaxHp => Balance.GearHp(Tier) * DurabilityMultiplier;
    public bool Resists(DamageKind kind) => kind == DamageKind.Thermal && Tier == GearTier.Osmium && HeatProtectionRemaining > 0;
}

/// <summary>One physical bucket's payload; different buckets can hold different liquids.</summary>
public sealed class ZombieBucketLoad
{
    public int Units { get; internal set; }
    public LiquidKind? Kind { get; internal set; }
}

public sealed class Zombie
{
    private List<ArmorPiece>? _armor;
    private float _bodyHp = Balance.ZombieHp;
    private float? _maximumHp;
    private int _carriedBlocks = 1;
    private ZombieSize _size;
    private Vector3? _guardFacing;
    public int Id { get; init; }
    public int WaveIndex { get; set; }
    public ZombieKind Kind { get; set; }
    public GearTier Tier { get; set; }
    public int RetreatReturns { get; internal set; }
    public int OriginalBudgetCost { get; internal set; }
    public ZombieSize Size
    {
        get => _size;
        init
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _size = value;
            _bodyHp = MaxBodyHp;
            _armor = null;
        }
    }
    public bool IsBoss { get; init; }
    public float BodyScale => Size switch { ZombieSize.Milli => .5f, ZombieSize.Kilo => 2, _ => 1 };
    public float HealthMultiplier => Size switch { ZombieSize.Milli => .5f, ZombieSize.Kilo => 4, _ => 1 };
    public float MaxBodyHp => Balance.ZombieHp * HealthMultiplier;
    public Vector3 BodyHalfExtents => Size == ZombieSize.Kilo ? Vector3.One : new Vector3(.2f, .39f, .2f) * BodyScale;
    public int GridFootprint => Size == ZombieSize.Kilo ? 2 : 1;
    public float StandingHeight => .5f * BodyScale;
    public bool HasWeapon => Kind is ZombieKind.Armed or ZombieKind.ArmedArmored;
    public float MovementSpeed => NavigationCosts.WalkTilesPerSecond * (HasWeapon ? 1 : 1.5f) *
        (Size switch { ZombieSize.Milli => 2, ZombieSize.Kilo => .75f, _ => 1 });
    public Vector3 StandAt(Cell cell) => cell.Center + new Vector3((GridFootprint - 1) * .5f, StandingHeight - .5f, (GridFootprint - 1) * .5f);
    public Vector3 Position { get; set; }
    public Vector3 Facing { get; private set; } = Vector3.UnitZ;
    private bool _carriesBucket;
    public bool CarriesBucket { get => Size == ZombieSize.Kilo || _carriesBucket; set => _carriesBucket = value; }
    public KiloEquipmentMode KiloEquipmentMode { get; internal set; } = KiloEquipmentMode.Covers;
    public float KiloBucketActionRemaining { get; internal set; }
    private readonly ZombieBucketLoad[] _bucketLoads = [new(), new()];
    public int BucketCount => Size == ZombieSize.Kilo ? 2 : 1;
    public IReadOnlyList<ZombieBucketLoad> BucketLoads => _bucketLoads;
    public int HeldBucketUnits => _bucketLoads[0].Units + _bucketLoads[1].Units;
    // Preserve the original one-bucket API for existing callers and checkpoints.
    public int BucketUnits { get => _bucketLoads[0].Units; internal set => _bucketLoads[0].Units = value; }
    public LiquidKind? BucketKind { get => _bucketLoads[0].Kind; internal set => _bucketLoads[0].Kind = value; }
    public int CarriedBlocks { get => CarriesBucket && Size != ZombieSize.Kilo ? 0 : _carriedBlocks; internal set => _carriedBlocks = value; }
    public Material CarriedBlockMaterial => Kind == ZombieKind.Basic ? Material.Dirt : Tier switch
    {
        GearTier.Wood => Material.Wood,
        GearTier.Stone => Material.Stone,
        GearTier.Copper => Material.Copper,
        GearTier.Iron => Material.Iron,
        GearTier.Gold => Material.Gold,
        GearTier.Diamond => Material.Diamond,
        GearTier.Osmium => Material.Osmium,
        _ => throw new ArgumentOutOfRangeException(nameof(Tier))
    };
    public float BodyHp => _bodyHp;
    public IReadOnlyList<ArmorPiece> ArmorPieces { get { EnsureArmor(); return _armor!; } }
    // Hp remains the complete surviving HP pool for wave accounting and older callers.
    // Assignments retain the old sequential total-HP contract independently of hit coverage.
    public float Hp
    {
        get => _bodyHp <= 0 ? 0 : _bodyHp + ArmorPieces.Sum(piece => piece.Hp);
        set
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            EnsureArmor();
            if (value <= Hp) ApplySequentialDamage(Hp - value);
            else
            {
                _armor = null;
                _bodyHp = MaxBodyHp;
                EnsureArmor();
                ApplySequentialDamage(Math.Max(0, Hp - value));
            }
        }
    }
    public float MaxHp { get => _maximumHp ?? (Balance.ZombieHp + (Kind is ZombieKind.Armored or ZombieKind.ArmedArmored ? 3 * Balance.GearHp(Tier) : 0)) * HealthMultiplier; init => _maximumHp = value; }
    public float AirRemaining { get; internal set; } = Balance.AirSeconds;
    public bool SinksIn(LiquidKind liquid) => liquid switch
    {
        LiquidKind.Water => true,
        LiquidKind.Mercury => ArmorPieces.Any(piece => piece.Tier == GearTier.Osmium),
        LiquidKind.Lava => ArmorPieces.Any(piece => piece.Tier >= GearTier.Iron),
        _ => throw new ArgumentOutOfRangeException(nameof(liquid))
    };
    public int Attack { get; set; }
    public float MoveProgress { get; set; }
    public float Cooldown { get; set; }
    public float DamageSinceLaneSwitch { get; set; }
    public int Lane { get; set; }
    public Cell? NextCell { get; set; }
    public Cell Cell => CellAt(Position);
    public Cell CellAt(Vector3 position) => new((int)MathF.Floor(position.X - (GridFootprint - 1) * .5f),
        (int)MathF.Floor(position.Y - StandingHeight + .5f), (int)MathF.Floor(position.Z - (GridFootprint - 1) * .5f));

    /// <summary>Faces a travel or attack direction while keeping the body upright.</summary>
    public void FaceToward(Vector3 direction)
    {
        if (_guardFacing is { } guard) { Facing = guard; return; }
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z)) return;
        var length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Z * direction.Z);
        if (length == 0) return;
        Facing = new Vector3((float)(direction.X / length), 0, (float)(direction.Z / length));
    }

    /// <summary>Keeps cover toward the latest incoming fire independently of the carrier's travel direction.</summary>
    internal void SetGuardFacing(Vector3? direction)
    {
        if (direction is not { } value) { _guardFacing = null; return; }
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) return;
        var length = Math.Sqrt((double)value.X * value.X + (double)value.Z * value.Z);
        if (length == 0) return;
        _guardFacing = new Vector3((float)(value.X / length), 0, (float)(value.Z / length));
        Facing = _guardFacing.Value;
    }

    private void EnsureArmor()
    {
        if (_armor != null) return;
        _armor = [];
        if (Kind is not (ZombieKind.Armored or ZombieKind.ArmedArmored)) return;
        // Stable slot order also defines compatibility HP assignment and heat-budget consumption.
        foreach (var slot in Enum.GetValues<ArmorSlot>())
            _armor.Add(new ArmorPiece
            {
                Slot = slot, Tier = Tier, Hp = Balance.GearHp(Tier) * HealthMultiplier,
                DurabilityMultiplier = HealthMultiplier,
                HeatProtectionRemaining = Tier == GearTier.Osmium ? Balance.HeatProtectionSecondsPerPiece : 0
            });
    }

    internal float ReequipArmor(GearTier? tier)
    {
        float before = Hp;
        if (tier.HasValue)
        {
            Tier = tier.Value;
            Kind = HasWeapon ? ZombieKind.ArmedArmored : ZombieKind.Armored;
        }
        _maximumHp = null;
        _armor = null;
        EnsureArmor();
        Attack = LevelCatalog.ZombieAttack(Kind, Tier);
        return Hp - before;
    }

    internal void RestoreBodyAndArmor(float bodyHp, IEnumerable<ArmorPiece> armor)
    {
        _bodyHp = bodyHp;
        _armor = armor.ToList();
        _maximumHp = null;
    }

    internal void ApplyDamage(float damage, DamageKind kind, ShotKind? shot = null, Vector3? incomingDirection = null)
    {
        if (!float.IsFinite(damage) || damage <= 0 || _bodyHp <= 0 || !Enum.IsDefined(kind) ||
            (shot.HasValue && !Enum.IsDefined(shot.Value))) return;
        EnsureArmor();
        if (kind is DamageKind.Pierce or DamageKind.Blunt)
        {
            if (IsShieldFacingHit(incomingDirection)) AbsorbWith(ArmorSlot.Shield, ref damage);
            var delivery = shot ?? (kind == DamageKind.Pierce ? ShotKind.Pellet : ShotKind.Melee);
            if (delivery == ShotKind.Lob) AbsorbWith(ArmorSlot.Helmet, ref damage);
            else if (delivery == ShotKind.Pellet) AbsorbWith(ArmorSlot.Chestplate, ref damage);
        }
        _bodyHp = Math.Max(0, _bodyHp - damage);
        if (_bodyHp <= 0) SetGuardFacing(null);
    }

    private bool IsShieldFacingHit(Vector3? incomingDirection)
    {
        if (incomingDirection is not { } incoming || !float.IsFinite(incoming.X) ||
            !float.IsFinite(incoming.Y) || !float.IsFinite(incoming.Z)) return false;
        // Incoming is travel into the body. Shield coverage is measured toward its source.
        double x = -incoming.X, y = -incoming.Y, z = -incoming.Z;
        var horizontalLength = Math.Sqrt(x * x + z * z);
        if (horizontalLength == 0) return false;
        var horizontalCosine = (x * Facing.X + z * Facing.Z) / horizontalLength;
        // Independent total 90-degree horizontal and 60-degree vertical windows.
        // A tiny tolerance keeps exact boundary directions inclusive after float rounding.
        return horizontalCosine >= Math.Cos(Math.PI / 4) - .000001 &&
            Math.Abs(y) <= horizontalLength * Math.Tan(Math.PI / 6) + horizontalLength * .000001;
    }

    private void AbsorbWith(ArmorSlot slot, ref float damage)
    {
        var pieces = _armor!;
        if (damage <= 0 || pieces.FirstOrDefault(piece => piece.Slot == slot) is not { } armor) return;
        var absorbed = Math.Min(armor.Hp, damage);
        armor.Hp -= absorbed;
        damage -= absorbed;
        if (armor.Hp <= 0) pieces.Remove(armor);
    }

    private void ApplySequentialDamage(float damage)
    {
        if (damage <= 0 || _bodyHp <= 0) return;
        EnsureArmor();
        foreach (var slot in Enum.GetValues<ArmorSlot>()) AbsorbWith(slot, ref damage);
        _bodyHp = Math.Max(0, _bodyHp - damage);
        if (_bodyHp <= 0) SetGuardFacing(null);
    }

    internal float ConsumeHeatProtection(float seconds)
    {
        foreach (var piece in ArmorPieces)
        {
            if (!piece.Resists(DamageKind.Thermal)) continue;
            var absorbed = Math.Min(seconds, piece.HeatProtectionRemaining);
            piece.HeatProtectionRemaining -= absorbed;
            seconds -= absorbed;
            if (seconds <= 0) break;
        }
        return seconds;
    }
}

public sealed class Projectile
{
    public Vector3 Origin { get; init; }
    public Vector3 Target { get; init; }
    public int TargetId { get; init; }
    public float Damage { get; init; }
    public ShotKind Kind { get; init; }
    public DamageKind DamageKind => Kind == ShotKind.Pellet ? DamageKind.Pierce : DamageKind.Blunt;
    public float Age { get; set; }
    public float Duration { get; init; }
    public float FlightDuration => Math.Max(.001f, Duration);
    /// <summary>Travel direction at an age on the arc; magnitude is irrelevant to hit coverage.</summary>
    public Vector3 GetTravelDirection(float age)
    {
        var direction = Target - Origin;
        if (Kind == ShotKind.Lob)
        {
            float t = Math.Max(0, age) / FlightDuration;
            direction.Y += 4 * (1 - 2 * t) * Math.Max(1, Vector3.Distance(Origin, Target) * .4f);
        }
        return direction;
    }
    /// <summary>The launch trajectory continues past its initial aim point without retargeting.</summary>
    public Vector3 GetPositionAt(float age)
    {
        float t = Math.Max(0, age) / FlightDuration;
        if (Kind == ShotKind.Melee) t = Math.Min(1, t);
        var position = Origin + (Target - Origin) * t;
        if (Kind == ShotKind.Lob) position.Y += 4 * t * (1 - t) * Math.Max(1, Vector3.Distance(Origin, Target) * .4f);
        return position;
    }
    public Vector3 Position => GetPositionAt(Age);
}

public sealed class ZombieGroup
{
    public ZombieKind Kind { get; set; }
    public GearTier Tier { get; set; }
    public int Count { get; set; } = 1;
    public ZombieSize Size { get; set; } = ZombieSize.Standard;
    public bool IsBoss { get; set; }
}
public sealed class WaveDefinition
{
    public float SpawnInterval { get; set; } = 1.4f;
    public List<ZombieGroup> Groups { get; set; } = [];
    public int? HpBudget { get; set; }
    public float BossBudgetFraction { get; set; } = .03f;
}
public sealed class LevelDefinition
{
    public int Number { get; set; } = 1;
    public string Name { get; set; } = "First contact";
    public List<WaveDefinition> Waves { get; set; } = [];
}
public sealed class WaveState
{
    public int Index { get; init; }
    public int TotalCount { get; set; }
    public int Spawned { get; set; }
    public float TotalHp { get; set; }
    public long HpBudget { get; set; }
    public long NormalBudget { get; set; }
    public long BossBudget { get; set; }
    public int ReinforcementsSpawned { get; set; }
    public long BudgetSpent { get; set; }
    public float DamageTaken { get; set; }
    public bool Started { get; set; }
    public float SpawnTimer { get; set; }
}
