using System.Numerics;
using System.Text.Json.Nodes;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class BucketCampaignPersistenceTests
{
    [Fact]
    public void CheckpointsPreserveFlyingAndPendingBucketLiquidWithoutDuplicatingItsUnits()
    {
        WithSavePath(path =>
        {
            var original = CreatePayloads();
            var flying = Assert.Single(original.Buckets.Flights);
            var pending = Assert.Single(original.Buckets.PendingSplashes);
            SaveStore.Save(original, path);

            var document = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(flying.Origin.X, document["Buckets"]!["Flights"]![0]!["Origin"]!["X"]!.GetValue<float>());
            Assert.Equal(flying.Direction.Z, document["Buckets"]!["Flights"]![0]!["Direction"]!["Z"]!.GetValue<float>());
            Assert.Equal(pending.Center.Y, document["Buckets"]!["PendingSplashes"]![0]!["Center"]!["Y"]!.GetValue<float>());
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            var restoredFlight = Assert.Single(restored.Buckets.Flights);
            Assert.Equal(flying.Origin, restoredFlight.Origin);
            Assert.Equal(flying.Direction, restoredFlight.Direction);
            Assert.Equal(flying.Position, restoredFlight.Position);
            Assert.Equal(flying.Kind, restoredFlight.Kind);
            Assert.Equal(flying.Units, restoredFlight.Units);
            Assert.Equal(flying.Age, restoredFlight.Age);
            Assert.Equal(pending, Assert.Single(restored.Buckets.PendingSplashes));
            Assert.Empty(restored.Liquids.Cells);
            Assert.Equal(14, restored.Buckets.Flights.Sum(flight => flight.Units) + restored.Buckets.PendingUnits);

            // Continuing both simulations verifies the restored parcels land in
            // the same cells; merely preserving serialized counts is insufficient.
            original.Buckets.Update(2);
            restored.Buckets.Update(2);
            Assert.Empty(original.Buckets.Flights);
            Assert.Empty(restored.Buckets.Flights);
            Assert.Empty(original.Buckets.PendingSplashes);
            Assert.Empty(restored.Buckets.PendingSplashes);
            Assert.Equal(original.Liquids.ExportState().Cells.OrderBy(cell => cell.Cell.ToString()),
                restored.Liquids.ExportState().Cells.OrderBy(cell => cell.Cell.ToString()));
            Assert.Equal(14, restored.Liquids.ExportState().Cells.Sum(cell => cell.Water + cell.Mercury + cell.Lava));
        });
    }

    [Fact]
    public void CheckpointsCreatedBeforeBucketsLoadWithoutInventingLiquid()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(new GameSession(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document.Remove("Buckets");
            string previousCheckpoint = document.ToJsonString();
            File.WriteAllText(path, previousCheckpoint);

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Empty(restored.Buckets.Flights);
            Assert.Empty(restored.Buckets.PendingSplashes);
            Assert.Empty(restored.Liquids.Cells);
            Assert.Equal(previousCheckpoint, File.ReadAllText(path));
        });
    }

    [Fact]
    public void AnOlderCheckpointWithABucketAboveTheCeilingLoadsAndRecordsItsEscapeOnlyOnce()
    {
        WithSavePath(path =>
        {
            SaveStore.Save(CreatePayloads(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var flight = document["Buckets"]!["Flights"]![0]!;
            flight["Origin"]!["Y"] = 31.8f;
            flight["Age"] = .2f;
            File.WriteAllText(path, document.ToJsonString());

            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);

            Assert.Empty(restored!.Buckets.Flights);
            Assert.Equal(10, restored.Liquids.GetEscapedUnits(LiquidKind.Lava));
            Assert.Equal(4, restored.Buckets.PendingUnits);
            SaveStore.Save(restored, path);
            Assert.True(SaveStore.TryLoad(path, out var again, out error), error);
            Assert.Equal(10, again!.Liquids.GetEscapedUnits(LiquidKind.Lava));
            Assert.Empty(again.Buckets.Flights);
            Assert.Equal(4, again.Buckets.PendingUnits);
        });
    }

    [Theory]
    [InlineData("missing-origin-x")]
    [InlineData("missing-origin")]
    [InlineData("missing-direction-z")]
    [InlineData("missing-splash-y")]
    [InlineData("infinite-origin")]
    [InlineData("string-coordinate")]
    [InlineData("negative-age")]
    [InlineData("infinite-age")]
    [InlineData("missing-age")]
    [InlineData("invalid-kind")]
    [InlineData("integer-kind")]
    [InlineData("empty-flight")]
    [InlineData("oversized-flight")]
    [InlineData("empty-splash")]
    [InlineData("invalid-direction")]
    [InlineData("outside-origin")]
    [InlineData("missing-flights")]
    [InlineData("null-flight")]
    [InlineData("null-splash")]
    public void MalformedBucketStateIsRejectedWithoutChangingTheCheckpoint(string fault)
    {
        WithSavePath(path =>
        {
            SaveStore.Save(CreatePayloads(), path);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var buckets = document["Buckets"]!.AsObject();
            var flight = buckets["Flights"]![0]!.AsObject();
            var splash = buckets["PendingSplashes"]![0]!.AsObject();
            switch (fault)
            {
                case "missing-origin-x": flight["Origin"]!.AsObject().Remove("X"); break;
                case "missing-origin": flight.Remove("Origin"); break;
                case "missing-direction-z": flight["Direction"]!.AsObject().Remove("Z"); break;
                case "missing-splash-y": splash["Center"]!.AsObject().Remove("Y"); break;
                case "infinite-origin": flight["Origin"]!["X"] = JsonNode.Parse("1e40"); break;
                case "string-coordinate": flight["Origin"]!["X"] = "5.25"; break;
                case "negative-age": flight["Age"] = -.1f; break;
                case "infinite-age": flight["Age"] = JsonNode.Parse("1e40"); break;
                case "missing-age": flight.Remove("Age"); break;
                case "invalid-kind": flight["Kind"] = "Oil"; break;
                case "integer-kind": flight["Kind"] = 2; break;
                case "empty-flight": flight["Units"] = 0; break;
                case "oversized-flight": flight["Units"] = 16; break;
                case "empty-splash": splash["Units"] = 0; break;
                case "invalid-direction": flight["Direction"]!["Y"] = 1; break;
                case "outside-origin": flight["Origin"]!["X"] = -1; break;
                case "missing-flights": buckets.Remove("Flights"); break;
                case "null-flight": buckets["Flights"]![0] = null; break;
                case "null-splash": buckets["PendingSplashes"]![0] = null; break;
                default: throw new ArgumentOutOfRangeException(nameof(fault));
            }
            string invalidCheckpoint = document.ToJsonString();
            File.WriteAllText(path, invalidCheckpoint);

            Assert.False(SaveStore.TryLoad(path, out var restored, out var error));
            Assert.Null(restored);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(invalidCheckpoint, File.ReadAllText(path));
        });
    }

    private static GameSession CreatePayloads()
    {
        var game = new GameSession();
        Assert.True(game.Buckets.Launch(new Vector3(5.25f, 16.4f, 5.75f), Vector3.UnitZ, LiquidKind.Lava, 10));
        game.Buckets.Update(.12f);
        Assert.True(game.Buckets.Spill(new Vector3(20.2f, 16.1f, 20.8f), LiquidKind.Mercury, 4));
        return game;
    }

    private static void WithSavePath(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-buckets-{Guid.NewGuid():N}.json");
        try { action(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
