using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ThrownBlockInterceptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlierBulletHitsTheLastHpOfACubeRegardlessOfProjectileAppendOrder(bool reverse)
    {
        var game = new GameSession();
        var flight = Debris(8, 20.5f, 8.5f);
        flight.Blocks[0].Hp = 1;
        Restore(game, flight);
        var zombie = new Zombie { Id = 300, Position = new Vector3(11, 20.5f, 8.5f) };
        game.Zombies.Add(zombie);
        var early = new Projectile
        {
            Origin = new Vector3(6.5f, 20.5f, 8.5f), Target = new Vector3(12.5f, 20.5f, 8.5f),
            Duration = .05f, Kind = ShotKind.Pellet, Damage = 2
        };
        var late = new Projectile
        {
            Origin = new Vector3(2, 20.5f, 8.5f), Target = new Vector3(14, 20.5f, 8.5f),
            Duration = .05f, Kind = ShotKind.Pellet, Damage = 7
        };
        game.Projectiles.AddRange(reverse ? [early, late] : [late, early]);

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Empty(game.MegaProjectiles);
        Assert.Empty(game.Projectiles);
        Assert.Equal(3, zombie.BodyHp);
    }

    [Fact]
    public void DebrisAbsorbsABulletBeforeTheZombieBehindIt()
    {
        var game = new GameSession();
        Restore(game, Debris(8, 20.5f, 8.5f));
        var zombie = new Zombie { Id = 300, Position = new Vector3(11, 20.5f, 8.5f) };
        game.Zombies.Add(zombie);
        Shoot(game, new Vector3(2, 20.5f, 8.5f), new Vector3(14, 20.5f, 8.5f), 7);

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(93, Assert.Single(game.MegaProjectiles).Hp);
        Assert.Equal(10, zombie.BodyHp);
    }

    [Fact]
    public void FastMovingBlockAndBulletCollideWhereTheirPathsCrossBetweenBothEndPoses()
    {
        var game = new GameSession();
        var flight = Debris(8, 20.5f, 5.5f);
        flight.VelocityZ = 120;
        Restore(game, flight);
        Shoot(game, new Vector3(2, 20.5f, 8.5f), new Vector3(14, 20.5f, 8.5f), 9);

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Empty(game.Projectiles);
        var block = Assert.Single(game.MegaProjectiles);
        Assert.Equal(91, block.Hp);
        Assert.Equal(11, block.Min.Z, 4);
    }

    [Fact]
    public void GeometricallyCrossingPathsAtDifferentTimesDoNotHit()
    {
        var game = new GameSession();
        var flight = Debris(8, 20.5f, 5.5f);
        flight.VelocityZ = 240;
        Restore(game, flight);
        Shoot(game, new Vector3(1, 20.5f, 8.5f), new Vector3(11, 20.5f, 8.5f), 9);

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Single(game.Projectiles);
        Assert.Equal(100, Assert.Single(game.MegaProjectiles).Hp);
    }

    [Fact]
    public void LobbedProjectileHitsAThrownBlockAlongItsActualArc()
    {
        var game = new GameSession();
        Restore(game, Debris(2.8f, 21, 8.5f));
        game.Projectiles.Add(new Projectile
        {
            Origin = new Vector3(2, 20, 8.5f), Target = new Vector3(12, 20, 8.5f),
            Duration = 1, Kind = ShotKind.Lob, Damage = 11
        });

        Invoke(game, "UpdateProjectiles", .1f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(89, Assert.Single(game.MegaProjectiles).Hp);
    }

    [Fact]
    public void NearestThrownBlockWinsEvenWhenItWasStoredAfterTheFartherBlock()
    {
        var game = new GameSession();
        var far = Debris(10, 20.5f, 8.5f);
        far.OwnerId = 101;
        Restore(game, far, Debris(6, 20.5f, 8.5f));
        Shoot(game, new Vector3(2, 20.5f, 8.5f), new Vector3(14, 20.5f, 8.5f), 7);

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Equal(93, Assert.Single(game.MegaProjectiles, block => block.CarrierId == 100).Hp);
        Assert.Equal(100, Assert.Single(game.MegaProjectiles, block => block.CarrierId == 101).Hp);
    }

    [Fact]
    public void TerrainInFrontOfAThrownBlockStillReceivesTheBulletFirst()
    {
        var game = new GameSession();
        var cover = new Cell(6, 20, 8);
        Assert.True((bool)Invoke(game.World, "AddBarrier", cover, Material.Dirt, null)!);
        Restore(game, Debris(9, 20.5f, 8.5f));
        Shoot(game, new Vector3(2, 20.5f, 8.5f), new Vector3(14, 20.5f, 8.5f), 7);

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(93, game.World.GetBarrier(cover)!.Hp);
        Assert.Equal(100, Assert.Single(game.MegaProjectiles).Hp);
    }

    [Fact]
    public void ZombieInFrontOfAThrownBlockStillReceivesTheBulletFirst()
    {
        var game = new GameSession();
        var zombie = new Zombie { Id = 300, Position = new Vector3(6, 20.5f, 8.5f) };
        game.Zombies.Add(zombie);
        Restore(game, Debris(9, 20.5f, 8.5f));
        Shoot(game, new Vector3(2, 20.5f, 8.5f), new Vector3(14, 20.5f, 8.5f), 7);

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Equal(3, zombie.BodyHp);
        Assert.Equal(100, Assert.Single(game.MegaProjectiles).Hp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrownBlocksAndHeldBlocksCompeteForTheActualNearestContact(bool heldFirst)
    {
        var game = new GameSession();
        var kilo = new Zombie { Id = 300, Size = ZombieSize.Kilo, Position = new Vector3(heldFirst ? 6 : 11, 20.5f, 8.5f) };
        kilo.FaceToward(-Vector3.UnitX);
        game.Zombies.Add(kilo);
        Assert.True((bool)Invoke(game, "TryRaiseKiloBackpackCover", kilo)!);
        Restore(game, Debris(8, 20.5f, 8));
        Shoot(game, new Vector3(2, 20.5f, 8), new Vector3(14, 20.5f, 8), 7);

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(heldFirst ? 93 : 100, Assert.Single(game.KiloCovers).Hp);
        Assert.Equal(heldFirst ? 100 : 93, Assert.Single(game.MegaProjectiles).Hp);
        Assert.Equal(40, kilo.BodyHp);
    }

    [Fact]
    public void PublicUpdateAdvancesTheFlightOnlyOnceWithoutAnyIncomingShots()
    {
        var game = new GameSession();
        var flight = Debris(8, 20.5f, 8.5f);
        flight.VelocityX = 2;
        Restore(game, flight);

        game.Update(.05f);

        var saved = Assert.Single(Export(game));
        Assert.Equal(8.1f, saved.X, 4);
        Assert.Equal(.05f, saved.Age, 4);
    }

    [Fact]
    public void DestroyingTheLastFlyingBlockConsumesTheBulletWithoutCreatingInventoryOrWorldDrops()
    {
        var game = new GameSession();
        Restore(game, Debris(8, 20.5f, 8.5f));
        var before = game.Inventory.DirtCount;
        Shoot(game, new Vector3(2, 20.5f, 8.5f), new Vector3(14, 20.5f, 8.5f), 150);

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Empty(game.Projectiles);
        Assert.Empty(game.MegaProjectiles);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(before, game.Inventory.DirtCount);
        Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(8, 20, 8)));
    }

    [Fact]
    public void ShotDestroysOnlyItsContactedChunkPartAndKeepsTheRemainingFlightMotionAndIndices()
    {
        var game = new GameSession();
        var flight = Chunk();
        flight.VelocityX = 2;
        Restore(game, flight);
        Shoot(game, new Vector3(2, 20, 8), new Vector3(14, 20, 8), 150);

        Invoke(game, "UpdateProjectiles", .05f);

        var saved = Assert.Single(Export(game));
        Assert.Equal(new[] { 1, 2, 3 }, saved.Blocks.Select(block => block.BlockIndex));
        Assert.All(saved.Blocks, block => Assert.Equal(100, block.Hp));
        Assert.Equal(8.1f, saved.X, 4);
        Assert.Equal(2, saved.VelocityX);
        Assert.Equal(-.4f, saved.VelocityY, 4);
        Assert.Equal(12, saved.Damage);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void DestroyingAFlyingBlockBeforeItReachesATowerPreventsThatImpact()
    {
        var game = new GameSession();
        var target = AddTower(game, new Cell(8, 20, 8));
        var flight = Debris(5.5f, 20.5f, 8.5f);
        flight.VelocityX = 40;
        Restore(game, flight);
        Shoot(game, new Vector3(6.5f, 23.5f, 8.5f), new Vector3(6.5f, 17.5f, 8.5f), 150);

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Empty(game.MegaProjectiles);
        Assert.Empty(game.Projectiles);
        Assert.Equal(1000, target.Hp);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void ABlockCannotInterceptBeyondItsEarlierTowerImpact()
    {
        var game = new GameSession();
        var target = AddTower(game, new Cell(7, 20, 8));
        var flight = Debris(5.5f, 20.5f, 8.5f);
        flight.VelocityX = 40;
        Restore(game, flight);
        Shoot(game, new Vector3(8, 23.5f, 8.5f), new Vector3(8, 17.5f, 8.5f), 150, .1f);

        Invoke(game, "UpdateProjectiles", .1f);

        Assert.Single(game.Projectiles);
        Assert.Empty(game.MegaProjectiles);
        Assert.Equal(997, target.Hp);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void PartialParentCheckpointRestoresWithoutRefillingItsMissingMaterial()
    {
        var game = new GameSession();
        Restore(game, Chunk());
        Shoot(game, new Vector3(2, 20, 8), new Vector3(14, 20, 8), 150);
        Invoke(game, "UpdateProjectiles", .05f);
        var saved = Export(game);
        var restored = new GameSession();

        Invoke(restored, "RestoreMegaWeaponFlights", saved);

        Assert.Equal(new[] { 1, 2, 3 }, restored.MegaProjectiles.Select(block => block.BlockIndex));
        Assert.All(restored.MegaProjectiles, block => Assert.Equal(100, block.Hp));
        Assert.Equal(12, Assert.Single(Export(restored)).Damage);
    }

    [Fact]
    public void APartiallyDestroyedStickLosesTheMissingQuarterOfItsConcentratedDamage()
    {
        var game = new GameSession();
        var flight = Chunk();
        flight.Formation = MegaWeaponFormation.Stick;
        flight.Blocks = Enumerable.Range(0, 3).Select(index => new SavedMegaWeaponPart
        {
            BlockIndex = index, Material = Material.Dirt, Hp = 100, OffsetX = index - 1.5f
        }).ToList();
        flight.X = 5;
        flight.VelocityX = 40;
        var target = AddTower(game, new Cell(10, 20, 8));
        Restore(game, flight);

        Invoke(game, "UpdateMegaProjectiles", .15f);

        Assert.Equal(991, target.Hp, 3);
        Assert.Equal(2, game.MegaProjectiles.Count);
        Assert.DoesNotContain(game.MegaProjectiles, block => block.BlockIndex == 2);
        Assert.All(game.MegaProjectiles, block => Assert.True(block.IsDebris));
    }

    [Fact]
    public void SurvivingChunkAndDebrisPartsKeepTheirOriginalQuarterShare()
    {
        var game = new GameSession();
        var flight = Debris(5, 20.5f, 8.5f);
        flight.IsDebris = false;
        flight.Formation = MegaWeaponFormation.Chunk;
        flight.VelocityX = 40;
        var target = AddTower(game, new Cell(10, 20, 8));
        target.Hp = 50;
        Restore(game, flight);

        Invoke(game, "UpdateMegaProjectiles", .15f);

        Assert.Equal(47, target.Hp, 3);
        var remaining = Assert.Single(game.MegaProjectiles);
        Assert.True(remaining.IsDebris);
        Assert.Equal(50, remaining.Hp);
        Assert.Equal(12, Assert.Single(Export(game)).Damage);
    }

    private static SavedMegaWeaponFlight Chunk() => new()
    {
        OwnerId = 100, Formation = MegaWeaponFormation.Chunk, X = 8, Y = 20.5f, Z = 8.5f, Damage = 12,
        Blocks = Enumerable.Range(0, 4).Select(index => new SavedMegaWeaponPart
        {
            BlockIndex = index, Material = Material.Dirt, Hp = 100,
            OffsetY = index / 2 == 0 ? -.5f : .5f, OffsetZ = index % 2 == 0 ? -.5f : .5f
        }).ToList()
    };

    private static SavedMegaWeaponFlight Debris(float x, float y, float z) => new()
    {
        OwnerId = 100, Formation = MegaWeaponFormation.Chunk, IsDebris = true,
        X = x, Y = y, Z = z, Damage = 12,
        Blocks = [new SavedMegaWeaponPart { BlockIndex = 0, Material = Material.Dirt, Hp = 100 }]
    };

    private static Turret AddTower(GameSession game, Cell cell)
    {
        var tower = new Turret { Id = 200, Cell = cell, Kind = PieceKind.Pawn, Hp = 1000 };
        game.Turrets.Add(tower);
        return tower;
    }

    private static void Shoot(GameSession game, Vector3 from, Vector3 to, float damage, float duration = .05f) =>
        game.Projectiles.Add(new Projectile { Origin = from, Target = to, Damage = damage, Duration = duration, Kind = ShotKind.Pellet });

    private static void Restore(GameSession game, params SavedMegaWeaponFlight[] flights) =>
        Invoke(game, "RestoreMegaWeaponFlights", (object)flights);

    private static List<SavedMegaWeaponFlight> Export(GameSession game) =>
        Assert.IsType<List<SavedMegaWeaponFlight>>(Invoke(game, "ExportMegaWeaponFlights"));

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
