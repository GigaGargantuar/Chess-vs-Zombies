using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class CombatFeedbackTests
{
    [Fact]
    public void PhysicalHitRecordsActualArmorAndBodyDamageAndStartsTheFlash()
    {
        var game = new GameSession();
        var zombie = Armored();

        game.DamageZombie(zombie, 25, DamageKind.Pierce, ShotKind.Pellet, -zombie.Facing);

        var hit = Assert.Single(game.Feedback.Hits);
        Assert.Equal(zombie.Id, hit.ZombieId);
        Assert.Equal(zombie.Position, hit.Position);
        Assert.Equal(25, hit.Amount);
        Assert.Equal(DamageKind.Pierce, hit.Kind);
        Assert.Equal(0, hit.Age);
        Assert.Equal(1, game.Feedback.GetFlash(zombie.Id));
        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(ArmorSlot.Helmet, Assert.Single(zombie.ArmorPieces).Slot);
    }

    [Fact]
    public void ArmorOnlyDamageStillGeneratesFeedback()
    {
        var game = new GameSession();
        var zombie = Armored();

        game.DamageZombie(zombie, 3, DamageKind.Pierce, ShotKind.Pellet, -zombie.Facing);

        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(3, Assert.Single(game.Feedback.Hits).Amount);
        Assert.Equal(1, game.Feedback.GetFlash(zombie.Id));
    }

    [Fact]
    public void LethalBodyDamageExcludesDiscardedArmorAndTheLabelSurvivesRemoval()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Waves = [new() { Groups = [new() { Kind = ZombieKind.Armored, Tier = GearTier.Osmium }] }]
        });
        var game = new GameSession(levels: levels);
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        var position = zombie.Position;

        game.DamageZombie(zombie, 1000, DamageKind.Chemical);

        Assert.Equal(640, game.Waves[0].DamageTaken);
        Assert.Equal(10, Assert.Single(game.Feedback.Hits).Amount);
        Assert.Equal(3, zombie.ArmorPieces.Count);
        game.Update(.01f);
        Assert.Empty(game.Zombies);
        var hit = Assert.Single(game.Feedback.Hits);
        Assert.Equal(position, hit.Position);
        Assert.Equal(10, hit.Amount);
        Assert.InRange(hit.Age, .0099f, .0101f);
        game.DamageZombie(zombie, 1000, DamageKind.Chemical);
        Assert.Single(game.Feedback.Hits);
    }

    [Fact]
    public void OverkillLabelsOnlyDurabilityActuallyRemoved()
    {
        var game = new GameSession();
        var zombie = Armored();

        game.DamageZombie(zombie, 1000, DamageKind.Pierce, ShotKind.Pellet, -zombie.Facing);

        Assert.Equal(30, Assert.Single(game.Feedback.Hits).Amount);
        Assert.Equal(ArmorSlot.Helmet, Assert.Single(zombie.ArmorPieces).Slot);
        Assert.Equal(0, zombie.BodyHp);
    }

    [Fact]
    public void FullyProtectedHeatAndInvalidHitsProduceNeitherLabelsNorFlashes()
    {
        var game = new GameSession();
        var zombie = Armored(GearTier.Osmium);

        game.DamageZombie(zombie, 10, DamageKind.Thermal);
        game.DamageZombie(zombie, 0);
        game.DamageZombie(zombie, -1);
        game.DamageZombie(zombie, float.NaN);
        game.DamageZombie(zombie, float.PositiveInfinity);
        game.DamageZombie(zombie, 1, (DamageKind)100);
        game.DamageZombie(zombie, 1, DamageKind.Pierce, (ShotKind)100);

        Assert.Empty(game.Feedback.Hits);
        Assert.Equal(0, game.Feedback.GetFlash(zombie.Id));
        Assert.Equal(10, zombie.BodyHp);
    }

    [Fact]
    public void PartiallyAbsorbedHeatLabelsOnlyTheUnprotectedDamage()
    {
        var game = new GameSession();
        var zombie = Armored(GearTier.Osmium);
        game.DamageZombie(zombie, 295, DamageKind.Thermal);
        Assert.Empty(game.Feedback.Hits);

        game.DamageZombie(zombie, 10, DamageKind.Thermal);

        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(5, Assert.Single(game.Feedback.Hits).Amount);
    }

    [Fact]
    public void ContinuousDamageAggregatesByZombieAndKindWithinFixedWindows()
    {
        var game = new GameSession();
        var first = Armored();
        var second = new Zombie { Id = 2, Position = new Cell(9, 16, 8).Center, WaveIndex = -1 };
        game.DamageZombie(first, 1, DamageKind.Chemical);
        var firstId = Assert.Single(game.Feedback.Hits).Id;
        var anchor = first.Position;
        game.Update(.1f);
        first.Position += Vector3.UnitX;
        game.DamageZombie(first, 2, DamageKind.Chemical);

        var aggregate = Assert.Single(game.Feedback.Hits);
        Assert.Equal(firstId, aggregate.Id);
        Assert.Equal(3, aggregate.Amount);
        Assert.Equal(anchor, aggregate.Position);
        Assert.InRange(aggregate.Age, .0999f, .1001f);
        game.DamageZombie(first, 1, DamageKind.Thermal);
        game.DamageZombie(second, 1, DamageKind.Chemical);
        Assert.Equal(3, game.Feedback.Hits.Count);

        game.Update(.11f);
        game.DamageZombie(first, 1, DamageKind.Chemical);
        Assert.Equal(4, game.Feedback.Hits.Count);
        Assert.Equal(2, game.Feedback.Hits.Count(hit => hit.ZombieId == first.Id && hit.Kind == DamageKind.Chemical));
        Assert.Equal(4, game.Feedback.Hits.Select(hit => hit.Id).Distinct().Count());
    }

    [Fact]
    public void SeparatePhysicalImpactsKeepSeparateLabelsAndRefreshTheFlashImmediately()
    {
        var game = new GameSession();
        var zombie = Armored();
        game.DamageZombie(zombie, 1, DamageKind.Pierce);
        game.Update(.1f);
        Assert.InRange(game.Feedback.GetFlash(zombie.Id), .3332f, .3335f);
        game.DamageZombie(zombie, 2, DamageKind.Pierce);

        Assert.Equal(1, game.Feedback.GetFlash(zombie.Id));
        Assert.Equal(new[] { 1f, 2f }, game.Feedback.Hits.Select(hit => hit.Amount));
        Assert.Equal(2, game.Feedback.Hits.Select(hit => hit.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(DamageKind.Chemical)]
    [InlineData(DamageKind.Thermal)]
    public void ContinuousFlashPulsesOnlyForNewBucketsAndNumbersKeepTheirOriginalLifetime(DamageKind kind)
    {
        var game = new GameSession();
        var zombie = Armored();
        game.DamageZombie(zombie, 1, kind);
        game.Update(.1f);
        Assert.InRange(game.Feedback.GetFlash(zombie.Id), .3332f, .3335f);
        game.DamageZombie(zombie, 1, kind);
        Assert.InRange(game.Feedback.GetFlash(zombie.Id), .3332f, .3335f);
        Assert.InRange(Assert.Single(game.Feedback.Hits).Age, .0999f, .1001f);
        game.Update(.1f);
        Assert.Equal(0, game.Feedback.GetFlash(zombie.Id));
        Assert.Single(game.Feedback.Hits);

        game.DamageZombie(zombie, 1, kind);
        Assert.Equal(1, game.Feedback.GetFlash(zombie.Id));
        Assert.Equal(2, game.Feedback.Hits.Count);
        Assert.Equal(new[] { 2f, 1f }, game.Feedback.Hits.Select(hit => hit.Amount));

        game.Update(.8f);
        Assert.Equal(0, game.Feedback.GetFlash(zombie.Id));
        Assert.Equal(1, Assert.Single(game.Feedback.Hits).Amount);
        Assert.InRange(Assert.Single(game.Feedback.Hits).Age, .7999f, .8001f);
        game.Update(.2f);
        Assert.Empty(game.Feedback.Hits);
    }

    [Fact]
    public void ALongSimulationUpdateStillSplitsContinuousDamageIntoShortWindows()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Kind = ZombieKind.Armored, Tier = GearTier.Iron }] }] });
        var game = new GameSession(levels: levels);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        var cell = new Cell(8, 13, 8);
        Assert.True(game.Dig(cell));
        Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Lava, 5));
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = cell.Center;
        zombie.NextCell = null;
        zombie.Cooldown = 1000;

        game.Update(.5f);

        Assert.Equal(3, game.Feedback.Hits.Count);
        Assert.All(game.Feedback.Hits, hit => Assert.Equal(DamageKind.Thermal, hit.Kind));
        Assert.InRange(game.Feedback.Hits.Sum(hit => hit.Amount), 4.999f, 5.001f);
        Assert.InRange(zombie.BodyHp, 4.999f, 5.001f);
    }

    [Fact]
    public void FeedbackContinuesToExpireAfterDefeatStopsCombat()
    {
        var game = new GameSession();
        Assert.True(game.StartLevel());
        var zombie = new Zombie
        {
            Id = 100, Position = new Cell(14, 16, 15).Center, Attack = Balance.CoreHp, WaveIndex = -1
        };
        game.Zombies.Add(zombie);
        game.DamageZombie(zombie, 1);

        game.Update(.01f);
        Assert.Equal(GamePhase.Defeat, game.Phase);
        Assert.Single(game.Feedback.Hits);
        game.Update(1);

        Assert.Empty(game.Feedback.Hits);
        Assert.Equal(0, game.Feedback.GetFlash(zombie.Id));
    }

    [Fact]
    public void ResetAndCheckpointLoadingDoNotRetainTransientFeedback()
    {
        var game = new GameSession();
        var zombie = Armored();
        game.DamageZombie(zombie, 1);
        var path = Path.Combine(Path.GetTempPath(), "ChessVsZombiesFeedback-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SaveStore.Save(game, path);
            Assert.True(SaveStore.TryLoad(path, out var restored, out var error), error);
            Assert.NotNull(restored);
            Assert.Empty(restored.Feedback.Hits);
            Assert.Equal(0, restored.Feedback.GetFlash(zombie.Id));
        }
        finally { File.Delete(path); }

        game.Reset();
        Assert.Empty(game.Feedback.Hits);
        Assert.Equal(0, game.Feedback.GetFlash(zombie.Id));
    }

    private static Zombie Armored(GearTier tier = GearTier.Wood) => new()
    {
        Id = 1, Kind = ZombieKind.Armored, Tier = tier, Position = new Cell(8, 16, 8).Center, WaveIndex = -1
    };
}
