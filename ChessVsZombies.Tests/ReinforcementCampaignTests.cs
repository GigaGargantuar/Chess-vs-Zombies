using System.Numerics;
using System.Reflection;
using System.Text.Json;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ReinforcementCampaignTests
{
    [Fact]
    public void DepartingKiloTransfersItsRemainingLifeAndOriginalCostWithoutDeathDropsOrRewards()
    {
        var (game, _) = Create(twoWaves: true);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(10, 16, 10)));
        Assert.True(game.StartLevel());
        var kilo = AddEquippedKilo(game);
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var old = game.Waves[0];
        old.DamageTaken = old.TotalHp - kilo.Hp;
        var next = game.Waves[1];
        float remaining = kilo.Hp, oldTotal = old.TotalHp, damage = old.DamageTaken, nextHp = next.TotalHp;
        int currency = game.Economy.Currency, nextCount = next.TotalCount;
        long nextBudget = next.HpBudget, bossBudget = next.BossBudget;
        var blocks = Terrain(game);
        var inventory = JsonSerializer.Serialize(game.Inventory.ExportState());

        Invoke(game, "DeferZombieToNextWave", kilo);

        Assert.DoesNotContain(kilo, game.Zombies);
        Assert.True(kilo.Hp > 0);
        Assert.Equal(1, game.PendingReinforcementCount);
        Assert.Equal(oldTotal - remaining, old.TotalHp);
        Assert.Equal(damage, old.DamageTaken);
        Assert.Equal(nextCount + 1, next.TotalCount);
        Assert.Equal(nextHp + remaining, next.TotalHp);
        Assert.Equal(nextBudget + 10, next.HpBudget);
        Assert.Equal(bossBudget + 10, next.BossBudget);
        Assert.Equal(0, next.BudgetSpent);
        Assert.Equal(currency, game.Economy.Currency);
        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.Equal(blocks, Terrain(game));
        Assert.Equal(inventory, JsonSerializer.Serialize(game.Inventory.ExportState()));
        Assert.Empty(game.KiloCovers);
        Assert.Empty(game.Buckets.Flights);
        Assert.Empty(game.Buckets.PendingSplashes);
        Assert.Empty(game.Liquids.Cells);
        Assert.Empty(game.Feedback.Hits);
    }

    [Fact]
    public void NextWaveRestoresBothIndependentHandsBucketsAndDamagedArmorExactly()
    {
        var (game, _) = Create(twoWaves: true);
        Assert.True(game.StartLevel());
        var kilo = AddEquippedKilo(game);
        var expected = Snapshot(game, kilo);
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        Invoke(game, "DeferZombieToNextWave", kilo);

        Spawn(game, game.Waves[1]);

        var returned = Assert.Single(game.Zombies);
        Assert.Equal(expected, Snapshot(game, returned));
        Assert.Equal(1, returned.WaveIndex);
        Assert.Equal(0, returned.RetreatReturns);
        Assert.Equal(10, returned.OriginalBudgetCost);
        Assert.Equal(1, game.Waves[1].Spawned);
        Assert.Equal(10, game.Waves[1].BudgetSpent);
        Assert.Equal(0, game.PendingReinforcementCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReinforcementsDoNotSkipOrDuplicateNativeSpawnsBeforeDuringOrAfterTheirRoster(int nativesBefore)
    {
        var (game, _) = Create(twoWaves: true);
        Assert.True(game.StartLevel());
        var next = game.Waves[1];
        for (int i = 0; i < nativesBefore; i++) Spawn(game, next);
        var survivor = new Zombie { Id = 1000, Kind = ZombieKind.Basic, WaveIndex = 0, Position = new Cell(5, 16, 5).Center };
        Set(survivor, nameof(Zombie.RetreatReturns), 3);
        Set(survivor, nameof(Zombie.OriginalBudgetCost), 10);
        game.Zombies.Add(survivor);
        Invoke(game, "DeferZombieToNextWave", survivor);

        while (next.Spawned < next.TotalCount) Spawn(game, next);

        Assert.Equal(4, game.Zombies.Count);
        Assert.Equal(4, game.Zombies.Select(zombie => zombie.Id).Distinct().Count());
        Assert.Single(game.Zombies, zombie => zombie.Id == survivor.Id);
        var native = game.Zombies.Where(zombie => zombie.Id != survivor.Id).ToArray();
        Assert.Equal(2, native.Count(zombie => zombie.Kind == ZombieKind.Basic));
        Assert.Single(native, zombie => zombie.Kind == ZombieKind.Armed && zombie.Tier == GearTier.Wood);
        Assert.All(game.Zombies, zombie => Assert.Equal(1, zombie.WaveIndex));
        Assert.Equal(4, next.TotalCount);
        Assert.Equal(4, next.Spawned);
        Assert.Equal(40, next.BudgetSpent);
        Assert.Equal(40, next.HpBudget);
    }

    [Fact]
    public void LastWaveSurvivorCrossesSaveLoadAndReturnsInNextLevelWithItsEquipment()
    {
        var (game, catalog) = Create(twoWaves: false);
        Assert.True(game.StartLevel());
        var kilo = AddEquippedKilo(game);
        var expected = Snapshot(game, kilo);
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        game.Waves[0].DamageTaken = game.Waves[0].TotalHp - kilo.Hp;
        Invoke(game, "DeferZombieToNextWave", kilo);
        game.Update(.001f);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(2, game.LevelNumber);
        Assert.Equal(1, game.PendingReinforcementCount);
        Assert.Empty(game.ExportState().PendingBlockDrops!);
        string path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-reinforcements-{Guid.NewGuid():N}.json");
        try
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error, catalog), error);
            Assert.NotNull(restored);
            Assert.Empty(restored.Zombies);
            Assert.Equal(1, restored.PendingReinforcementCount);
            var saved = Assert.Single(restored.ExportState().ReturningZombies!);
            Assert.Equal(2, saved.TargetLevel);
            Assert.Equal(0, saved.TargetWaveIndex);
            Assert.Equal(3, saved.RetreatReturns);
            Assert.True(restored.StartLevel());
            Assert.Equal(4, restored.Waves[0].TotalCount);
            Assert.Equal(40, restored.Waves[0].HpBudget);

            Spawn(restored, restored.Waves[0]);

            var returned = Assert.Single(restored.Zombies);
            Assert.Equal(expected, Snapshot(restored, returned));
            Assert.Equal(0, returned.WaveIndex);
            Assert.Equal(0, returned.RetreatReturns);
            Assert.Equal(10, restored.Waves[0].BudgetSpent);
            Assert.Equal(0, restored.PendingReinforcementCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExportedReinforcementEquipmentCannotMutateTheLiveCampaignQueue()
    {
        var (game, _) = Create(twoWaves: false);
        Assert.True(game.StartLevel());
        var kilo = AddEquippedKilo(game);
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        Invoke(game, "DeferZombieToNextWave", kilo);
        game.Update(.001f);
        string expected = JsonSerializer.Serialize(game.ExportState().ReturningZombies);

        var detached = Assert.Single(game.ExportState().ReturningZombies!);
        detached.BodyHp = 1;
        detached.Armor[0].Hp = 1;
        detached.Armor[1].HeatProtectionRemaining = 0;
        detached.Buckets[0].Units = 0;
        detached.Buckets[0].Kind = null;
        detached.HeldBlocks[0].Hp = 1;
        detached.HeldBlocks[1].Material = Material.Wood;

        Assert.Equal(expected, JsonSerializer.Serialize(game.ExportState().ReturningZombies));
    }

    private static (GameSession Game, LevelCatalog Catalog) Create(bool twoWaves)
    {
        var catalog = new LevelCatalog();
        var first = new WaveDefinition { Groups = [new() { Kind = ZombieKind.Armored, Tier = GearTier.Osmium, Size = ZombieSize.Kilo, IsBoss = true }] };
        static WaveDefinition Next() => new() { Groups = [new() { Kind = ZombieKind.Basic, Count = 2 }, new() { Kind = ZombieKind.Armed, Tier = GearTier.Wood }] };
        catalog.Set(new LevelDefinition { Number = 1, Waves = twoWaves ? [first, Next()] : [first] });
        catalog.Set(new LevelDefinition { Number = 2, Waves = [Next()] });
        return (new GameSession(levels: catalog), catalog);
    }

    private static Zombie AddEquippedKilo(GameSession game)
    {
        var kilo = new Zombie
        {
            Id = 500, WaveIndex = 0, Kind = ZombieKind.Armored, Tier = GearTier.Osmium,
            Size = ZombieSize.Kilo, IsBoss = true, Position = new Vector3(6, 17, 6), Attack = 1
        };
        game.Zombies.Add(kilo);
        kilo.FaceToward(Vector3.UnitX);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.Equal(2, game.KiloCovers.Count);
        Invoke(game, "DamageKiloCover", kilo.Id, 0, 37.25f);
        Invoke(game, "DamageKiloCover", kilo.Id, 1, 1234.5f);
        float[] hp = [277.125f, 130.375f, 401.25f];
        float[] heat = [3.125f, .0625f, 9.5f];
        var armor = kilo.ArmorPieces.ToArray();
        for (int i = 0; i < armor.Length; i++)
        {
            Set(armor[i], nameof(ArmorPiece.Hp), hp[i]);
            Set(armor[i], nameof(ArmorPiece.HeatProtectionRemaining), heat[i]);
        }
        Invoke(kilo, "RestoreBodyAndArmor", 27.125f, armor);
        kilo.CarriesBucket = true;
        Set(kilo.BucketLoads[0], nameof(ZombieBucketLoad.Units), 4);
        Set(kilo.BucketLoads[0], nameof(ZombieBucketLoad.Kind), LiquidKind.Water);
        Set(kilo.BucketLoads[1], nameof(ZombieBucketLoad.Units), 8);
        Set(kilo.BucketLoads[1], nameof(ZombieBucketLoad.Kind), LiquidKind.Mercury);
        Set(kilo, nameof(Zombie.RetreatReturns), 3);
        Set(kilo, nameof(Zombie.OriginalBudgetCost), 10);
        return kilo;
    }

    private static string Snapshot(GameSession game, Zombie zombie) => JsonSerializer.Serialize(new
    {
        zombie.Id, zombie.Kind, zombie.Tier, zombie.Size, zombie.IsBoss, zombie.BodyHp, zombie.Hp,
        zombie.CarriesBucket, zombie.CarriedBlocks, zombie.CarriedBlockMaterial,
        Armor = zombie.ArmorPieces.Select(piece => new { piece.Slot, piece.Tier, piece.Hp, piece.HeatProtectionRemaining }),
        Buckets = zombie.BucketLoads.Select(load => new { load.Units, load.Kind }),
        Covers = game.KiloCovers.Where(cover => cover.CarrierId == zombie.Id).OrderBy(cover => cover.BlockIndex)
            .Select(cover => new { cover.BlockIndex, cover.Material, cover.Hp, cover.WasBarrier,
                OffsetX = ((cover.Min.X + cover.Max.X) * .5f) - zombie.Position.X,
                OffsetY = ((cover.Min.Y + cover.Max.Y) * .5f) - zombie.Position.Y,
                OffsetZ = ((cover.Min.Z + cover.Max.Z) * .5f) - zombie.Position.Z })
    });

    private static Cell[] Terrain(GameSession game)
    {
        var cells = new List<Cell>();
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 0; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (Balance.IsTerrainMaterial(game.World.GetMaterial(cell))) cells.Add(cell);
                }
        return cells.ToArray();
    }

    private static void Spawn(GameSession game, WaveState wave) => Assert.True((bool)Invoke(game, "Spawn", wave)!);
    private static object? Invoke(object target, string method, params object?[] arguments) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);
}
