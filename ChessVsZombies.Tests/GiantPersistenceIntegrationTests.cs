using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GiantPersistenceIntegrationTests
{
    [Fact]
    public void AReturningMegaRoundTripsFourRealBlocksArmorDamageAndBothFullBuckets()
    {
        var catalog = Catalog();
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var mega = new Zombie
        {
            Id = 500, WaveIndex = 0, Size = ZombieSize.Mega, Kind = ZombieKind.ArmedArmored,
            Tier = GearTier.Osmium, IsBoss = true, Attack = 315
        };
        mega.Position = mega.StandAt(new Cell(5, 16, 8));
        game.Zombies.Add(mega);
        Assert.True((bool)Invoke(game.World, "AddBarrier", new Cell(4, 16, 9), Material.Copper, (float?)77)!);
        for (int action = 0; action < 4; action++) Invoke(game, "UpdateMegas", 1f);
        Assert.Equal(4, game.MegaWeapons.Count);
        Assert.Equal(2, game.MegaWeapons.Count(block => block.HandIndex == 0));
        Assert.Equal(2, game.MegaWeapons.Count(block => block.HandIndex == 1));
        for (int block = 0; block < 4; block++) Invoke(game, "DamageKiloCover", mega.Id, block, 3.125f + block);
        game.DamageZombie(mega, 17.25f, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitY);
        game.DamageZombie(mega, 23.5f, DamageKind.Pierce, ShotKind.Pellet, -Vector3.UnitY);
        game.DamageZombie(mega, 31.75f, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitX);
        game.DamageZombie(mega, 12.5f, DamageKind.Thermal);
        game.DamageZombie(mega, 7.25f, DamageKind.Chemical);
        Set(mega.BucketLoads[0], nameof(ZombieBucketLoad.Units), 15);
        Set(mega.BucketLoads[0], nameof(ZombieBucketLoad.Kind), LiquidKind.Water);
        Set(mega.BucketLoads[1], nameof(ZombieBucketLoad.Units), 15);
        Set(mega.BucketLoads[1], nameof(ZombieBucketLoad.Kind), LiquidKind.Mercury);
        Set(mega, nameof(Zombie.RetreatReturns), 3);
        Set(mega, nameof(Zombie.OriginalBudgetCost), 15);
        var expected = Snapshot(game, mega);
        float remainingHp = mega.Hp;
        game.Waves[0].DamageTaken = game.Waves[0].TotalHp - remainingHp;

        Invoke(game, "DeferZombieToNextWave", mega);
        game.Update(.001f);

        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(2, game.LevelNumber);
        Assert.Empty(game.MegaWeapons);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Empty(game.Buckets.Flights);
        Assert.Empty(game.Liquids.Cells);
        WithSavePath(path =>
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error, catalog), error);
            var queued = Assert.Single(restored!.ExportState().ReturningZombies!);
            Assert.Equal(8, restored.ExportState().Version);
            Assert.Equal(ZombieSize.Mega, queued.Size);
            Assert.Equal(15, queued.OriginalBudgetCost);
            Assert.Equal(4, queued.HeldBlocks.Count);
            Assert.Equal(4, queued.Armor.Count);
            Assert.Equal(2, queued.Buckets.Count);
            Assert.All(queued.Buckets, bucket => Assert.Equal(15, bucket.Units));
            Assert.Equal(82.75f, queued.BodyHp);
            Assert.Equal(1890 - 17.25f, queued.Armor.Single(armor => armor.Slot == ArmorSlot.Boots).Hp);
            Assert.Equal(8.75f, queued.Armor.Single(armor => armor.Slot == ArmorSlot.Shield).HeatProtectionRemaining);
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error, catalog), error);
            Assert.True(reloaded!.StartLevel());
            Assert.Equal(25, reloaded.Waves[0].HpBudget);
            Assert.Equal(remainingHp + 10, reloaded.Waves[0].TotalHp);

            Assert.True((bool)Invoke(reloaded, "Spawn", reloaded.Waves[0])!);

            var returned = Assert.Single(reloaded.Zombies);
            Assert.Equal(expected, Snapshot(reloaded, returned));
            Assert.Equal(315, returned.Attack);
            Assert.Equal(0, returned.RetreatReturns);
            Assert.Equal(15, returned.OriginalBudgetCost);
            Assert.Equal(15, reloaded.Waves[0].BudgetSpent);
            Assert.Equal(0, reloaded.PendingReinforcementCount);
            AssertBoundaryBody(reloaded, returned);
        });
    }

    [Fact]
    public void ALegacyVersionEightReturningKiloRetainsItsThreeArmorPiecesWithoutInventingBoots()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var saved = new SavedReturningZombie
            {
                TargetLevel = 1, TargetWaveIndex = 0, Id = 500, Kind = ZombieKind.Armored, Tier = GearTier.Osmium,
                Size = ZombieSize.Kilo, IsBoss = true, BodyHp = 37, RetreatReturns = 3, OriginalBudgetCost = 10,
                CarriesBucket = false, CarriedBlocks = 1,
                Armor = new[] { ArmorSlot.Shield, ArmorSlot.Chestplate, ArmorSlot.Helmet }.Select(slot => new SavedZombieArmor
                { Slot = slot, Tier = GearTier.Osmium, Hp = 500, HeatProtectionRemaining = 6.25f }).ToList()
            };
            document["ReturningZombies"] = new JsonArray(JsonSerializer.SerializeToNode(saved,
                new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } }));
            File.WriteAllText(path, document.ToJsonString());
            string original = File.ReadAllText(path);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);

            Assert.Equal(original, File.ReadAllText(path));
            Assert.Equal(3, Assert.Single(restored!.ExportState().ReturningZombies!).Armor.Count);
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var reloaded, out error), error);
            Assert.True(reloaded!.StartLevel());
            Assert.True((bool)Invoke(reloaded, "Spawn", reloaded.Waves[0])!);
            var kilo = Assert.Single(reloaded.Zombies);
            Assert.Equal(3, kilo.ArmorPieces.Count);
            Assert.DoesNotContain(kilo.ArmorPieces, piece => piece.Slot == ArmorSlot.Boots);
            Assert.Equal(1537, kilo.Hp);
            Assert.Equal(18.75f, kilo.ArmorPieces.Sum(piece => piece.HeatProtectionRemaining));
            Assert.Equal(2, kilo.BucketCount);
            Assert.Equal(0, kilo.HeldBucketUnits);
            Assert.Equal(1, kilo.CarriedBlocks);

            reloaded.DamageZombie(kilo, 3, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitY);

            Assert.Equal(34, kilo.BodyHp);
            Assert.All(kilo.ArmorPieces, piece => Assert.Equal(500, piece.Hp));
        });
    }

    [Fact]
    public void ARegularMegaSpawnUsesItsFullThreeCubeBoundaryFootprintAndScaledWeaponAttack()
    {
        var game = new GameSession(12345, Catalog());
        Assert.True(game.StartLevel());

        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);

        var mega = Assert.Single(game.Zombies);
        Assert.Equal(ZombieSize.Mega, mega.Size);
        Assert.Equal(315, mega.Attack);
        Assert.Equal(90, mega.BodyHp);
        Assert.Equal(4, mega.ArmorPieces.Count);
        Assert.All(mega.ArmorPieces, piece => Assert.Equal(1890, piece.Hp));
        Assert.Equal(15, game.Waves[0].BudgetSpent);
        Assert.Equal(2, mega.BucketCount);
        Assert.Equal(15, mega.BucketCapacity);
        AssertBoundaryBody(game, mega);
    }

    private static LevelCatalog Catalog()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Number = 1, Waves = [new() { Groups =
            [new() { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Osmium, Size = ZombieSize.Mega, IsBoss = true }] }] });
        catalog.Set(new LevelDefinition { Number = 2, Waves = [new() { Groups = [new() { Kind = ZombieKind.Basic }] }] });
        return catalog;
    }

    private static string Snapshot(GameSession game, Zombie zombie) => JsonSerializer.Serialize(new
    {
        zombie.Id, zombie.Kind, zombie.Size, zombie.Tier, zombie.IsBoss, zombie.BodyHp, zombie.Hp, zombie.CarriedBlocks,
        Armor = zombie.ArmorPieces.Select(piece => new { piece.Slot, piece.Tier, piece.Hp, piece.HeatProtectionRemaining }),
        Buckets = zombie.BucketLoads.Select(load => new { load.Units, load.Kind }),
        Blocks = game.MegaWeapons.Where(block => block.CarrierId == zombie.Id).OrderBy(block => block.BlockIndex).Select(block =>
            new { block.BlockIndex, block.Material, block.Hp, block.WasBarrier,
                OffsetX = (block.Min.X + block.Max.X) * .5f - zombie.Position.X,
                OffsetY = (block.Min.Y + block.Max.Y) * .5f - zombie.Position.Y,
                OffsetZ = (block.Min.Z + block.Max.Z) * .5f - zombie.Position.Z })
    });

    private static void AssertBoundaryBody(GameSession game, Zombie zombie)
    {
        var min = zombie.Position - zombie.BodyHalfExtents;
        var max = zombie.Position + zombie.BodyHalfExtents;
        Assert.Equal(new Vector3(3), max - min);
        Assert.True(min.X >= 0 && min.Y >= 0 && min.Z >= 0 && max.X <= 32 && max.Y <= 32 && max.Z <= 32);
        Assert.True(min.X == 0 || min.Z == 0 || max.X == 32 || max.Z == 32 || max.Y == 32);
        Assert.True((bool)Invoke(game, "HasZombieSupport", zombie.Cell, zombie)!);
        Assert.True((bool)Invoke(game, "IsBodyClear", zombie.Position, zombie)!);
    }

    private static object? Invoke(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Set(object target, string name, object? value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static void WithSavePath(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-giant-{Guid.NewGuid():N}.json");
        try { action(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
