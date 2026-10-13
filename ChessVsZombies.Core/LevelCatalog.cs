using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessVsZombies.Core;

/// <summary>Data-driven waves. JSON files override generated levels by their Number.</summary>
public sealed class LevelCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly Dictionary<int, LevelDefinition> _levels = [];
    public IReadOnlyList<string> LoadErrors => _loadErrors;
    private readonly List<string> _loadErrors = [];

    public LevelCatalog(string? directory = null)
    {
        if (directory != null) LoadDirectory(directory);
    }

    public void LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var level = JsonSerializer.Deserialize<LevelDefinition>(File.ReadAllText(file), JsonOptions)
                    ?? throw new InvalidDataException("The file must contain a level object.");
                Set(level);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _loadErrors.Add($"{Path.GetFileName(file)}: {error.Message}");
            }
        }
    }

    public void Set(LevelDefinition level)
    {
        Validate(level);
        // Protect the catalog from accidental edits to authoring objects during a running wave.
        _levels[level.Number] = Clone(level);
    }

    public LevelDefinition Get(int number)
    {
        if (number < 1) throw new ArgumentOutOfRangeException(nameof(number));
        return _levels.TryGetValue(number, out var level) ? Clone(level) : Generate(number);
    }

    public static void Validate(LevelDefinition level)
    {
        if (level.Number < 1) throw new InvalidDataException("Number must be positive.");
        if (string.IsNullOrWhiteSpace(level.Name)) throw new InvalidDataException("Name is required.");
        if (level.Waves == null || level.Waves.Count == 0) throw new InvalidDataException("At least one wave is required.");
        foreach (var wave in level.Waves)
        {
            if (wave == null || !float.IsFinite(wave.SpawnInterval) || wave.SpawnInterval <= 0)
                throw new InvalidDataException("Each wave needs a finite, positive SpawnInterval.");
            if (wave.Groups == null || wave.Groups.Count == 0)
                throw new InvalidDataException("Each wave needs at least one zombie group.");
            foreach (var group in wave.Groups)
                if (group == null || group.Count <= 0 || !Enum.IsDefined(group.Kind) || !Enum.IsDefined(group.Tier))
                    throw new InvalidDataException("Groups need positive Count and valid Kind and Tier values.");
            if (wave.Groups.Sum(group => (long)group.Count) > int.MaxValue)
                throw new InvalidDataException("A wave's total zombie count exceeds the supported integer range.");
        }
    }

    public static int ZombieMaxHp(ZombieKind kind, GearTier tier) => Balance.ZombieHp +
        (kind is ZombieKind.Armored or ZombieKind.ArmedArmored ? 3 * Balance.GearHp(tier) : 0);
    public static int ZombieAttack(ZombieKind kind, GearTier tier) => Balance.BaseAttack +
        (kind is ZombieKind.Armed or ZombieKind.ArmedArmored ? Balance.WeaponAttack(tier) : 0);

    private static LevelDefinition Generate(int number)
    {
        var tier = (GearTier)Math.Min(6, (number - 1) / 5);
        var groups = new List<ZombieGroup> { new() { Kind = ZombieKind.Basic, Tier = tier, Count = 6 + number * 2 } };
        if (number >= 2) groups.Add(new() { Kind = ZombieKind.Armed, Tier = tier, Count = 1 + number / 3 });
        if (number >= 3) groups.Add(new() { Kind = ZombieKind.Armored, Tier = tier, Count = 1 + number / 4 });
        if (number >= 5) groups.Add(new() { Kind = ZombieKind.ArmedArmored, Tier = tier, Count = 1 + number / 5 });
        return new LevelDefinition
        {
            Number = number,
            Name = number == 1 ? "First contact" : $"{tier} siege {number}",
            Waves = [new WaveDefinition { SpawnInterval = Math.Max(.3f, 1.4f - number * .025f), Groups = groups }]
        };
    }

    private static LevelDefinition Clone(LevelDefinition level) => new()
    {
        Number = level.Number,
        Name = level.Name,
        Waves = level.Waves.Select(wave => new WaveDefinition
        {
            SpawnInterval = wave.SpawnInterval,
            Groups = wave.Groups.Select(group => new ZombieGroup { Kind = group.Kind, Tier = group.Tier, Count = group.Count }).ToList()
        }).ToList()
    };
}
