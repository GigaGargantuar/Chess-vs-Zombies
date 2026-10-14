using System.Numerics;

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

public enum Material { Air, Stone, Dirt, Core, Barrier, Obsidian }
public enum ToolKind { Shovel, Pickaxe }
public enum PieceKind { Pawn, Knight, Bishop, Rook }
public enum UpgradeKind { HP, ATK, Orthogonal, PlaneDiagonal, SpaceDiagonal, Jump }
public enum ZombieKind { Basic, Armed, Armored, ArmedArmored }
public enum GearTier { Wood, Stone, Copper, Iron, Gold, Diamond, Osmium }
public enum GamePhase { Building, Combat, Defeat }
public enum ShotKind { Melee, Pellet, Lob }
public enum DamageKind { Pierce, Blunt, Chemical, Thermal }
public enum ArmorSlot { Shield, Chestplate, Helmet }

public static class Balance
{
    public const int StartingCurrency = 1000, PullCost = 5, SurvivorIncome = 2;
    public const int TurretHp = 30, BaseAttack = 1, ZombieHp = 10, CoreHp = 200;
    public const int ShovelCost = 100, PickaxeCost = 1000;
    public const int DirtBlockHp = 100, StoneBlockHp = 500;
    public const int LiquidPurchaseUnits = 10;
    public const float AirSeconds = 10, HeatProtectionSecondsPerPiece = 10;
    public const float ChemicalDamagePerSecond = 2, LavaDamagePerSecond = 5;
    public const int DuplicatesPerRank = 10;
    public const int HpUpgradePerPoint = 10, AtkUpgradePerPoint = 1;
    // Retained constant names for source compatibility; ranks set caps, never stat bonuses.
    public const int HpUpgradePerRank = HpUpgradePerPoint, AtkUpgradePerRank = AtkUpgradePerPoint;
    public static int PieceCost(PieceKind kind) => kind switch { PieceKind.Pawn => 1, PieceKind.Knight => 2, _ => 4 };
    public static int BlockHp(Material material) => material switch
    {
        Material.Dirt => DirtBlockHp,
        Material.Stone => StoneBlockHp,
        _ => throw new ArgumentOutOfRangeException(nameof(material), "Only dirt and stone can be reused as defensive blocks.")
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
    public bool Resists(DamageKind kind) => kind == DamageKind.Thermal && Tier == GearTier.Osmium && HeatProtectionRemaining > 0;
}

public sealed class Zombie
{
    private List<ArmorPiece>? _armor;
    private float _bodyHp = Balance.ZombieHp;
    private float? _maximumHp;
    public int Id { get; init; }
    public int WaveIndex { get; init; }
    public ZombieKind Kind { get; init; }
    public GearTier Tier { get; init; }
    public Vector3 Position { get; set; }
    public Vector3 Facing { get; private set; } = Vector3.UnitZ;
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
                _bodyHp = Balance.ZombieHp;
                EnsureArmor();
                ApplySequentialDamage(Math.Max(0, Hp - value));
            }
        }
    }
    public float MaxHp { get => _maximumHp ?? Balance.ZombieHp + (Kind is ZombieKind.Armored or ZombieKind.ArmedArmored ? 3 * Balance.GearHp(Tier) : 0); init => _maximumHp = value; }
    public float AirRemaining { get; internal set; } = Balance.AirSeconds;
    public bool SinksIn(LiquidKind liquid) => liquid switch
    {
        LiquidKind.Water => true,
        LiquidKind.Mercury => ArmorPieces.Any(piece => piece.Tier == GearTier.Osmium),
        LiquidKind.Lava => ArmorPieces.Any(piece => piece.Tier >= GearTier.Iron),
        _ => throw new ArgumentOutOfRangeException(nameof(liquid))
    };
    public int Attack { get; init; }
    public float MoveProgress { get; set; }
    public float Cooldown { get; set; }
    public float DamageSinceLaneSwitch { get; set; }
    public int Lane { get; set; }
    public Cell? NextCell { get; set; }
    public Cell Cell => new((int)MathF.Floor(Position.X), (int)MathF.Floor(Position.Y), (int)MathF.Floor(Position.Z));

    /// <summary>Faces a travel or attack direction while keeping the body upright.</summary>
    public void FaceToward(Vector3 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z)) return;
        var length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Z * direction.Z);
        if (length == 0) return;
        Facing = new Vector3((float)(direction.X / length), 0, (float)(direction.Z / length));
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
                Slot = slot, Tier = Tier, Hp = Balance.GearHp(Tier),
                HeatProtectionRemaining = Tier == GearTier.Osmium ? Balance.HeatProtectionSecondsPerPiece : 0
            });
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
    /// <summary>Travel direction at an age on the arc; magnitude is irrelevant to hit coverage.</summary>
    public Vector3 GetTravelDirection(float age)
    {
        var direction = Target - Origin;
        if (Kind == ShotKind.Lob)
        {
            float t = Math.Clamp(age / Math.Max(.001f, Duration), 0, 1);
            direction.Y += 4 * (1 - 2 * t) * Math.Max(1, Vector3.Distance(Origin, Target) * .4f);
        }
        return direction;
    }
    public Vector3 Position
    {
        get
        {
            float t = Math.Clamp(Age / Math.Max(.001f, Duration), 0, 1);
            var p = Vector3.Lerp(Origin, Target, t);
            if (Kind == ShotKind.Lob) p.Y += 4 * t * (1 - t) * Math.Max(1, Vector3.Distance(Origin, Target) * .4f);
            return p;
        }
    }
}

public sealed class ZombieGroup
{
    public ZombieKind Kind { get; set; }
    public GearTier Tier { get; set; }
    public int Count { get; set; } = 1;
}
public sealed class WaveDefinition
{
    public float SpawnInterval { get; set; } = 1.4f;
    public List<ZombieGroup> Groups { get; set; } = [];
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
    public int TotalCount { get; init; }
    public int Spawned { get; set; }
    public float TotalHp { get; init; }
    public float DamageTaken { get; set; }
    public bool Started { get; set; }
    public float SpawnTimer { get; set; }
}
