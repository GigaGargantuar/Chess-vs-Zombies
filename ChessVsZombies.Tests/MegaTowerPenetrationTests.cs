using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MegaTowerPenetrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DebrisPiercesEveryWeakTowerInOneUpdateInPhysicalRatherThanListOrder(bool reverse)
    {
        var game = new GameSession();
        var towers = new[]
        {
            Tower(201, 8, 5), Tower(202, 10, 10), Tower(203, 12, 100)
        };
        foreach (var tower in reverse ? towers.Reverse() : towers) AddTower(game, tower);
        Restore(game, Debris(Material.Stone, 500, damage: 400));

        Advance(game, .25f);

        Assert.Equal(-495, towers[0].Hp, 3);
        Assert.Equal(-485, towers[1].Hp, 3);
        Assert.Equal(-385, towers[2].Hp, 3);
        Assert.Empty(game.Turrets);
        var block = Assert.Single(game.MegaProjectiles);
        Assert.Equal(385, block.Hp);
        Assert.True(block.Min.X > 14);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void NextTowerUsesTheContactBlocksReducedDurabilityAndASurvivingTowerEndsTheImpact()
    {
        var game = new GameSession();
        var first = AddTower(game, Tower(201, 8, 10));
        var second = AddTower(game, Tower(202, 11, 50));
        var beyond = AddTower(game, Tower(203, 14, 50));
        Restore(game, Debris(Material.Stone, 500));

        Advance(game, .3f);

        Assert.Equal(-5, first.Hp, 4);
        Assert.Equal(35.3f, second.Hp, 4);
        Assert.Equal(50, beyond.Hp);
        Assert.Empty(game.MegaProjectiles);
        var drop = Assert.Single(game.PendingBlockDrops);
        Assert.Equal(Material.Stone, drop.Content);
        Assert.Equal(440, drop.Hp);
        Advance(game, .3f);
        Assert.Equal(35.3f, second.Hp, 4);
        Assert.Single(game.PendingBlockDrops);
    }

    [Fact]
    public void StickKeepsItsFormationAndSpendsOnlyTheLeadingBlocksHpAcrossKills()
    {
        var game = new GameSession();
        var first = AddTower(game, Tower(201, 8, 10));
        var second = AddTower(game, Tower(202, 12, 20));
        var flight = Stick();
        Restore(game, flight);

        Advance(game, .25f);

        Assert.Equal(-50, first.Hp, 3);
        Assert.Equal(-38.8f, second.Hp, 3);
        Assert.Equal(4, game.MegaProjectiles.Count);
        Assert.All(game.MegaProjectiles, block => Assert.False(block.IsDebris));
        Assert.Equal(470, Assert.Single(game.MegaProjectiles, block => block.BlockIndex == 3).Hp);
        foreach (var source in flight.Blocks.Where(block => block.BlockIndex != 3))
            Assert.Equal(source.Hp, Assert.Single(game.MegaProjectiles,
                block => block.BlockIndex == source.BlockIndex).Hp);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void ExactMutualDestructionConsumesTheBlockAndLeavesTheNextTowerUntouched()
    {
        var game = new GameSession();
        var first = AddTower(game, Tower(201, 8, 100));
        var second = AddTower(game, Tower(202, 11, 100));
        Restore(game, Debris(Material.Dirt, 100, damage: 400));

        Advance(game, .25f);

        Assert.Equal(0, first.Hp);
        Assert.Equal(100, second.Hp);
        Assert.Empty(game.MegaProjectiles);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.DirtCount);
    }

    [Fact]
    public void ABlockDestroyedByASurvivingTowerDoesNotBecomeAWorldOrInventoryDrop()
    {
        var game = new GameSession();
        var target = AddTower(game, Tower(201, 8, 100));
        Restore(game, Debris(Material.Dirt, 25, damage: 400));

        Advance(game, .25f);

        Assert.Equal(75, target.Hp);
        Assert.Empty(game.MegaProjectiles);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(8, 20, 8)));
    }

    [Fact]
    public void AStoppedStickLosesOnlyItsDestroyedContactPartAndScattersTheRearParts()
    {
        var game = new GameSession();
        var target = AddTower(game, Tower(201, 10, 30));
        var flight = Stick();
        flight.Damage = 400;
        flight.Blocks[3].Material = Material.Dirt;
        flight.Blocks[3].Hp = 5;
        Restore(game, flight);

        Advance(game, .2f);

        Assert.Equal(10, target.Hp, 3);
        Assert.Equal(new[] { 0, 1, 2 }, game.MegaProjectiles.Select(block => block.BlockIndex).Order());
        Assert.All(game.MegaProjectiles, block => Assert.True(block.IsDebris));
        foreach (var source in flight.Blocks.Where(block => block.BlockIndex != 3))
            Assert.Equal(source.Hp, Assert.Single(game.MegaProjectiles,
                block => block.BlockIndex == source.BlockIndex).Hp);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void MixedChunkDebitsEachContactingPieceIndependentlyWhilePiercingBothLanes()
    {
        var game = new GameSession();
        var stoneTarget = AddTower(game, Tower(201, 9, 5, z: 7));
        var woodTarget = AddTower(game, Tower(202, 9, 5, z: 9));
        Restore(game, Chunk());

        Advance(game, .2f);

        Assert.Equal(-10, stoneTarget.Hp, 3);
        Assert.Equal(-2.5f, woodTarget.Hp, 3);
        Assert.Equal(4, game.MegaProjectiles.Count);
        Assert.All(game.MegaProjectiles, block => Assert.False(block.IsDebris));
        Assert.Equal(495, Assert.Single(game.MegaProjectiles, block => block.BlockIndex == 0).Hp);
        Assert.Equal(245, Assert.Single(game.MegaProjectiles, block => block.BlockIndex == 1).Hp);
        Assert.Equal(100, Assert.Single(game.MegaProjectiles, block => block.BlockIndex == 2).Hp);
        Assert.Equal(100, Assert.Single(game.MegaProjectiles, block => block.BlockIndex == 3).Hp);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void AChunkSeamDoesNotSpendTwoBlocksOrStrikeTheSameTowerTwice()
    {
        var game = new GameSession();
        var target = AddTower(game, Tower(201, 10, 100));
        var flight = Chunk();
        foreach (var block in flight.Blocks)
        {
            block.Material = Material.Osmium;
            block.Hp = 8000;
        }
        // The target straddles the two front cubes. Its one first-contact cube
        // pays its pre-impact HP; the adjoining cube does not pay it again.
        Restore(game, flight);

        Advance(game, .2f);

        Assert.Equal(-140, target.Hp, 3);
        Assert.Equal(4, game.MegaProjectiles.Count);
        Assert.Equal(7900, Assert.Single(game.MegaProjectiles, block => block.BlockIndex == 0).Hp);
        Assert.All(game.MegaProjectiles.Where(block => block.BlockIndex != 0), block => Assert.Equal(8000, block.Hp));
        Assert.All(game.MegaProjectiles, block => Assert.False(block.IsDebris));
    }

    [Fact]
    public void SurvivingChunkContactEndsTheImpactOnceAndAllSurvivingPartsScatter()
    {
        var game = new GameSession();
        var target = AddTower(game, Tower(201, 10, 1000));
        var flight = Chunk();
        foreach (var block in flight.Blocks)
        {
            block.Material = Material.Osmium;
            block.Hp = 8000;
        }
        Restore(game, flight);

        Advance(game, .2f);

        Assert.Equal(760, target.Hp, 3);
        Assert.Equal(4, game.MegaProjectiles.Count);
        Assert.All(game.MegaProjectiles, block => Assert.True(block.IsDebris));
        Assert.Equal(7000, Assert.Single(game.MegaProjectiles, block => block.BlockIndex == 0).Hp);
        Assert.All(game.MegaProjectiles.Where(block => block.BlockIndex != 0), block => Assert.Equal(8000, block.Hp));
        Advance(game, .005f);
        Assert.Equal(760, target.Hp, 3);
    }

    [Fact]
    public void TerrainStillEndsTheFlightEvenWhenItsCoverBlockIsDestroyedAfterPiercingATower()
    {
        var game = new GameSession();
        var target = AddTower(game, Tower(201, 8, 5));
        Support(game, 12, 8, top: 20);
        var cover = new Cell(12, 21, 8);
        Assert.True((bool)Invoke(game.World, "AddBarrier", cover, Material.Dirt, null)!);
        Restore(game, Debris(Material.Stone, 500, damage: 400));

        Advance(game, .25f);

        Assert.Equal(-495, target.Hp, 3);
        Assert.Equal(Material.Air, game.World.GetMaterial(cover));
        Assert.Empty(game.MegaProjectiles);
        var drop = Assert.Single(game.PendingBlockDrops);
        Assert.Equal(495, drop.Hp);
        Assert.Equal(Material.Stone, drop.Content);
        Assert.True(drop.Cell.X < 12);
        Assert.Equal(0, game.Inventory.DirtCount);
    }

    [Fact]
    public void CheckpointAfterPiercingPreservesReducedHpAndContinuesWithoutReplayingTheFirstTower()
    {
        var original = new GameSession();
        var first = AddTower(original, Tower(201, 7, 5));
        var next = AddTower(original, Tower(202, 12, 50));
        Restore(original, Debris(Material.Stone, 500));
        Advance(original, .06f);
        var saved = Export(original);
        Assert.Equal(495, Assert.Single(Assert.Single(saved).Blocks).Hp);
        Assert.Contains(first.Id, saved[0].DamagedTowerIds);
        Assert.Equal(50, next.Hp);
        var restored = new GameSession();
        var restoredNext = AddTower(restored, Tower(202, 12, 50));
        Restore(restored, saved.ToArray());

        Advance(original, .2f);
        Advance(restored, .2f);

        Assert.Equal(-10, first.Hp, 4);
        Assert.Equal(35.15f, next.Hp, 4);
        Assert.Equal(next.Hp, restoredNext.Hp);
        Assert.Equal(original.MegaProjectiles, restored.MegaProjectiles);
        Assert.Equal(original.PendingBlockDrops, restored.PendingBlockDrops);
        Assert.Equal(445, Assert.Single(restored.PendingBlockDrops).Hp);
    }

    [Fact]
    public void ABlockThatPiercedATowerStillInterceptsALaterBulletWithTheCorrectEventOrder()
    {
        var game = new GameSession();
        var target = AddTower(game, Tower(201, 7, 1));
        var flight = Debris(Material.Dirt, 100);
        flight.X = 5.5f;
        Restore(game, flight);
        var below = new Zombie { Id = 300, Position = new Vector3(8, 18.5f, 8.5f) };
        game.Zombies.Add(below);
        game.Projectiles.Add(new Projectile
        {
            Origin = new Vector3(8, 24.1f, 8.5f), Target = new Vector3(8, 18.1f, 8.5f),
            Kind = ShotKind.Pellet, Damage = 7, Duration = .1f
        });

        Invoke(game, "UpdateProjectiles", .1f);

        Assert.Equal(-2, target.Hp, 4);
        Assert.Empty(game.Projectiles);
        Assert.Equal(10, below.BodyHp);
        Assert.Equal(92, Assert.Single(game.MegaProjectiles).Hp);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void ALethalBulletAfterPiercingDoesNotUndoTheEarlierTowerHitOrCreateADrop()
    {
        var game = new GameSession();
        var target = AddTower(game, Tower(201, 7, 1));
        var flight = Debris(Material.Dirt, 100);
        flight.X = 5.5f;
        Restore(game, flight);
        var below = new Zombie { Id = 300, Position = new Vector3(8, 18.5f, 8.5f) };
        game.Zombies.Add(below);
        game.Projectiles.Add(new Projectile
        {
            Origin = new Vector3(8, 24.1f, 8.5f), Target = new Vector3(8, 18.1f, 8.5f),
            Kind = ShotKind.Pellet, Damage = 150, Duration = .1f
        });

        Invoke(game, "UpdateProjectiles", .1f);

        Assert.Equal(-2, target.Hp, 4);
        Assert.DoesNotContain(target, game.Turrets);
        Assert.Empty(game.Projectiles);
        Assert.Empty(game.MegaProjectiles);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Equal(10, below.BodyHp);
    }

    [Fact]
    public void AnEarlierBulletCanMakeTheNextTowerSurviveAndChargesItsFullPreHitHpToTheBlock()
    {
        var game = new GameSession();
        var target = AddTower(game, Tower(201, 8, 2));
        var flight = Debris(Material.Dirt, 100);
        flight.X = 5.5f;
        Restore(game, flight);
        // The shot reaches the cube around t=.01, before it reaches the tower
        // around t=.05. Its remaining 50 HP gives 1.5 damage instead of 3.
        game.Projectiles.Add(new Projectile
        {
            Origin = new Vector3(6, 24.1f, 8.5f), Target = new Vector3(6, 18.1f, 8.5f),
            Kind = ShotKind.Pellet, Damage = 50, Duration = .02f
        });

        Invoke(game, "UpdateProjectiles", .1f);

        Assert.Equal(.5f, target.Hp, 4);
        Assert.Contains(target, game.Turrets);
        Assert.Empty(game.Projectiles);
        Assert.Empty(game.MegaProjectiles);
        var drop = Assert.Single(game.PendingBlockDrops);
        Assert.Equal(Material.Dirt, drop.Content);
        Assert.Equal(48, drop.Hp);
        Assert.Equal(0, game.Inventory.DirtCount);
    }

    private static SavedMegaWeaponFlight Debris(Material material, float hp, float damage = 12) => new()
    {
        OwnerId = 100, Formation = MegaWeaponFormation.Chunk, IsDebris = true,
        X = 5, Y = 21.1f, Z = 8.5f, VelocityX = 40, Damage = damage,
        Blocks = [Part(0, material, hp, 0, 0)]
    };

    private static SavedMegaWeaponFlight Stick() => new()
    {
        OwnerId = 100, Formation = MegaWeaponFormation.Stick,
        X = 5, Y = 21.1f, Z = 8.5f, VelocityX = 40, Damage = 12,
        Blocks =
        [
            Part(0, Material.Wood, 250, -1.5f, 0),
            Part(1, Material.Iron, 2000, -.5f, 0),
            Part(2, Material.Dirt, 100, .5f, 0),
            Part(3, Material.Stone, 500, 1.5f, 0)
        ]
    };

    private static SavedMegaWeaponFlight Chunk() => new()
    {
        OwnerId = 100, Formation = MegaWeaponFormation.Chunk,
        X = 5, Y = 21.1f, Z = 8.5f, VelocityX = 40, Damage = 12,
        Blocks =
        [
            Part(0, Material.Stone, 500, .5f, -.5f),
            Part(1, Material.Wood, 250, .5f, .5f),
            Part(2, Material.Dirt, 100, -.5f, -.5f),
            Part(3, Material.Dirt, 100, -.5f, .5f)
        ]
    };

    private static SavedMegaWeaponPart Part(int index, Material material, float hp, float x, float z) => new()
    {
        BlockIndex = index, Material = material, Hp = hp, WasBarrier = true,
        OffsetX = x, OffsetZ = z
    };

    private static Turret Tower(int id, int x, float hp, int z = 8) => new()
    {
        Id = id, Cell = new Cell(x, 20, z), Kind = PieceKind.Pawn, Hp = hp
    };

    private static Turret AddTower(GameSession game, Turret tower)
    {
        Support(game, tower.Cell.X, tower.Cell.Z, top: 19);
        game.Turrets.Add(tower);
        return tower;
    }

    private static void Support(GameSession game, int x, int z, int top)
    {
        for (int y = 16; y <= top; y++)
            Assert.True((bool)Invoke(game.World, "AddBarrier", new Cell(x, y, z), Material.Osmium, null)!);
    }

    private static void Advance(GameSession game, float dt) => Invoke(game, "UpdateMegaProjectiles", dt);

    private static void Restore(GameSession game, params SavedMegaWeaponFlight[] flights) =>
        Invoke(game, "RestoreMegaWeaponFlights", (object)flights);

    private static List<SavedMegaWeaponFlight> Export(GameSession game) =>
        Assert.IsType<List<SavedMegaWeaponFlight>>(Invoke(game, "ExportMegaWeaponFlights"));

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(target, arguments);
}
