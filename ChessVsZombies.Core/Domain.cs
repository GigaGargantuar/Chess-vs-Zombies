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

public enum Material { Air, Stone, Dirt, Core, Barrier }
public enum ToolKind { Shovel, Pickaxe }
public enum PieceKind { Pawn, Knight, Bishop, Rook }
public enum UpgradeKind { HP, ATK, Orthogonal, PlaneDiagonal, SpaceDiagonal, Jump }
public enum ZombieKind { Basic, Armed, Armored, ArmedArmored }
public enum GearTier { Wood, Stone, Copper, Iron, Gold, Diamond, Osmium }
public enum GamePhase { Building, Combat, Defeat }
public enum ShotKind { Melee, Pellet, Lob }

public static class Balance
{
    public const int StartingCurrency = 1000, PullCost = 5, SurvivorIncome = 2;
    public const int TurretHp = 30, BaseAttack = 1, ZombieHp = 10, CoreHp = 200;
    public const int ShovelCost = 100, PickaxeCost = 1000;
    public const int DirtBlockHp = 100, StoneBlockHp = 500;
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

/// <summary>A reusable allocation to spend on the aimed turret as one transaction.</summary>
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

public sealed class Zombie
{
    public int Id { get; init; }
    public int WaveIndex { get; init; }
    public ZombieKind Kind { get; init; }
    public GearTier Tier { get; init; }
    public Vector3 Position { get; set; }
    public float Hp { get; set; }
    public float MaxHp { get; init; }
    public int Attack { get; init; }
    public float MoveProgress { get; set; }
    public float Cooldown { get; set; }
    public float DamageSinceLaneSwitch { get; set; }
    public int Lane { get; set; }
    public Cell? NextCell { get; set; }
    public Cell Cell => new((int)MathF.Floor(Position.X), (int)MathF.Floor(Position.Y), (int)MathF.Floor(Position.Z));
}

public sealed class Projectile
{
    public Vector3 Origin { get; init; }
    public Vector3 Target { get; init; }
    public int TargetId { get; init; }
    public float Damage { get; init; }
    public ShotKind Kind { get; init; }
    public float Age { get; set; }
    public float Duration { get; init; }
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
