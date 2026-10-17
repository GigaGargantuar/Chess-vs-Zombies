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
            if (wave.HpBudget is <= 0)
                throw new InvalidDataException("HpBudget must be positive when specified.");
            if (!float.IsFinite(wave.BossBudgetFraction) || wave.BossBudgetFraction < .01f || wave.BossBudgetFraction > .05f)
                throw new InvalidDataException("BossBudgetFraction must be between 0.01 and 0.05.");
            foreach (var group in wave.Groups)
                if (group == null || group.Count <= 0 || !Enum.IsDefined(group.Kind) || !Enum.IsDefined(group.Tier) ||
                    !Enum.IsDefined(group.Size) || group.IsBoss && group.Size is not (ZombieSize.Kilo or ZombieSize.Mega))
                    throw new InvalidDataException("Groups need positive Count and valid Kind, Tier, and Size values; bosses must be Kilos or Megas.");
            if (wave.HpBudget.HasValue && !wave.Groups.Any(group => !group.IsBoss))
                throw new InvalidDataException("A budgeted wave needs at least one normal spawn type.");
            if (!wave.HpBudget.HasValue && wave.Groups.Sum(group => (long)group.Count) > int.MaxValue)
                throw new InvalidDataException("A wave's total zombie count exceeds the supported integer range.");
        }
    }

    public static int ZombieMaxHp(ZombieKind kind, GearTier tier, ZombieSize size = ZombieSize.Standard)
    {
        int standard = Balance.ZombieHp +
            (kind is ZombieKind.Armored or ZombieKind.ArmedArmored ? 4 * Balance.GearHp(tier) : 0);
        return size switch
        {
            ZombieSize.Micro => 2 + (kind is ZombieKind.Armored or ZombieKind.ArmedArmored ? Balance.GearHp(tier) : 0),
            ZombieSize.Milli => standard / 2,
            ZombieSize.Standard => standard,
            ZombieSize.Kilo => standard * 4,
            ZombieSize.Mega => standard * 9,
            _ => throw new ArgumentOutOfRangeException(nameof(size))
        };
    }
    public static int ZombieAttack(ZombieKind kind, GearTier tier, ZombieSize size = ZombieSize.Standard) =>
        (Balance.BaseAttack + (kind is ZombieKind.Armed or ZombieKind.ArmedArmored ? Balance.WeaponAttack(tier) : 0)) *
        (size == ZombieSize.Mega ? 9 : 1);

    /// <summary>The strongest armor unlocked by the generated campaign, or none before wave five.</summary>
    public static GearTier? HighestAvailableArmorTier(int levelNumber)
    {
        if (levelNumber < 1) throw new ArgumentOutOfRangeException(nameof(levelNumber));
        return levelNumber < 5 ? null : (GearTier)Math.Clamp(levelNumber / 5 - 1, 0, 6);
    }

    private static LevelDefinition Generate(int number)
    {
        var unlockedArmor = HighestAvailableArmorTier(number);
        var tier = unlockedArmor ?? GearTier.Wood;
        int total = checked(6 + number * 2);
        float gearedShare = number < 5 ? 0 : number < 10 ? .2f + (number - 5) * .15f : .85f;
        int geared = (int)Math.Round(total * gearedShare);
        var groups = new List<ZombieGroup>();
        int microCompositionRemainder = 0;
        void AddSizedGroups(ZombieKind kind, GearTier gear, int count)
        {
            int millis = count / 5;
            // Carry the tenth-spawn quota across gear groups so small groups
            // do not make Micros disappear as the campaign unlocks more tiers.
            int micros = (count + microCompositionRemainder) / 10;
            microCompositionRemainder = (count + microCompositionRemainder) % 10;
            if (count - millis - micros > 0)
                groups.Add(new ZombieGroup { Kind = kind, Tier = gear, Count = count - millis - micros });
            if (millis > 0)
                groups.Add(new ZombieGroup { Kind = kind, Tier = gear, Count = millis, Size = ZombieSize.Milli });
            if (micros > 0)
                groups.Add(new ZombieGroup { Kind = kind, Tier = gear, Count = micros, Size = ZombieSize.Micro });
        }
        AddSizedGroups(ZombieKind.Basic, GearTier.Wood, total - geared);
        if (geared > 0)
        {
            int lowerCount = (int)tier;
            int lowerSpawns = lowerCount == 0 ? 0 : Math.Max(lowerCount, (int)Math.Round(geared * .3));
            var tierCounts = new int[lowerCount + 1];
            // Every previous tier retains at least one armored representative.
            if (lowerCount > 0)
            {
                // A triangular cycle gives the more recent lower tiers a greater share.
                int cycle = lowerCount * (lowerCount + 1) / 2;
                int extras = lowerSpawns - lowerCount;
                for (int previous = 0; previous < lowerCount; previous++)
                    tierCounts[previous] = 1 + extras / cycle * (previous + 1) +
                        Math.Clamp(extras % cycle - previous * (previous + 1) / 2, 0, previous + 1);
            }
            tierCounts[lowerCount] = geared - lowerSpawns;
            for (int gear = 0; gear < tierCounts.Length; gear++)
            {
                int count = tierCounts[gear];
                AddSizedGroups(ZombieKind.Armored, (GearTier)gear, (count + 2) / 3);
                AddSizedGroups(ZombieKind.ArmedArmored, (GearTier)gear, (count + 1) / 3);
                AddSizedGroups(ZombieKind.Armed, (GearTier)gear, count / 3);
            }
        }
        const float bossFraction = .03f;
        long normalHp = groups.Sum(group => (long)group.Count * ZombieMaxHp(group.Kind, group.Tier, group.Size));
        int hpBudget = checked((int)decimal.Ceiling(normalHp / (1m - (decimal)bossFraction)));
        groups.Add(new ZombieGroup
        {
            Kind = unlockedArmor.HasValue ? ZombieKind.Armored : ZombieKind.Basic,
            Tier = tier, Size = ZombieSize.Kilo, IsBoss = true
        });
        groups.Add(new ZombieGroup
        {
            Kind = unlockedArmor.HasValue ? ZombieKind.ArmedArmored : ZombieKind.Basic,
            Tier = tier, Size = ZombieSize.Mega, IsBoss = true
        });
        return new LevelDefinition
        {
            Number = number,
            Name = number == 1 ? "First contact" : $"{tier} siege {number}",
            Waves = [new WaveDefinition { SpawnInterval = Math.Max(.3f, 1.4f - number * .025f), Groups = groups,
                HpBudget = hpBudget, BossBudgetFraction = bossFraction }]
        };
    }

    private static LevelDefinition Clone(LevelDefinition level) => new()
    {
        Number = level.Number,
        Name = level.Name,
        Waves = level.Waves.Select(wave => new WaveDefinition
        {
            SpawnInterval = wave.SpawnInterval,
            HpBudget = wave.HpBudget,
            BossBudgetFraction = wave.BossBudgetFraction,
            Groups = wave.Groups.Select(group => new ZombieGroup { Kind = group.Kind, Tier = group.Tier, Count = group.Count,
                Size = group.Size, IsBoss = group.IsBoss }).ToList()
        }).ToList()
    };
}
