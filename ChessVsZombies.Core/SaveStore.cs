using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessVsZombies.Core;

public sealed class SavedUpgradeProgress
{
    [JsonRequired]
    public int PullCount { get; set; }
    [JsonRequired]
    public int Available { get; set; }
}

public sealed class EconomyState
{
    [JsonRequired]
    public int Currency { get; set; }
    [JsonRequired]
    public ulong RandomState { get; set; }
    [JsonRequired]
    public Dictionary<UpgradeKind, SavedUpgradeProgress> Upgrades { get; set; } = [];
}

public sealed class SavedTurret
{
    [JsonRequired]
    public int Id { get; set; }
    [JsonRequired]
    public PieceKind Kind { get; set; }
    [JsonRequired]
    public Cell Cell { get; set; }
    [JsonRequired]
    public float Hp { get; set; }
    // Both shapes are accepted during deserialization; version-specific validation
    // requires exactly the representation belonging to that checkpoint version.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<UpgradeKind, int>? UpgradePoints { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<UpgradeKind>? Upgrades { get; set; }
    [JsonRequired]
    public List<Cell> JumpPattern { get; set; } = [];
}

public sealed class SavedBarrier
{
    [JsonRequired]
    public Cell Cell { get; set; }
    [JsonRequired]
    public float Hp { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Material? Content { get; set; }
}

public sealed class SavedTerrainDamage
{
    [JsonRequired]
    public Cell Cell { get; set; }
    [JsonRequired]
    public float Hp { get; set; }
}

public sealed class SavedTerrainPlacement
{
    [JsonRequired]
    public Cell Cell { get; set; }
    [JsonRequired]
    public Material Material { get; set; }
}

public sealed class BlockInventoryState
{
    [JsonRequired]
    public int DirtCount { get; set; }
    [JsonRequired]
    public int StoneCount { get; set; }
    [JsonRequired]
    public bool HasShovel { get; set; }
    [JsonRequired]
    public bool HasPickaxe { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<Material, int>? AdditionalMaterials { get; set; }
    // Optional additive v8 field: checkpoints created before obsidian mining default to zero.
    public int ObsidianLayers { get; set; }
}

public sealed class SavedUpgradeTemplate
{
    [JsonRequired]
    public Dictionary<UpgradeKind, int> Points { get; set; } = [];
    [JsonRequired]
    public bool UseJumpPattern { get; set; }
    [JsonRequired]
    public List<Cell> JumpPattern { get; set; } = [];

    internal static SavedUpgradeTemplate FromTemplate(UpgradeTemplate template) => new()
    {
        Points = new Dictionary<UpgradeKind, int>(template.Points),
        UseJumpPattern = template.UseJumpPattern,
        JumpPattern = template.JumpPattern.ToList()
    };

    internal UpgradeTemplate ToTemplate() => new()
    {
        Points = new Dictionary<UpgradeKind, int>(Points),
        UseJumpPattern = UseJumpPattern,
        JumpPattern = JumpPattern.ToList()
    };
}

/// <summary>A preparation-phase checkpoint. Active waves restart from this snapshot.</summary>
public sealed class CampaignState
{
    [JsonRequired]
    public int Version { get; set; } = 8;
    [JsonRequired]
    public EconomyState Economy { get; set; } = new();
    [JsonRequired]
    public int LevelNumber { get; set; } = 1;
    // Optional preparation preference; older checkpoints continue one level at a time.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MergeFiveLevels { get; set; }
    [JsonRequired]
    public float CoreHp { get; set; } = Balance.CoreHp;
    [JsonRequired]
    public List<SavedTurret> Turrets { get; set; } = [];
    [JsonRequired]
    public List<SavedBarrier> Barriers { get; set; } = [];
    [JsonRequired]
    public List<Cell> Excavated { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlockInventoryState? Inventory { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SavedTerrainDamage>? DamagedTerrain { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<LiquidKind, int>? LiquidInventory { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiquidState? Liquids { get; set; }
    // Additive checkpoint data: older saves have no in-flight or pending bucket liquid.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BucketSystemState? Buckets { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SavedTerrainPlacement>? TerrainPlacements { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SavedUpgradeTemplate? UpgradeTemplate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UpgradeBatchState? QueuedUpgradeBatch { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BlockDrop>? PendingBlockDrops { get; set; }
    // Optional additive v8 data: campaigns saved before returning enemies have no queue.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SavedReturningZombie>? ReturningZombies { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SavedMegaWeaponFlight>? MegaWeaponFlights { get; set; }
}

public static class SaveStore
{
    private const int LegacyBarrierHp = 40;
    private const float LegacyObsidianHpPerUnit = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new CellConverter() }
    };

    public static void Save(GameSession session, string path)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Phase != GamePhase.Building)
            throw new InvalidOperationException("Campaign checkpoints can only be saved while building.");
        var state = session.ExportState();
        Validate(state);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            // The temporary file lives on the same volume. Existing checkpoints are replaced
            // only after the complete new checkpoint has been written and flushed.
            if (File.Exists(fullPath))
            {
                PreserveLegacyBackup(fullPath);
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            }
            else File.Move(temporaryPath, fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static bool TrySave(GameSession session, string path, out string error)
    {
        try
        {
            Save(session, path);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (IsSaveException(exception))
        {
            error = $"Could not save campaign: {exception.Message}";
            return false;
        }
    }

    /// <summary>Saves committed template edits without replacing the prepared battlefield during combat.</summary>
    public static bool TrySaveUpgradeTemplate(UpgradeTemplate template, string path, out string error)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(template);
            var saved = SavedUpgradeTemplate.FromTemplate(template);
            ValidateUpgradeTemplate(saved);
            if (!TryLoad(path, out var prepared, out error))
            {
                if (string.IsNullOrEmpty(error)) error = "No preparation checkpoint is available to save this template.";
                return false;
            }
            var copy = saved.ToTemplate();
            prepared!.UpgradeTemplate.Points = copy.Points;
            prepared.UpgradeTemplate.UseJumpPattern = copy.UseJumpPattern;
            prepared.UpgradeTemplate.JumpPattern = copy.JumpPattern;
            Save(prepared, path);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (IsSaveException(exception))
        {
            error = $"Could not save upgrade template: {exception.Message}";
            return false;
        }
    }

    /// <summary>Clear a finished battle's selection without replacing its preparation checkpoint.</summary>
    public static bool TryClearMergeSelection(string path, out string error)
    {
        try
        {
            if (!TryLoad(path, out var prepared, out error))
            {
                if (string.IsNullOrEmpty(error)) error = "No preparation checkpoint is available to clear the merge selection.";
                return false;
            }
            if (!prepared!.MergeFiveLevels) return true;
            if (!prepared.SetMergeFiveLevels(false))
                throw new InvalidOperationException(prepared.Message);
            Save(prepared, path);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (IsSaveException(exception))
        {
            error = $"Could not clear merge selection: {exception.Message}";
            return false;
        }
    }

    /// <summary>A missing file returns false with no error. Failed loads never modify the file.</summary>
    public static bool TryLoad(string path, out GameSession? session, out string error, LevelCatalog? levels = null)
    {
        session = null;
        error = string.Empty;
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            var state = JsonSerializer.Deserialize<CampaignState>(stream, JsonOptions)
                ?? throw new InvalidDataException("The checkpoint does not contain a campaign.");
            Validate(state);
            var originalVersion = state.Version;
            var migrationNotice = state.Version == 1 ? MigrateLegacy(state) : string.Empty;
            if (state.Version < 3)
                migrationNotice = string.Join(" ", new[] { migrationNotice, MigrateBlockInventory(state, originalVersion) }.Where(text => text.Length > 0));
            if (state.Version < 4)
                migrationNotice = string.Join(" ", new[] { migrationNotice, MigrateTerrainDamage(state, originalVersion) }.Where(text => text.Length > 0));
            if (state.Version < 5)
                migrationNotice = string.Join(" ", new[] { migrationNotice, MigrateLiquids(state, originalVersion) }.Where(text => text.Length > 0));
            if (state.Version < 6)
                migrationNotice = string.Join(" ", new[] { migrationNotice, MigrateTerrainPlacements(state, originalVersion) }.Where(text => text.Length > 0));
            if (state.Version < 7)
                migrationNotice = string.Join(" ", new[] { migrationNotice, MigrateUpgradeTemplate(state, originalVersion) }.Where(text => text.Length > 0));
            if (state.Version < 8)
                migrationNotice = string.Join(" ", new[] { migrationNotice, MigrateMaterialInventory(state, originalVersion) }.Where(text => text.Length > 0));
            Validate(state);
            var restored = new GameSession(levels: levels);
            restored.RestoreCheckpoint(state);
            restored.MigrationNotice = migrationNotice;
            LevelCatalog.Validate(restored.Level);
            session = restored;
            return true;
        }
        catch (Exception exception) when (IsSaveException(exception))
        {
            error = $"Could not load campaign; the original file was preserved. {exception.Message}";
            return false;
        }
    }

    private static bool IsSaveException(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException;

    private static void Validate(CampaignState state)
    {
        if (state.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8)) throw new InvalidDataException($"Unsupported checkpoint version {state.Version}.");
        var legacy = state.Version == 1;
        var legacyBlocks = state.Version < 3;
        if (state.LevelNumber < 1 || !float.IsFinite(state.CoreHp) || state.CoreHp <= 0 || state.CoreHp > Balance.CoreHp)
            throw new InvalidDataException("The saved level or objective HP is invalid.");
        if (state.Economy is null || state.Turrets is null || state.Barriers is null || state.Excavated is null)
            throw new InvalidDataException("The checkpoint is missing its economy or battlefield collections.");
        if (legacyBlocks ? state.Inventory != null : state.Inventory == null)
            throw new InvalidDataException("The block inventory does not match the checkpoint version.");
        if (state.Inventory != null && (state.Inventory.DirtCount < 0 || state.Inventory.StoneCount < 0 || state.Inventory.ObsidianLayers < 0))
            throw new InvalidDataException("Saved block inventory counts must be nonnegative.");
        if (state.Version < 8 ? state.Inventory?.AdditionalMaterials != null : state.Inventory?.AdditionalMaterials == null)
            throw new InvalidDataException("The additional material inventory does not match the checkpoint version.");
        if (state.Version >= 8) new BlockInventory().Restore(state.Inventory!);
        if (state.Version < 8 ? state.PendingBlockDrops != null : state.PendingBlockDrops == null)
            throw new InvalidDataException("Pending block drops do not match the checkpoint version.");
        if (state.PendingBlockDrops?.Any(drop => drop is null || !WorldGrid.IsInside(drop.Cell) || !Balance.IsTerrainMaterial(drop.Content) ||
                drop.Hp is { } hp && (!float.IsFinite(hp) || hp <= 0 || hp > Balance.BlockHp(drop.Content))) == true)
            throw new InvalidDataException("A pending block drop needs an in-grid location and a terrain material.");
        if (state.Version < 4 ? state.DamagedTerrain != null : state.DamagedTerrain == null)
            throw new InvalidDataException("The natural terrain damage data does not match the checkpoint version.");
        if (state.Version < 5 ? state.LiquidInventory != null || state.Liquids != null : state.LiquidInventory == null || state.Liquids == null)
            throw new InvalidDataException("The liquid inventory or simulation data does not match the checkpoint version.");
        if (state.Version < 6 ? state.TerrainPlacements != null : state.TerrainPlacements == null)
            throw new InvalidDataException("The moved terrain data does not match the checkpoint version.");
        if (state.Version < 7 ? state.UpgradeTemplate != null : state.UpgradeTemplate == null)
            throw new InvalidDataException("The upgrade template does not match the checkpoint version.");
        if (state.UpgradeTemplate != null) ValidateUpgradeTemplate(state.UpgradeTemplate);
        if (state.LiquidInventory != null && (state.LiquidInventory.Count != Enum.GetValues<LiquidKind>().Length ||
            state.LiquidInventory.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value < 0) ||
            Enum.GetValues<LiquidKind>().Any(kind => !state.LiquidInventory.ContainsKey(kind))))
            throw new InvalidDataException("The liquid inventory requires a nonnegative unit count for each liquid kind.");
        if (state.Excavated.Count > 16 * WorldGrid.Size * WorldGrid.Size ||
            state.DamagedTerrain?.Count > (state.Version >= 8 ? WorldGrid.Size : 16) * WorldGrid.Size * WorldGrid.Size ||
            state.TerrainPlacements?.Count > WorldGrid.Size * WorldGrid.Size * WorldGrid.Size ||
            state.Barriers.Count > (state.Version >= 7 ? 30 * 31 * 30 : state.Version >= 6 ? 8 * 20 * 8 : 8 * 8 * 8) ||
            state.Turrets.Count > (state.Version >= 6 ? 30 * 31 * 30 : 30 * 30 * 30))
            throw new InvalidDataException("A saved collection exceeds the battlefield's capacity.");

        var economy = new Economy();
        economy.ImportState(state.Economy);
        var world = new WorldGrid();
        var dug = new HashSet<Cell>();
        foreach (var cell in state.Excavated)
        {
            if (!WorldGrid.IsInside(cell) || cell.Y >= 16 || WorldGrid.IsCore(cell) || !dug.Add(cell) || !world.Dig(cell))
                throw new InvalidDataException($"Invalid or duplicate excavated cell: {cell}.");
        }

        var placedCells = new HashSet<Cell>();
        foreach (var placement in state.TerrainPlacements ?? [])
        {
            if (placement is null || !WorldGrid.IsInside(placement.Cell) ||
                !(state.Version >= 8 ? Balance.IsTerrainMaterial(placement.Material) : placement.Material is Material.Dirt or Material.Stone) ||
                WorldGrid.InitialMaterial(placement.Cell) == placement.Material ||
                !placedCells.Add(placement.Cell) || !world.RestoreTerrainPlacement(placement.Cell, placement.Material))
                throw new InvalidDataException("Placed terrain must identify a unique, cleared cell with a valid terrain material different from the initial terrain.");
        }

        var damagedCells = new HashSet<Cell>();
        foreach (var damage in state.DamagedTerrain ?? [])
        {
            if (damage is null || !damagedCells.Add(damage.Cell) || !world.RestoreTerrainHp(damage.Cell, damage.Hp))
                throw new InvalidDataException("Saved terrain damage must identify a unique, existing terrain block with positive HP below its maximum.");
        }

        foreach (var barrier in state.Barriers)
        {
            if (barrier is null || !IsValidBarrierCell(barrier.Cell, state.Version) || WorldGrid.IsCore(barrier.Cell) ||
                !float.IsFinite(barrier.Hp) || barrier.Hp <= 0)
                throw new InvalidDataException("A saved defensive block has invalid placement or HP.");
            if (legacyBlocks ? barrier.Content != null : state.Version >= 8
                ? !barrier.Content.HasValue || !Balance.IsTerrainMaterial(barrier.Content.Value)
                : barrier.Content is not (Material.Dirt or Material.Stone))
                throw new InvalidDataException("A saved defensive block has invalid material data.");
            var material = barrier.Content ?? Material.Dirt;
            var maxHp = legacyBlocks ? LegacyBarrierHp : Balance.BlockHp(material);
            if (barrier.Hp > maxHp || !world.AddBarrier(barrier.Cell, material, barrier.Hp))
                throw new InvalidDataException("A saved defensive block has invalid placement or HP.");
        }

        // Use the simulation's own restore validation so capacity, crust geometry,
        // reaction clouds, and future fluid rules cannot diverge from checkpoint checks.
        var liquids = new LiquidSystem(world);
        if (state.Liquids != null) liquids.RestoreState(state.Version is 5 or 6
            ? ConvertLegacyObsidianDurability(state.Liquids) : state.Liquids);
        if (state.Buckets != null) new LiquidBucketSystem(world, liquids).RestoreState(state.Buckets);
        if (state.MegaWeaponFlights != null) GameSession.ValidateMegaWeaponFlights(state.MegaWeaponFlights);

        var cells = new HashSet<Cell>();
        var ids = new HashSet<int>();
        var equippedCounts = Enum.GetValues<UpgradeKind>().ToDictionary(kind => kind, _ => 0L);
        foreach (var saved in state.Turrets)
        {
            if (saved is null || saved.Id <= 0 || saved.Id == int.MaxValue || !ids.Add(saved.Id) ||
                !Enum.IsDefined(saved.Kind) || !IsValidTurretCell(saved.Cell, state.Version) || WorldGrid.IsCore(saved.Cell) ||
                !cells.Add(saved.Cell) || world.GetMaterial(saved.Cell) != Material.Air || liquids.GetTotalUnits(saved.Cell) != 0 ||
                saved.JumpPattern is null || !float.IsFinite(saved.Hp) || saved.Hp <= 0)
                throw new InvalidDataException("A saved piece has invalid identity, placement, HP, or component data.");
            var turret = new Turret { Id = saved.Id, Kind = saved.Kind, Cell = saved.Cell, Hp = saved.Hp };
            if (legacy)
            {
                if (saved.Upgrades is null || saved.UpgradePoints != null || saved.Upgrades.Distinct().Count() != saved.Upgrades.Count)
                    throw new InvalidDataException("A legacy piece needs its original, unique component list.");
                foreach (var kind in saved.Upgrades)
                {
                    if (!Enum.IsDefined(kind)) throw new InvalidDataException("A legacy piece has an unknown component.");
                    equippedCounts[kind]++;
                }
                if (saved.Hp > LegacyMaxHp(saved, economy))
                    throw new InvalidDataException("A legacy piece has more HP than its upgraded maximum.");
                var jumpEquipped = saved.Upgrades.Contains(UpgradeKind.Jump);
                var jumpRank = economy.GetProgress(UpgradeKind.Jump).Rank;
                var range = jumpEquipped ? checked(jumpRank + 1) : 2;
                ValidateJumpPattern(saved.JumpPattern, range);
            }
            else
            {
                if (saved.UpgradePoints == null || saved.Upgrades != null)
                    throw new InvalidDataException("This checkpoint version requires allocated upgrade points.");
                foreach (var (kind, points) in saved.UpgradePoints)
                {
                    if (!Enum.IsDefined(kind) || points <= 0 || points > economy.GetAllocationCap(kind) || IsInnatelyInfinite(saved.Kind, kind))
                        throw new InvalidDataException("A saved piece has invalid upgrade points or exceeds its collection cap.");
                    turret.UpgradePoints.Add(kind, points);
                    equippedCounts[kind] += points;
                }
                if (saved.Hp > economy.GetMaxHp(turret))
                    throw new InvalidDataException("A saved piece has more HP than its upgraded maximum.");
                _ = economy.GetAttack(turret);
                ValidateJumpPattern(saved.JumpPattern, economy.HasJump(turret) ? economy.GetRange(turret, UpgradeKind.Jump) : 2);
            }
        }

        foreach (var (kind, equipped) in equippedCounts)
        {
            var progress = economy.GetProgress(kind);
            if (equipped > progress.PullCount - progress.Available)
                throw new InvalidDataException($"The saved field has more {kind} points than were consumed.");
        }
        if (state.QueuedUpgradeBatch is { } batch)
        {
            if (state.Version < 8 || batch.Targets is null || batch.Costs is null || batch.Targets.Count == 0 ||
                batch.Targets.Count > state.Turrets.Count ||
                batch.Targets.Any(target => target is null) ||
                batch.GraceRemaining is < 0 or > GameSession.UpgradeBatchGraceSeconds ||
                batch.GraceRemaining is { } remaining && !float.IsFinite(remaining) ||
                batch.Targets.Select(target => target.TurretId).Distinct().Count() != batch.Targets.Count ||
                batch.Costs.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value <= 0))
                throw new InvalidDataException("The queued upgrade batch is invalid.");
            var costs = new Dictionary<UpgradeKind, long>();
            foreach (var target in batch.Targets)
            {
                var turret = state.Turrets.SingleOrDefault(saved => saved.Id == target.TurretId);
                var projected = turret == null ? null : new Turret { Kind = turret.Kind };
                if (projected != null)
                    foreach (var (kind, points) in turret!.UpgradePoints!) projected.UpgradePoints[kind] = points;
                if (turret == null || target.Expected is null || target.Additions is null || target.Additions.Count == 0 ||
                    target.Expected.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value < 0) ||
                    target.Additions.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value <= 0 ||
                        economy.GetUpgradeCapacity(projected!, pair.Key) < pair.Value))
                    throw new InvalidDataException("The queued upgrade batch has an invalid target.");
                foreach (var kind in Enum.GetValues<UpgradeKind>())
                    if (target.Expected.GetValueOrDefault(kind) != turret.UpgradePoints!.GetValueOrDefault(kind))
                        throw new InvalidDataException("The queued upgrade batch target changed.");
                foreach (var (kind, count) in target.Additions)
                    costs[kind] = checked(costs.GetValueOrDefault(kind) + count);
            }
            if (costs.Count != batch.Costs.Count ||
                costs.Any(pair => batch.Costs.GetValueOrDefault(pair.Key) != pair.Value))
                throw new InvalidDataException("The queued upgrade batch costs are invalid.");
        }
        ValidateReturningZombies(state, ids);
    }

    private static void ValidateReturningZombies(CampaignState state, HashSet<int> usedIds)
    {
        if (state.Version < 8 && state.ReturningZombies != null)
            throw new InvalidDataException("Returning zombies do not match the checkpoint version.");
        foreach (var zombie in state.ReturningZombies ?? [])
        {
            if (zombie is null || zombie.Id <= 0 || zombie.Id == int.MaxValue || !usedIds.Add(zombie.Id) ||
                zombie.TargetLevel != state.LevelNumber || zombie.TargetWaveIndex != 0 ||
                !Enum.IsDefined(zombie.Kind) || !Enum.IsDefined(zombie.Tier) || !Enum.IsDefined(zombie.Size) ||
                zombie.IsBoss && zombie.Size is not (ZombieSize.Kilo or ZombieSize.Mega) || zombie.RetreatReturns is < 0 or > 3 ||
                zombie.OriginalBudgetCost <= 0 || zombie.IsBoss && zombie.OriginalBudgetCost != (zombie.Size == ZombieSize.Mega ? 15 : Balance.ZombieHp) ||
                zombie.Armor is null || zombie.Buckets is null || zombie.HeldBlocks is null)
                throw new InvalidDataException("A returning zombie has invalid identity, wave, or equipment data.");

            bool giant = zombie.Size is ZombieSize.Kilo or ZombieSize.Mega;
            int blockCapacity = zombie.Size == ZombieSize.Mega ? 4 : zombie.Size == ZombieSize.Kilo ? 2 : 1;
            float offsetLimit = zombie.Size == ZombieSize.Mega ? 1 : .5f;
            float multiplier = zombie.Size switch { ZombieSize.Milli => .5f, ZombieSize.Kilo => 4, ZombieSize.Mega => 9, _ => 1 };
            if (!float.IsFinite(zombie.BodyHp) || zombie.BodyHp <= 0 || zombie.BodyHp > Balance.ZombieHp * multiplier)
                throw new InvalidDataException("A returning zombie has invalid body HP.");

            var slots = new HashSet<ArmorSlot>();
            if (zombie.Armor.Count > 4) throw new InvalidDataException("A returning zombie has too many armor pieces.");
            foreach (var armor in zombie.Armor)
                if (armor is null || !Enum.IsDefined(armor.Slot) || !Enum.IsDefined(armor.Tier) || !slots.Add(armor.Slot) ||
                    !float.IsFinite(armor.Hp) || armor.Hp <= 0 || armor.Hp > Balance.GearHp(armor.Tier) * multiplier ||
                    !float.IsFinite(armor.HeatProtectionRemaining) || armor.HeatProtectionRemaining < 0 ||
                    armor.HeatProtectionRemaining > (armor.Tier == GearTier.Osmium ? 10 : 0))
                    throw new InvalidDataException("A returning zombie has invalid armor durability or heat protection.");

            int bucketCount = zombie.CarriesBucket ? giant ? 2 : 1 : 0;
            if (zombie.Buckets.Count != bucketCount || zombie.CarriedBlocks < 0 ||
                zombie.CarriedBlocks > blockCapacity ||
                !giant && zombie.CarriesBucket && zombie.CarriedBlocks != 0)
                throw new InvalidDataException("A returning zombie has invalid carried block or bucket counts.");
            foreach (var bucket in zombie.Buckets)
                if (bucket is null || bucket.Units < 0 || bucket.Units > (zombie.Size == ZombieSize.Mega ? 15 : LiquidSystem.UnitsPerBlock) ||
                    bucket.Kind.HasValue && !Enum.IsDefined(bucket.Kind.Value) || (bucket.Units == 0) != !bucket.Kind.HasValue)
                    throw new InvalidDataException("A returning zombie has an invalid bucket payload.");

            if (zombie.HeldBlocks.Count > (giant ? blockCapacity : 0) ||
                zombie.Size == ZombieSize.Kilo && zombie.CarriedBlocks + zombie.HeldBlocks.Count > blockCapacity)
                throw new InvalidDataException("A returning zombie has too many held blocks.");
            var hands = new HashSet<int>();
            foreach (var block in zombie.HeldBlocks)
                if (block is null || block.BlockIndex < 0 || block.BlockIndex >= blockCapacity || !hands.Add(block.BlockIndex) ||
                    !Balance.IsTerrainMaterial(block.Material) || !float.IsFinite(block.Hp) || block.Hp <= 0 ||
                    block.Hp > Balance.BlockHp(block.Material) || !float.IsFinite(block.OffsetX) ||
                    !float.IsFinite(block.OffsetY) || !float.IsFinite(block.OffsetZ) ||
                    Math.Abs(block.OffsetX) > offsetLimit || Math.Abs(block.OffsetY) > offsetLimit || Math.Abs(block.OffsetZ) > offsetLimit)
                    throw new InvalidDataException("A returning Kilo has invalid held-block data.");
            for (int firstIndex = 0; firstIndex < zombie.HeldBlocks.Count; firstIndex++)
                for (int secondIndex = firstIndex + 1; secondIndex < zombie.HeldBlocks.Count; secondIndex++)
            {
                var first = zombie.HeldBlocks[firstIndex];
                var second = zombie.HeldBlocks[secondIndex];
                if (Math.Abs(first.OffsetX - second.OffsetX) < 1 && Math.Abs(first.OffsetY - second.OffsetY) < 1 &&
                    Math.Abs(first.OffsetZ - second.OffsetZ) < 1)
                    throw new InvalidDataException("A returning Kilo's independent blocks overlap.");
            }
        }
    }

    private static bool IsValidTurretCell(Cell cell, int version) => version >= 6
        ? cell.X is >= 1 and <= 30 && cell.Y is >= 0 and <= 30 && cell.Z is >= 1 and <= 30
        : version == 1
            ? cell.X is >= 1 and <= 30 && cell.Y is >= 1 and <= 30 && cell.Z is >= 1 and <= 30
            : WorldGrid.IsTurretZone(cell);

    private static bool IsValidBarrierCell(Cell cell, int version) => version >= 7
        ? cell.X is >= 1 and <= 30 && cell.Y is >= 0 and <= 30 && cell.Z is >= 1 and <= 30
        : version >= 6
            ? cell.X is >= 12 and <= 19 && cell.Y is >= 0 and <= 19 && cell.Z is >= 12 and <= 19
            : WorldGrid.IsBuildZone(cell);

    private static bool IsInnatelyInfinite(PieceKind piece, UpgradeKind kind) =>
        (piece == PieceKind.Rook && kind == UpgradeKind.Orthogonal) ||
        (piece == PieceKind.Bishop && kind == UpgradeKind.PlaneDiagonal);

    private static int LegacyMaxHp(SavedTurret saved, Economy economy) => checked(Balance.TurretHp +
        (saved.Upgrades!.Contains(UpgradeKind.HP) ? economy.GetProgress(UpgradeKind.HP).Rank * Balance.HpUpgradePerPoint : 0));

    private static string MigrateLegacy(CampaignState state)
    {
        var economy = new Economy();
        economy.ImportState(state.Economy);
        var migrated = new List<SavedTurret>();
        int removed = 0, refundedCurrency = 0, refundedPoints = 0, adjustedPatterns = 0, rebalanced = 0;
        foreach (var saved in state.Turrets)
        {
            if (WorldGrid.IsBuildZone(saved.Cell))
            {
                removed++;
                refundedCurrency = checked(refundedCurrency + Balance.PieceCost(saved.Kind));
                foreach (var kind in saved.Upgrades!)
                {
                    state.Economy.Upgrades[kind].Available++;
                    refundedPoints++;
                }
                continue;
            }
            var previousMaxHp = LegacyMaxHp(saved, economy);
            var turret = new Turret { Id = saved.Id, Kind = saved.Kind, Cell = saved.Cell, Hp = saved.Hp };
            foreach (var kind in saved.Upgrades!)
            {
                if (IsInnatelyInfinite(saved.Kind, kind))
                {
                    state.Economy.Upgrades[kind].Available++;
                    refundedPoints++;
                }
                else turret.UpgradePoints.Add(kind, 1);
            }
            if (saved.Upgrades!.Count > 0) rebalanced++;
            saved.Hp = Math.Max(1, economy.GetMaxHp(turret) - (previousMaxHp - saved.Hp));
            saved.UpgradePoints = new Dictionary<UpgradeKind, int>(turret.UpgradePoints);
            saved.Upgrades = null;
            var jumpRange = economy.HasJump(turret) ? economy.GetRange(turret, UpgradeKind.Jump) : 2;
            var pattern = saved.JumpPattern.Where(offset => JumpDistance(offset) <= jumpRange).ToList();
            if (pattern.Count == 0) pattern = new Turret().JumpPattern.ToList();
            if (!saved.JumpPattern.SequenceEqual(pattern)) adjustedPatterns++;
            saved.JumpPattern = pattern;
            migrated.Add(saved);
        }
        state.Turrets = migrated;
        state.Economy.Currency = checked(state.Economy.Currency + refundedCurrency);
        state.Version = 2;
        return $"Checkpoint updated to allocated points: {removed} pieces from the block enclosure refunded ({refundedCurrency} currency); " +
            $"{refundedPoints} points returned; {rebalanced} pieces rebalanced with existing HP damage preserved (minimum 1 HP); " +
            $"{adjustedPatterns} jump patterns adjusted.";
    }

    private static string MigrateBlockInventory(CampaignState state, int originalVersion)
    {
        var dirt = state.Excavated.Count(cell => cell.Y >= 12);
        var stone = state.Excavated.Count - dirt;
        state.Inventory = new BlockInventoryState { DirtCount = dirt, StoneCount = stone };
        foreach (var barrier in state.Barriers)
        {
            barrier.Content = Material.Dirt;
            barrier.Hp = Balance.BlockHp(Material.Dirt) - (LegacyBarrierHp - barrier.Hp);
        }
        state.Version = 3;
        return $"Block inventory restored: {dirt} dirt and {stone} stone from past excavation; " +
            $"{state.Barriers.Count} old fortifications converted to dirt with damage preserved. Excavation tools must be purchased. " +
            $"The original version {originalVersion} file is backed up on the next save.";
    }

    private static string MigrateTerrainDamage(CampaignState state, int originalVersion)
    {
        // Version 3 already accounts for collected materials. Its removed terrain must
        // remain removed without being credited to inventory a second time.
        state.DamagedTerrain = [];
        state.Version = 4;
        return "Natural terrain durability is now saved." + (originalVersion == 3
            ? " The original version 3 file is backed up on the next save." : string.Empty);
    }

    private static string MigrateLiquids(CampaignState state, int originalVersion)
    {
        state.LiquidInventory = Enum.GetValues<LiquidKind>().ToDictionary(kind => kind, _ => 0);
        state.Liquids = new LiquidState();
        state.Version = 5;
        return "Liquid inventory, reactions, and vapor clouds are now saved." + (originalVersion == 4
            ? " The original version 4 file is backed up on the next save." : string.Empty);
    }

    private static string MigrateTerrainPlacements(CampaignState state, int originalVersion)
    {
        state.TerrainPlacements = [];
        state.Version = 6;
        return "Fallen natural terrain is now saved." + (originalVersion == 5
            ? " The original version 5 file is backed up on the next save." : string.Empty);
    }

    private static string MigrateUpgradeTemplate(CampaignState state, int originalVersion)
    {
        if (originalVersion is 5 or 6)
            state.Liquids = ConvertLegacyObsidianDurability(state.Liquids!);
        state.UpgradeTemplate = SavedUpgradeTemplate.FromTemplate(new UpgradeTemplate());
        state.Version = 7;
        return "Upgrade templates are now saved; this older checkpoint starts with an empty template." +
            (originalVersion is 5 or 6 ? " Obsidian durability was rescaled with its remaining durability fraction preserved." : string.Empty) + (originalVersion == 6
            ? " The original version 6 file is backed up on the next save." : string.Empty);
    }

    private static LiquidState ConvertLegacyObsidianDurability(LiquidState source)
    {
        // Validate the original durability before rescaling it. Validation uses a
        // copy so the subsequent migration cannot scale the same saved HP twice.
        if (source.Obsidian is null) throw new InvalidDataException("Liquid snapshot is missing its obsidian collection.");
        var obsidian = new List<ObsidianCell>(source.Obsidian.Count);
        foreach (var crust in source.Obsidian)
        {
            if (crust is null || crust.OppositeUnits != 0 || crust.OppositeHp != 0 ||
                crust.Units is < 1 or > LiquidSystem.UnitsPerBlock ||
                !float.IsFinite(crust.Hp) || crust.Hp <= 0 || crust.Hp > crust.Units * LegacyObsidianHpPerUnit)
                throw new InvalidDataException("Invalid legacy obsidian durability.");
            // Keep a positive representable value for extremely small surviving
            // durability rather than removing a block through floating-point underflow.
            var hp = Math.Max(float.Epsilon, (float)((double)crust.Hp * LiquidSystem.ObsidianHpPerUnit / LegacyObsidianHpPerUnit));
            obsidian.Add(crust with { Hp = hp });
        }
        return new LiquidState
        {
            Cells = source.Cells,
            Obsidian = obsidian,
            Hazards = source.Hazards,
            Accumulator = source.Accumulator,
            FlowTick = source.FlowTick,
            EscapedWaterUnits = source.EscapedWaterUnits,
            EscapedMercuryUnits = source.EscapedMercuryUnits,
            EscapedLavaUnits = source.EscapedLavaUnits,
            SuppliedWaterUnits = source.SuppliedWaterUnits
        };
    }

    private static string MigrateMaterialInventory(CampaignState state, int originalVersion)
    {
        state.Inventory!.AdditionalMaterials = Enum.GetValues<Material>()
            .Where(material => Balance.IsTerrainMaterial(material) && material is not (Material.Dirt or Material.Stone))
            .ToDictionary(material => material, _ => 0);
        state.PendingBlockDrops = [];
        state.Version = 8;
        return "Gear-material inventory is now saved; new material counts start at zero." + (originalVersion == 7
            ? " The original version 7 file is backed up on the next save." : string.Empty);
    }

    private static void ValidateUpgradeTemplate(SavedUpgradeTemplate template)
    {
        // A reusable request can exceed current inventory, collection caps, or the
        // aimed piece's range. Only its data shape is validated here; application
        // decides which points and pattern the particular piece can actually use.
        if (template.Points is null || template.JumpPattern is null ||
            template.Points.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value < 0))
            throw new InvalidDataException("Saved template counts must be nonnegative and use known upgrade types.");
        if (template.JumpPattern.Count == 0 || template.JumpPattern.Distinct().Count() != template.JumpPattern.Count)
            throw new InvalidDataException("A saved template jump pattern must contain unique destinations.");
        if (template.JumpPattern.Any(offset => JumpDistance(offset) is < 2 or > int.MaxValue))
            throw new InvalidDataException("A saved template jump destination is too close or has unsupported coordinates.");
    }

    private static long JumpDistance(Cell offset) => Math.Max(Math.Abs((long)offset.X), Math.Max(Math.Abs((long)offset.Y), Math.Abs((long)offset.Z)));

    private static void PreserveLegacyBackup(string path)
    {
        using var existing = File.OpenRead(path);
        using var document = JsonDocument.Parse(existing);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;
        var version = document.RootElement.EnumerateObject().FirstOrDefault(property => property.Name.Equals("Version", StringComparison.OrdinalIgnoreCase));
        if (version.Value.ValueKind != JsonValueKind.Number || !version.Value.TryGetInt32(out var number) || number is not (1 or 2 or 3 or 4 or 5 or 6 or 7)) return;
        var backup = path + $".v{number}.bak";
        if (File.Exists(backup)) backup = path + $".v{number}.{Guid.NewGuid():N}.bak";
        File.Copy(path, backup);
    }

    private static void ValidateJumpPattern(List<Cell> pattern, int range)
    {
        if (pattern.Count < 1 || pattern.Distinct().Count() != pattern.Count)
            throw new InvalidDataException("A saved jump pattern is empty or contains duplicate destinations.");
        foreach (var offset in pattern)
        {
            long distance = JumpDistance(offset);
            if (distance < 2 || distance > range)
                throw new InvalidDataException("A saved jump destination is outside the piece's jump range.");
        }
    }

    private sealed class CellConverter : JsonConverter<Cell>
    {
        public override Cell Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var element = document.RootElement;
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("X", out var x) || !x.TryGetInt32(out int ix) ||
                !element.TryGetProperty("Y", out var y) || !y.TryGetInt32(out int iy) ||
                !element.TryGetProperty("Z", out var z) || !z.TryGetInt32(out int iz))
                throw new JsonException("A cell needs integer X, Y, and Z coordinates.");
            return new Cell(ix, iy, iz);
        }

        public override void Write(Utf8JsonWriter writer, Cell value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("X", value.X);
            writer.WriteNumber("Y", value.Y);
            writer.WriteNumber("Z", value.Z);
            writer.WriteEndObject();
        }
    }
}
