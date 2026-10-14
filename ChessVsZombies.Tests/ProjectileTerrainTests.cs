using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ProjectileTerrainTests
{
    [Fact]
    public void PelletKeepsItsLaunchRayAfterTheAimTargetHasGone()
    {
        var game = new GameSession();
        var projectile = Shot(ShotKind.Pellet, new(8.5f, 16.5f, 8.5f), new(9.5f, 16.5f, 8.5f), .1f, 7);
        game.Projectiles.Add(projectile); // Its former TargetId is no longer present.

        game.Update(.2f);

        Assert.Same(projectile, Assert.Single(game.Projectiles));
        Assert.True(projectile.Age > projectile.Duration);
        Assert.Equal(10.5f, projectile.Position.X, 4);
        Assert.Equal(16.5f, projectile.Position.Y);
        Assert.Equal(8.5f, projectile.Position.Z);
        game.Update(3);
        Assert.Empty(game.Projectiles);
    }

    [Theory]
    [InlineData(Material.Dirt)]
    [InlineData(Material.Wood)]
    [InlineData(Material.Stone)]
    [InlineData(Material.Copper)]
    [InlineData(Material.Iron)]
    [InlineData(Material.Gold)]
    [InlineData(Material.Diamond)]
    [InlineData(Material.Osmium)]
    public void MissedPelletsDamageAndDestroyEveryTerrainMaterialWithoutRecoveringBlocks(Material material)
    {
        var cell = new Cell(12, 16, 8);
        var game = WithTerrain(material, cell);
        Assert.True(game.Inventory.Add(material, 2));
        var currency = game.Economy.Currency;
        game.Projectiles.Add(Shot(ShotKind.Pellet, new(8.5f, 16.5f, 8.5f), new(9.5f, 16.5f, 8.5f), .1f, 7));

        game.Update(.4f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(Balance.BlockHp(material) - 7, game.World.GetTerrainHp(cell));
        game.Projectiles.Add(Shot(ShotKind.Pellet, new(8.5f, 16.5f, 8.5f), new(9.5f, 16.5f, 8.5f), .1f, Balance.BlockHp(material)));
        game.Update(.4f);
        Assert.Equal(Material.Air, game.World.GetMaterial(cell));
        Assert.Equal(2, game.Inventory.GetCount(material));
        Assert.Equal(currency, game.Economy.Currency);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void AMissedLobContinuesDescendingAndHitsGroundBeyondItsAimPoint()
    {
        var game = new GameSession();
        var projectile = Shot(ShotKind.Lob, new(8.5f, 16.5f, 8.5f), new(10.5f, 16.5f, 8.5f), .4f, 7);
        Assert.True(projectile.GetPositionAt(.44f).X > projectile.Target.X);
        Assert.True(projectile.GetPositionAt(.44f).Y < projectile.Target.Y);
        game.Projectiles.Add(projectile);

        game.Update(.5f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(93, game.World.GetTerrainHp(new Cell(10, 15, 8)));
        Assert.Equal(100, game.World.GetTerrainHp(new Cell(9, 15, 8)));
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void ADeadTargetsPhysicalBlockCanStopItsStillTravellingBullet()
    {
        var game = new GameSession();
        var target = new Zombie { Id = 99, WaveIndex = -1, Position = new Cell(10, 16, 8).Center };
        game.Zombies.Add(target);
        game.Projectiles.Add(Shot(ShotKind.Pellet, new Cell(8, 16, 8).Center, target.Position, .5f, 7));
        game.DamageZombie(target, 10, DamageKind.Chemical);

        game.Update(.1f);
        Assert.Single(game.Projectiles);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(new Cell(10, 16, 8)));
        game.Update(.5f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(93, game.World.GetTerrainHp(new Cell(10, 16, 8)));
        Assert.Equal(0, game.Inventory.DirtCount);
    }

    [Theory]
    [InlineData(Material.Dirt)]
    [InlineData(Material.Stone)]
    public void BreakingAFortificationSettlesItsTowerWithoutInventoryOrProjectileFriendlyFire(Material material)
    {
        var game = new GameSession();
        var cell = new Cell(12, 16, 8);
        Assert.True(game.Inventory.Add(material));
        Assert.True(game.BuildBarrier(cell, material));
        Assert.True(game.Deploy(PieceKind.Pawn, cell + new Cell(0, 1, 0)));
        var turret = Assert.Single(game.Turrets);
        game.Projectiles.Add(Shot(ShotKind.Pellet, new Cell(8, 16, 8).Center, new Cell(9, 16, 8).Center, .1f, Balance.BlockHp(material)));

        game.Update(.4f);

        Assert.Null(game.World.GetBarrier(cell));
        Assert.Equal(cell, turret.Cell);
        Assert.Equal(Balance.TurretHp, turret.Hp);
        Assert.Equal(0, game.Inventory.GetCount(material));
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void AThinCrustIsHitBetweenSamplesAndProjectileDestructionDoesNotShatter()
    {
        var game = new GameSession();
        var cell = new Cell(9, 16, 8);
        game.Liquids.RestoreState(new LiquidState
        {
            Cells = [new(cell, 0, 0, 0, 1, CrustFace.West)],
            Obsidian = [new(cell, 1, CrustFace.West, 5)]
        });
        var bystander = new Zombie { Id = 1, WaveIndex = -1, Position = new Cell(9, 16, 9).Center };
        game.Zombies.Add(bystander);
        game.Projectiles.Add(Shot(ShotKind.Pellet, new Cell(8, 16, 8).Center, new Cell(10, 16, 8).Center, .1f, 3));

        game.Update(.05f);
        Assert.Equal(2, game.World.GetObsidian(cell)!.Hp);
        Assert.Empty(game.Projectiles);
        game.Projectiles.Add(Shot(ShotKind.Pellet, new Cell(8, 16, 8).Center, new Cell(10, 16, 8).Center, .1f, 3));
        game.Update(.05f);

        Assert.Null(game.World.GetObsidian(cell));
        Assert.Equal(10, bystander.BodyHp);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.DirtCount);
    }

    [Fact]
    public void AProjectilePassesThroughTheEmptyPartOfAnObsidianCell()
    {
        var game = new GameSession();
        var cell = new Cell(9, 16, 8);
        game.Liquids.RestoreState(new LiquidState
        {
            Cells = [new(cell, 0, 0, 0, 2, CrustFace.Bottom)],
            Obsidian = [new(cell, 2, CrustFace.Bottom, 10)]
        });
        game.Projectiles.Add(Shot(ShotKind.Pellet, new Cell(8, 16, 8).Center, new Cell(10, 16, 8).Center, .1f, 3));

        game.Update(.1f);

        Assert.Single(game.Projectiles);
        Assert.Equal(10, game.World.GetObsidian(cell)!.Hp);
    }

    [Fact]
    public void CollisionChoosesTheNearestZombieRatherThanTargetIdOrCollectionOrder()
    {
        var game = new GameSession();
        var farther = new Zombie { Id = 99, WaveIndex = -1, Position = new(10.1f, 16.5f, 8.5f) };
        var nearer = new Zombie { Id = 2, WaveIndex = -1, Position = new(9.7f, 16.5f, 8.5f) };
        game.Zombies.AddRange([farther, nearer]);
        game.Projectiles.Add(Shot(ShotKind.Pellet, new Cell(8, 16, 8).Center, new Cell(12, 16, 8).Center, .1f, 3));

        game.Update(.05f);

        Assert.Equal(7, nearer.BodyHp);
        Assert.Equal(10, farther.BodyHp);
        Assert.Empty(game.Projectiles);
    }

    [Theory]
    [InlineData(11.5f, false)]
    [InlineData(13.5f, true)]
    public void FirstContactOrdersZombieAndTerrainHits(float zombieX, bool terrainFirst)
    {
        var cell = new Cell(12, 16, 8);
        var game = WithTerrain(Material.Wood, cell);
        var zombie = new Zombie { Id = 99, WaveIndex = -1, Position = new(zombieX, 16.5f, 8.5f) };
        game.Zombies.Add(zombie);
        game.Projectiles.Add(Shot(ShotKind.Pellet, new Cell(8, 16, 8).Center, new Cell(15, 16, 8).Center, .05f, 3));

        game.Update(.05f);

        Assert.Equal(terrainFirst ? 10 : 7, zombie.BodyHp);
        Assert.Equal(terrainFirst ? 247 : 250, game.World.GetTerrainHp(cell));
    }

    [Fact]
    public void AMissedShotSurvivesTheLastKillAndDamagesTerrainDuringPreparation()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Count = 1 }] }] });
        var game = new GameSession(levels: levels);
        Assert.True(game.Inventory.Add(Material.Dirt, 2));
        Assert.True(game.BuildBarrier(new Cell(12, 16, 8)));
        Assert.True(game.BuildBarrier(new Cell(12, 17, 8)));
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var last = Assert.Single(game.Zombies);
        last.Position = new Cell(14, 16, 15).Center;
        var projectile = Shot(ShotKind.Pellet, new Cell(8, 17, 8).Center, new Cell(9, 17, 8).Center, .1f, 7);
        game.Projectiles.Add(projectile);
        game.DamageZombie(last, 10, DamageKind.Chemical);

        game.Update(.01f);

        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Same(projectile, Assert.Single(game.Projectiles));
        game.Update(.4f);
        Assert.Equal(93, game.World.GetBarrier(new Cell(12, 17, 8))!.Hp);
        Assert.Empty(game.Projectiles);
    }

    private static Projectile Shot(ShotKind kind, Vector3 origin, Vector3 target, float duration, float damage) =>
        new() { Kind = kind, Origin = origin, Target = target, Duration = duration, Damage = damage, TargetId = 99 };

    private static GameSession WithTerrain(Material material, Cell cell)
    {
        var state = new GameSession().ExportState();
        state.TerrainPlacements!.Add(new SavedTerrainPlacement { Cell = cell, Material = material });
        var path = Path.Combine(Path.GetTempPath(), $"ChessVsZombies-projectiles-{Guid.NewGuid():N}.json");
        try
        {
            var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true, Converters = { new JsonStringEnumConverter() } };
            File.WriteAllText(path, JsonSerializer.Serialize(state, options));
            Assert.True(SaveStore.TryLoad(path, out var game, out var error), error);
            return game!;
        }
        finally { File.Delete(path); }
    }
}
