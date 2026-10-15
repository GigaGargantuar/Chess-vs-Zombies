using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ReturningZombieValidationTests
{
    [Theory]
    [InlineData("null-zombie")]
    [InlineData("missing-id")]
    [InlineData("zero-id")]
    [InlineData("maximum-id")]
    [InlineData("duplicate-id")]
    [InlineData("turret-id")]
    [InlineData("wrong-level")]
    [InlineData("wrong-wave")]
    [InlineData("invalid-kind")]
    [InlineData("invalid-tier")]
    [InlineData("invalid-size")]
    [InlineData("integer-enum")]
    [InlineData("standard-boss")]
    [InlineData("dead-body")]
    [InlineData("oversized-body")]
    [InlineData("infinite-body")]
    [InlineData("negative-returns")]
    [InlineData("too-many-returns")]
    [InlineData("zero-budget")]
    [InlineData("wrong-boss-budget")]
    [InlineData("null-armor")]
    [InlineData("null-armor-piece")]
    [InlineData("duplicate-armor-slot")]
    [InlineData("invalid-armor-slot")]
    [InlineData("invalid-armor-tier")]
    [InlineData("dead-armor")]
    [InlineData("oversized-armor")]
    [InlineData("infinite-armor")]
    [InlineData("negative-heat")]
    [InlineData("oversized-heat")]
    [InlineData("infinite-heat")]
    [InlineData("non-osmium-heat")]
    [InlineData("null-buckets")]
    [InlineData("null-bucket")]
    [InlineData("missing-bucket")]
    [InlineData("extra-bucket")]
    [InlineData("negative-liquid")]
    [InlineData("overflow-liquid")]
    [InlineData("invalid-liquid")]
    [InlineData("missing-liquid-kind")]
    [InlineData("liquid-without-kind")]
    [InlineData("kind-without-liquid")]
    [InlineData("buckets-without-tool")]
    [InlineData("bucket-and-backpack")]
    [InlineData("negative-blocks")]
    [InlineData("too-many-blocks")]
    [InlineData("null-held-blocks")]
    [InlineData("null-held-block")]
    [InlineData("too-many-held-blocks")]
    [InlineData("invalid-hand")]
    [InlineData("duplicate-hand")]
    [InlineData("invalid-material")]
    [InlineData("obsidian-cover")]
    [InlineData("dead-cover")]
    [InlineData("oversized-cover")]
    [InlineData("infinite-cover-hp")]
    [InlineData("outside-cover")]
    [InlineData("infinite-cover-offset")]
    [InlineData("overlapping-covers")]
    [InlineData("combined-block-overflow")]
    [InlineData("standard-held-block")]
    [InlineData("legacy-field")]
    public void MalformedReinforcementDataIsRejectedAndOriginalFileIsPreserved(string fault)
    {
        WithSavePath(path =>
        {
            var game = new GameSession();
            Assert.True(game.Deploy(PieceKind.Pawn, new Cell(4, 16, 4)));
            SaveStore.Save(game, path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var zombie = JsonSerializer.SerializeToNode(ValidKilo(), JsonOptions)!.AsObject();
            document["ReturningZombies"] = new JsonArray(zombie);
            var armor = zombie["Armor"]![0]!.AsObject();
            var bucket = zombie["Buckets"]![0]!.AsObject();
            var cover = zombie["HeldBlocks"]![0]!.AsObject();
            switch (fault)
            {
                case "null-zombie": document["ReturningZombies"]![0] = null; break;
                case "missing-id": zombie.Remove("Id"); break;
                case "zero-id": zombie["Id"] = 0; break;
                case "maximum-id": zombie["Id"] = int.MaxValue; break;
                case "duplicate-id": document["ReturningZombies"]!.AsArray().Add(zombie.DeepClone()); break;
                case "turret-id": zombie["Id"] = Assert.Single(game.Turrets).Id; break;
                case "wrong-level": zombie["TargetLevel"] = 2; break;
                case "wrong-wave": zombie["TargetWaveIndex"] = 1; break;
                case "invalid-kind": zombie["Kind"] = "Mage"; break;
                case "invalid-tier": zombie["Tier"] = "Uranium"; break;
                case "invalid-size": zombie["Size"] = "Unknown"; break;
                case "integer-enum": zombie["Size"] = 2; break;
                case "standard-boss": zombie["Size"] = "Standard"; break;
                case "dead-body": zombie["BodyHp"] = 0; break;
                case "oversized-body": zombie["BodyHp"] = 41; break;
                case "infinite-body": zombie["BodyHp"] = JsonNode.Parse("1e40"); break;
                case "negative-returns": zombie["RetreatReturns"] = -1; break;
                case "too-many-returns": zombie["RetreatReturns"] = 4; break;
                case "zero-budget": zombie["OriginalBudgetCost"] = 0; break;
                case "wrong-boss-budget": zombie["OriginalBudgetCost"] = 40; break;
                case "null-armor": zombie["Armor"] = null; break;
                case "null-armor-piece": zombie["Armor"]![0] = null; break;
                case "duplicate-armor-slot": zombie["Armor"]![1]!["Slot"] = armor["Slot"]!.DeepClone(); break;
                case "invalid-armor-slot": armor["Slot"] = "Gorget"; break;
                case "invalid-armor-tier": armor["Tier"] = "Uranium"; break;
                case "dead-armor": armor["Hp"] = 0; break;
                case "oversized-armor": armor["Hp"] = 841; break;
                case "infinite-armor": armor["Hp"] = JsonNode.Parse("1e40"); break;
                case "negative-heat": armor["HeatProtectionRemaining"] = -.1f; break;
                case "oversized-heat": armor["HeatProtectionRemaining"] = 10.1f; break;
                case "infinite-heat": armor["HeatProtectionRemaining"] = JsonNode.Parse("1e40"); break;
                case "non-osmium-heat": armor["Tier"] = "Diamond"; armor["Hp"] = 100; break;
                case "null-buckets": zombie["Buckets"] = null; break;
                case "null-bucket": zombie["Buckets"]![0] = null; break;
                case "missing-bucket": zombie["Buckets"]!.AsArray().RemoveAt(1); break;
                case "extra-bucket": zombie["Buckets"]!.AsArray().Add(bucket.DeepClone()); break;
                case "negative-liquid": bucket["Units"] = -1; break;
                case "overflow-liquid": bucket["Units"] = 11; break;
                case "invalid-liquid": bucket["Kind"] = "Oil"; break;
                case "missing-liquid-kind": bucket.Remove("Kind"); break;
                case "liquid-without-kind": bucket["Kind"] = null; break;
                case "kind-without-liquid": bucket["Units"] = 0; break;
                case "buckets-without-tool": zombie["CarriesBucket"] = false; break;
                case "bucket-and-backpack": zombie["CarriedBlocks"] = 1; break;
                case "negative-blocks": zombie["CarriedBlocks"] = -1; break;
                case "too-many-blocks": zombie["CarriedBlocks"] = 3; break;
                case "null-held-blocks": zombie["HeldBlocks"] = null; break;
                case "null-held-block": zombie["HeldBlocks"]![0] = null; break;
                case "too-many-held-blocks": zombie["HeldBlocks"]!.AsArray().Add(cover.DeepClone()); break;
                case "invalid-hand": cover["BlockIndex"] = 2; break;
                case "duplicate-hand": zombie["HeldBlocks"]![1]!["BlockIndex"] = cover["BlockIndex"]!.DeepClone(); break;
                case "invalid-material": cover["Material"] = "Air"; break;
                case "obsidian-cover": cover["Material"] = "Obsidian"; break;
                case "dead-cover": cover["Hp"] = 0; break;
                case "oversized-cover": cover["Hp"] = 101; break;
                case "infinite-cover-hp": cover["Hp"] = JsonNode.Parse("1e40"); break;
                case "outside-cover": cover["OffsetX"] = -.501f; break;
                case "infinite-cover-offset": cover["OffsetY"] = JsonNode.Parse("1e40"); break;
                case "overlapping-covers": cover["OffsetX"] = 0; break;
                case "combined-block-overflow":
                    zombie["CarriesBucket"] = false; zombie["Buckets"] = new JsonArray(); zombie["CarriedBlocks"] = 1; break;
                case "standard-held-block":
                    zombie["Size"] = "Standard"; zombie["IsBoss"] = false; zombie["BodyHp"] = 10;
                    zombie["Armor"] = new JsonArray(); zombie["Buckets"]!.AsArray().RemoveAt(1); break;
                case "legacy-field":
                    document["Version"] = 7; document["Inventory"]!.AsObject().Remove("AdditionalMaterials");
                    document.AsObject().Remove("PendingBlockDrops"); break;
                default: throw new ArgumentOutOfRangeException(nameof(fault));
            }
            string invalid = document.ToJsonString();
            File.WriteAllText(path, invalid);

            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalid, File.ReadAllText(path));
        });
    }

    [Fact]
    public void OlderVersionEightCheckpointHasAnEmptyReinforcementQueue()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document.Remove("ReturningZombies");
            string old = document.ToJsonString();
            File.WriteAllText(path, old);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Empty(restored.ExportState().ReturningZombies ?? []);
            Assert.Equal(old, File.ReadAllText(path));
        });
    }

    [Theory]
    [InlineData(ZombieSize.Milli, 5, 105)]
    [InlineData(ZombieSize.Standard, 10, 210)]
    [InlineData(ZombieSize.Kilo, 40, 840)]
    public void SizeScaledDurabilityLimitsAcceptValidPayloadsAndRejectOverflow(ZombieSize size, float bodyHp, float armorHp)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var saved = ValidKilo();
            saved.Size = size;
            saved.IsBoss = false;
            saved.BodyHp = bodyHp;
            saved.HeldBlocks.Clear();
            if (size != ZombieSize.Kilo) saved.Buckets.RemoveAt(1);
            foreach (var armor in saved.Armor) armor.Hp = armorHp;
            var zombie = JsonSerializer.SerializeToNode(saved, JsonOptions)!.AsObject();
            document["ReturningZombies"] = new JsonArray(zombie);
            File.WriteAllText(path, document.ToJsonString());
            Assert.True(SaveStore.TryLoad(path, out _, out var validError), validError);

            zombie["BodyHp"] = bodyHp + .01f;
            File.WriteAllText(path, document.ToJsonString());
            Assert.False(SaveStore.TryLoad(path, out _, out _));

            zombie["BodyHp"] = bodyHp;
            zombie["Armor"]![0]!["Hp"] = armorHp + .01f;
            File.WriteAllText(path, document.ToJsonString());
            Assert.False(SaveStore.TryLoad(path, out _, out _));
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static SavedReturningZombie ValidKilo() => new()
    {
        TargetLevel = 1, TargetWaveIndex = 0, Id = 1000, Kind = ZombieKind.Armored, Tier = GearTier.Osmium,
        Size = ZombieSize.Kilo, IsBoss = true, BodyHp = 27, RetreatReturns = 3, OriginalBudgetCost = 10,
        CarriesBucket = true, CarriedBlocks = 0,
        Armor = Enum.GetValues<ArmorSlot>().Select(slot => new SavedZombieArmor
        { Slot = slot, Tier = GearTier.Osmium, Hp = 700, HeatProtectionRemaining = 8 }).ToList(),
        Buckets = [new() { Units = 7, Kind = LiquidKind.Water }, new() { Units = 0, Kind = null }],
        HeldBlocks =
        [
            new() { BlockIndex = 0, Material = Material.Dirt, Hp = 50, OffsetX = -.5f, OffsetZ = .5f },
            new() { BlockIndex = 1, Material = Material.Osmium, Hp = 7000, OffsetX = .5f, OffsetZ = .5f }
        ]
    };

    private static void WithSavePath(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-returning-validation-{Guid.NewGuid():N}.json");
        try { action(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
