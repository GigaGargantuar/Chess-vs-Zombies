using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MegaDurabilityDamageTests
{
    private const float WholeThrowDamage = 12;

    [Theory]
    [InlineData(Material.Stone, 500, 5)]
    [InlineData(Material.Wood, 250, 2.5f)]
    [InlineData(Material.Osmium, 8000, 80)]
    [InlineData(Material.Dirt, 50, .5f)]
    public void StickUsesTheLeadingContactBlocksRemainingHpRatherThanItsMaterialMaximumOrOtherBlocks(
        Material material, float hp, float ratio)
    {
        var game = new GameSession();
        var tower = AddTower(game, 200, new Cell(10, 20, 8));
        var flight = Stick(material, hp);
        // The original wielder's attack and equipment do not replace the
        // saved throw's base damage or the block that actually contacts.
        game.Zombies.Add(new Zombie
        {
            Id = flight.OwnerId, Size = ZombieSize.Mega, Tier = GearTier.Osmium,
            Kind = ZombieKind.ArmedArmored, Attack = 999,
            Position = new Vector3(24, 24, 24)
        });
        Restore(game, flight);

        Invoke(game, "UpdateMegaProjectiles", .25f);

        Assert.Equal(10000 - WholeThrowDamage * ratio, tower.Hp, 4);
        Assert.Equal(4, game.MegaProjectiles.Count);
        Assert.All(game.MegaProjectiles, block => Assert.True(block.IsDebris));
        AssertUnchangedBlocks(flight, game);
    }

    [Theory]
    [InlineData(Material.Stone, 500, 5)]
    [InlineData(Material.Wood, 250, 2.5f)]
    [InlineData(Material.Osmium, 8000, 80)]
    [InlineData(Material.Dirt, 50, .5f)]
    public void SingleDebrisBlockUsesItsQuarterShareTimesItsOwnRemainingHp(
        Material material, float hp, float ratio)
    {
        var game = new GameSession();
        var tower = AddTower(game, 200, new Cell(10, 20, 8));
        var flight = Debris(material, hp);
        Restore(game, flight);

        Invoke(game, "UpdateMegaProjectiles", .25f);

        Assert.Equal(10000 - WholeThrowDamage / 4 * ratio, tower.Hp, 4);
        Assert.Empty(game.MegaProjectiles);
        var drop = Assert.Single(game.PendingBlockDrops);
        Assert.Equal(material, drop.Content);
        Assert.Equal(hp, drop.Hp);
        Assert.True(drop.WasBarrier);
    }

    [Fact]
    public void MixedChunkAppliesTheActualContactBlocksRatioSeparatelyToEachTower()
    {
        var game = new GameSession();
        var stoneTarget = AddTower(game, 200, new Cell(10, 20, 7));
        var damagedWoodTarget = AddTower(game, 201, new Cell(10, 20, 9));
        var flight = MixedChunk();
        Restore(game, flight);

        Invoke(game, "UpdateMegaProjectiles", .25f);

        Assert.Equal(10000 - WholeThrowDamage / 4 * 5, stoneTarget.Hp, 4);
        Assert.Equal(10000 - WholeThrowDamage / 4 * .5f, damagedWoodTarget.Hp, 4);
        AssertUnchangedBlocks(flight, game);
    }

    [Fact]
    public void MixedChunkAppliesTheSameContactBasedModifierToTerrain()
    {
        var game = new GameSession();
        var stoneTarget = new Cell(10, 20, 7);
        var damagedWoodTarget = new Cell(10, 20, 9);
        AddTerrain(game, stoneTarget);
        AddTerrain(game, damagedWoodTarget);
        var flight = MixedChunk();
        Restore(game, flight);

        Invoke(game, "UpdateMegaProjectiles", .25f);

        Assert.Equal(Balance.OsmiumBlockHp - WholeThrowDamage / 4 * 5, game.World.GetBarrier(stoneTarget)!.Hp, 4);
        Assert.Equal(Balance.OsmiumBlockHp - WholeThrowDamage / 4 * .5f, game.World.GetBarrier(damagedWoodTarget)!.Hp, 4);
        AssertUnchangedBlocks(flight, game);
    }

    [Fact]
    public void DamagedDebrisUsesItsRemainingHpAgainstTerrainAndRetainsThatHpAfterImpact()
    {
        var game = new GameSession();
        var target = new Cell(10, 20, 8);
        AddTerrain(game, target);
        var flight = Debris(Material.Stone, 50);
        Restore(game, flight);

        Invoke(game, "UpdateMegaProjectiles", .25f);

        Assert.Equal(Balance.OsmiumBlockHp - WholeThrowDamage / 4 * .5f, game.World.GetBarrier(target)!.Hp, 4);
        var drop = Assert.Single(game.PendingBlockDrops);
        Assert.Equal(Material.Stone, drop.Content);
        Assert.Equal(50, drop.Hp);
        Assert.True(drop.WasBarrier);
    }

    [Fact]
    public void FlightCheckpointKeepsUnscaledBaseDamageSoItsContactRatioIsAppliedOnlyOnce()
    {
        var original = new GameSession();
        Restore(original, Debris(Material.Stone, 250));
        Invoke(original, "UpdateMegaProjectiles", .025f);
        var saved = Assert.IsType<List<SavedMegaWeaponFlight>>(Invoke(original, "ExportMegaWeaponFlights"));
        Assert.Equal(WholeThrowDamage, Assert.Single(saved).Damage);
        Assert.Equal(250, Assert.Single(saved[0].Blocks).Hp);
        var restored = new GameSession();
        var tower = AddTower(restored, 200, new Cell(10, 20, 8));
        Invoke(restored, "RestoreMegaWeaponFlights", saved);

        Invoke(restored, "UpdateMegaProjectiles", .25f);

        Assert.Equal(10000 - WholeThrowDamage / 4 * 2.5f, tower.Hp, 4);
        Assert.Equal(250, Assert.Single(restored.PendingBlockDrops).Hp);
    }

    private static SavedMegaWeaponFlight Stick(Material contactMaterial, float contactHp) => Flight(false,
        MegaWeaponFormation.Stick,
        [
            Part(0, Material.Gold, 3000, -1.5f, 0),
            Part(1, Material.Iron, 2000, -.5f, 0),
            Part(2, Material.Dirt, 80, .5f, 0),
            Part(3, contactMaterial, contactHp, 1.5f, 0)
        ]);

    private static SavedMegaWeaponFlight MixedChunk() => Flight(false, MegaWeaponFormation.Chunk,
        [
            Part(0, Material.Stone, 500, .5f, -.5f),
            Part(1, Material.Wood, 50, .5f, .5f),
            Part(2, Material.Osmium, 8000, -.5f, -.5f),
            Part(3, Material.Gold, 3000, -.5f, .5f)
        ]);

    private static SavedMegaWeaponFlight Debris(Material material, float hp) =>
        Flight(true, MegaWeaponFormation.Chunk, [Part(0, material, hp, 0, 0)]);

    private static SavedMegaWeaponFlight Flight(bool debris, MegaWeaponFormation formation, List<SavedMegaWeaponPart> parts) => new()
    {
        OwnerId = 100, Formation = formation, IsDebris = debris,
        X = 5, Y = 20.5f, Z = 8.5f, VelocityX = 40,
        Damage = WholeThrowDamage, Blocks = parts
    };

    private static SavedMegaWeaponPart Part(int index, Material material, float hp, float x, float z) => new()
    {
        BlockIndex = index, Material = material, Hp = hp, WasBarrier = true, OffsetX = x, OffsetZ = z
    };

    private static Turret AddTower(GameSession game, int id, Cell cell)
    {
        var turret = new Turret { Id = id, Kind = PieceKind.Pawn, Cell = cell, Hp = 10000 };
        game.Turrets.Add(turret);
        return turret;
    }

    private static void AddTerrain(GameSession game, Cell cell) =>
        Assert.True((bool)Invoke(game.World, "AddBarrier", cell, Material.Osmium, null)!);

    private static void Restore(GameSession game, SavedMegaWeaponFlight flight) =>
        Invoke(game, "RestoreMegaWeaponFlights", (object)new[] { flight });

    private static void AssertUnchangedBlocks(SavedMegaWeaponFlight before, GameSession game)
    {
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(before.Blocks.Count, game.MegaProjectiles.Count);
        foreach (var source in before.Blocks)
        {
            var actual = Assert.Single(game.MegaProjectiles, block => block.BlockIndex == source.BlockIndex);
            Assert.Equal(source.Material, actual.Material);
            Assert.Equal(source.Hp, actual.Hp);
            Assert.Equal(source.WasBarrier, actual.WasBarrier);
        }
    }

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
