using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MegaWeaponTests
{
    [Fact]
    public void MegaBodyGearAttackAndBucketsUseTheirSpecifiedScale()
    {
        var normal = new Zombie { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Iron };
        var mega = new Zombie { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Iron, Size = ZombieSize.Mega };
        Assert.Equal(new Vector3(1.5f), mega.BodyHalfExtents);
        Assert.Equal(3, mega.GridFootprint);
        Assert.Equal(normal.MaxBodyHp * 9, mega.MaxBodyHp);
        Assert.Equal(normal.Hp * 9, mega.Hp);
        Assert.Equal(normal.MovementSpeed * .5f, mega.MovementSpeed);
        Assert.Equal(LevelCatalog.ZombieAttack(normal.Kind, normal.Tier) * 9,
            LevelCatalog.ZombieAttack(mega.Kind, mega.Tier, mega.Size));
        Assert.Equal(2, mega.BucketCount);
        Assert.Equal(15, mega.BucketCapacity);
        Assert.True(mega.CarriesBucket);
    }

    [Fact]
    public void MegasAndKilosBuyFromTheSameDiscountedReserveWithoutChangingRealHp()
    {
        var plan = WaveSpawnPlanner.Plan(new WaveDefinition
        {
            HpBudget = 1000, BossBudgetFraction = .05f,
            Groups =
            [
                new() { Kind = ZombieKind.Basic },
                new() { Kind = ZombieKind.Basic, Size = ZombieSize.Kilo, IsBoss = true },
                new() { Kind = ZombieKind.Basic, Size = ZombieSize.Mega, IsBoss = true }
            ]
        });
        Assert.Equal(50, plan.BossBudget);
        Assert.Equal(950, plan.NormalBudget);
        Assert.Equal(2, Assert.Single(plan.Groups, group => group.Size == ZombieSize.Kilo).Count);
        Assert.Equal(2, Assert.Single(plan.Groups, group => group.Size == ZombieSize.Mega).Count);
        Assert.Equal(10, WaveSpawnPlan.BudgetCost(Assert.Single(plan.Groups, group => group.Size == ZombieSize.Kilo)));
        Assert.Equal(15, WaveSpawnPlan.BudgetCost(Assert.Single(plan.Groups, group => group.Size == ZombieSize.Mega)));
        Assert.Equal(1210, plan.TotalHp);
        Assert.Equal(1000, plan.BudgetSpent);
    }

    [Fact]
    public void GeneratedHighTierWavesContainBothBossSizesAndTheirArmor()
    {
        var wave = Assert.Single(new LevelCatalog().Get(20).Waves);
        var plan = WaveSpawnPlanner.Plan(wave);
        Assert.Contains(plan.Groups, group => group.IsBoss && group.Size == ZombieSize.Kilo);
        var mega = Assert.Single(plan.Groups, group => group.IsBoss && group.Size == ZombieSize.Mega);
        Assert.Equal(GearTier.Iron, mega.Tier);
        Assert.Equal(ZombieKind.ArmedArmored, mega.Kind);
        Assert.Equal(9 * (10 + 4 * Balance.GearHp(GearTier.Iron)), LevelCatalog.ZombieMaxHp(mega.Kind, mega.Tier, mega.Size));
    }

    [Fact]
    public void FourBlockWeaponRipsOutAConnectedTerrainChunkAndKeepsItsBackpack()
    {
        var (game, mega) = Create();
        var recovered = new Cell(4, 16, 9);
        Assert.True((bool)Invoke(game.World, "AddBarrier", recovered, Material.Copper, (float?)77)!);
        foreach (var cell in new[] { new Cell(3, 16, 8), new Cell(3, 16, 9), new Cell(4, 16, 8) })
            Assert.True((bool)Invoke(game.World, "AddBarrier", cell, Material.Dirt, null)!);
        int before = TerrainCount(game);

        Acquire(game, mega);

        Assert.Equal(4, game.MegaWeapons.Count);
        Assert.Equal(1, mega.CarriedBlocks);
        Assert.Equal(before - 4, TerrainCount(game));
        Assert.Equal(Material.Air, game.World.GetMaterial(recovered));
        var copper = Assert.Single(game.MegaWeapons, block => block.Material == Material.Copper);
        Assert.Equal(77, copper.Hp);
        Assert.True(copper.WasBarrier);
        Assert.Equal(2, game.MegaWeapons.Count(block => block.HandIndex == 0));
        Assert.Equal(2, game.MegaWeapons.Count(block => block.HandIndex == 1));
        Assert.All(game.MegaWeapons, block =>
        {
            Assert.Equal(Vector3.One, block.Max - block.Min);
            Assert.True(block.Min.X >= mega.Position.X - 1.5f && block.Max.X <= mega.Position.X + 1.5f);
            Assert.True(block.Min.Y >= mega.Position.Y - 1.5f && block.Max.Y <= mega.Position.Y + 1.5f);
            Assert.True(block.Min.Z >= mega.Position.Z - 1.5f && block.Max.Z <= mega.Position.Z + 1.5f);
        });
        Invoke(game, "UpdateMegas", 1f);
        Assert.Equal(before - 4, TerrainCount(game));
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.DirtCount);
    }

    [Fact]
    public void BucketWorkStowsTheWeaponAndPreventsAcquisitionAndThrowing()
    {
        var (game, mega) = Create();
        Acquire(game, mega);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 16, 9)));
        Invoke(game, "BeginKiloBucketAction", mega);

        Invoke(game, "UpdateMegas", 1f);

        Assert.Equal(4, game.MegaWeapons.Count);
        Assert.All(game.MegaWeapons, block => Assert.False(block.IsActive));
        Assert.Empty(game.MegaProjectiles);
        Assert.False((bool)Invoke(game, "TryThrowMegaWeapon", mega)!);
    }

    [Fact]
    public void AStickConcentratesOneHundredFiftyPercentAttackAndRetainsFourRealDebrisBlocks()
    {
        var (game, mega) = Create();
        Acquire(game, mega);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 16, 9)));
        var tower = Assert.Single(game.Turrets);
        float before = tower.Hp;

        Assert.True((bool)Invoke(game, "TryThrowMegaWeapon", mega)!);

        Assert.Empty(game.MegaWeapons);
        Assert.Equal(4, game.MegaProjectiles.Count);
        Assert.All(game.MegaProjectiles, block => Assert.Equal(MegaWeaponFormation.Stick, block.Formation));
        for (int tick = 0; tick < 60 && tower.Hp == before; tick++) Invoke(game, "UpdateMegaProjectiles", .05f);
        Assert.Equal(before - mega.Attack * 1.5f, tower.Hp, 4);
        Assert.Equal(4, game.MegaProjectiles.Count + game.PendingBlockDrops.Count);
        Assert.All(game.MegaProjectiles, block => Assert.True(block.IsDebris));
        for (int tick = 0; tick < 240 && game.MegaProjectiles.Count > 0; tick++) Invoke(game, "UpdateMegaProjectiles", .05f);
        Assert.Empty(game.MegaProjectiles);
        Assert.Equal(4, game.PendingBlockDrops.Count);
        Assert.Equal(before - mega.Attack * 1.5f, tower.Hp, 4);
    }

    [Fact]
    public void AChunkUsesItsQuarterShareAndSpentDurabilityForEachContactBeforeScattering()
    {
        var (game, mega) = Create();
        Acquire(game, mega);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 16, 8)));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 16, 9)));
        float before = game.Turrets[0].Hp;

        Assert.True((bool)Invoke(game, "TryThrowMegaWeapon", mega)!);

        Assert.All(game.MegaProjectiles, block => Assert.Equal(MegaWeaponFormation.Chunk, block.Formation));
        for (int tick = 0; tick < 60 && game.Turrets.All(tower => tower.Hp == before); tick++) Invoke(game, "UpdateMegaProjectiles", .05f);
        float quarterDamage = mega.Attack * 1.5f / 4;
        // Both towers meet the same cube at their shared seam. The first
        // contact spends its pre-hit HP before the next contact's ratio is read.
        Assert.Equal(before - quarterDamage, game.Turrets[0].Hp, 4);
        Assert.Equal(before - quarterDamage * ((Balance.DirtBlockHp - before) / Balance.DirtBlockHp),
            game.Turrets[1].Hp, 4);
        Assert.Equal(4, game.MegaProjectiles.Count + game.PendingBlockDrops.Count);
        Assert.Equal(4 * Balance.DirtBlockHp - 2 * before,
            game.MegaProjectiles.Sum(block => block.Hp) + game.PendingBlockDrops.Sum(block => block.Hp));
        Assert.All(game.MegaProjectiles, block => Assert.True(block.IsDebris));
    }

    [Fact]
    public void FastDebrisSweepsThroughTheActualTowerAndDealsItsOriginalQuarterShare()
    {
        var game = new GameSession();
        var tower = new Turret { Id = 200, Kind = PieceKind.Pawn, Cell = new Cell(10, 20, 8) };
        game.Turrets.Add(tower);
        var flight = Flight(true, 36);
        // A previous primary contact grants no permanent immunity after the
        // actual debris has separated and comes back through this tower.
        flight.DamagedTowerIds.Add(tower.Id);
        Invoke(game, "RestoreMegaWeaponFlights", (object)new[] { flight });

        Invoke(game, "UpdateMegaProjectiles", .25f);

        Assert.Equal(Balance.TurretHp - 9 * 3.11f, tower.Hp, 4);
        Assert.Empty(game.MegaProjectiles);
        var drop = Assert.Single(game.PendingBlockDrops);
        Assert.Equal(Material.Stone, drop.Content);
        Assert.Equal(311 - Balance.TurretHp, drop.Hp);
        Assert.True(drop.WasBarrier);
    }

    [Fact]
    public void InFlightCheckpointRoundTripPreservesMotionDamageAndFiniteMaterial()
    {
        var original = new GameSession();
        Invoke(original, "RestoreMegaWeaponFlights", (object)new[] { Flight(true, 54) });
        Invoke(original, "UpdateMegaProjectiles", .05f);
        var saved = Invoke(original, "ExportMegaWeaponFlights");
        var restored = new GameSession();
        Invoke(restored, "RestoreMegaWeaponFlights", saved);
        Assert.Equal(original.MegaProjectiles, restored.MegaProjectiles);
        Invoke(original, "UpdateMegaProjectiles", .1f);
        Invoke(restored, "UpdateMegaProjectiles", .1f);
        Assert.Equal(original.MegaProjectiles, restored.MegaProjectiles);
        Assert.Equal(original.PendingBlockDrops, restored.PendingBlockDrops);
    }

    [Theory]
    [InlineData("nan")]
    [InlineData("negativeDamage")]
    [InlineData("speed")]
    [InlineData("material")]
    [InlineData("overlapping")]
    [InlineData("duplicate")]
    public void InvalidRestoreIsRejectedBeforeExistingFlightStateIsChanged(string fault)
    {
        var game = new GameSession();
        Invoke(game, "RestoreMegaWeaponFlights", (object)new[] { Flight(true, 54) });
        var before = game.MegaProjectiles.ToArray();
        var invalid = Flight(true, 54);
        switch (fault)
        {
            case "nan": invalid.VelocityX = float.NaN; break;
            case "negativeDamage": invalid.Damage = -1; break;
            case "speed": invalid.VelocityZ = 257; break;
            case "material": invalid.Blocks[0].Material = Material.Core; break;
            case "overlapping":
            case "duplicate":
                invalid.IsDebris = false;
                invalid.Blocks = Enumerable.Range(0, 4).Select(index => new SavedMegaWeaponPart
                {
                    BlockIndex = fault == "duplicate" ? 0 : index, Material = Material.Dirt, Hp = 100,
                    OffsetX = fault == "overlapping" ? 0 : index - 1.5f
                }).ToList();
                break;
        }
        var exception = Assert.Throws<TargetInvocationException>(() => Invoke(game, "RestoreMegaWeaponFlights", (object)new[] { invalid }));
        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Equal(before, game.MegaProjectiles);
    }

    [Fact]
    public void DiagonallyAimedStickStillContainsFourDistinctFullCubes()
    {
        var (game, mega) = Create();
        Acquire(game, mega);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 16, 20)));
        Assert.True((bool)Invoke(game, "TryThrowMegaWeapon", mega)!);
        var blocks = game.MegaProjectiles.ToArray();
        Assert.Equal(4, blocks.Length);
        for (int first = 0; first < blocks.Length; first++)
            for (int second = first + 1; second < blocks.Length; second++)
            {
                var a = blocks[first];
                var b = blocks[second];
                bool overlaps = a.Min.X < b.Max.X && a.Max.X > b.Min.X && a.Min.Y < b.Max.Y &&
                    a.Max.Y > b.Min.Y && a.Min.Z < b.Max.Z && a.Max.Z > b.Min.Z;
                Assert.False(overlaps);
            }
    }

    [Fact]
    public void AnAllyInTheLongStickReshapeSpaceRequiresAClearCompactFormation()
    {
        var (game, mega) = Create();
        Acquire(game, mega);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 16, 9)));
        var ally = new Zombie { Id = 101, Position = mega.Position + new Vector3(2.1f, .25f, 0) };
        game.Zombies.Add(ally);

        Assert.True((bool)Invoke(game, "TryThrowMegaWeapon", mega)!);

        Assert.Empty(game.MegaWeapons);
        Assert.Equal(4, game.MegaProjectiles.Count);
        Assert.All(game.MegaProjectiles, block =>
        {
            Assert.Equal(MegaWeaponFormation.Chunk, block.Formation);
            var low = ally.Position - ally.BodyHalfExtents;
            var high = ally.Position + ally.BodyHalfExtents;
            bool overlaps = block.Min.X < high.X && block.Max.X > low.X && block.Min.Y < high.Y &&
                block.Max.Y > low.Y && block.Min.Z < high.Z && block.Max.Z > low.Z;
            Assert.False(overlaps);
        });
        Assert.Equal(1, mega.CarriedBlocks);
    }

    [Fact]
    public void DyingWithAnUnthrownWeaponDropsEachRealBlockOnce()
    {
        var (game, mega) = Create();
        Acquire(game, mega);
        mega.Hp = 0;
        Invoke(game, "HandleKiloDeath", mega);
        Invoke(game, "HandleKiloDeath", mega);
        Invoke(game, "QueueDeathBlock", mega);
        Assert.Empty(game.MegaWeapons);
        Assert.Equal(5, game.PendingBlockDrops.Count);
    }

    [Fact]
    public void RealSimulationRefillsOneDestroyedSlotWithoutReplacingTheOtherHandDurability()
    {
        var (game, mega) = Create();
        Acquire(game, mega);
        Invoke(game, "DamageKiloCover", mega.Id, 2, 17f);
        Invoke(game, "DamageKiloCover", mega.Id, 0, 1000f);
        int before = TerrainCount(game);
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;

        for (int tick = 0; tick < 240 && game.MegaWeapons.Count < 4; tick++) game.Update(.05f);

        Assert.Equal(4, game.MegaWeapons.Count);
        Assert.Equal(83, Assert.Single(game.MegaWeapons, block => block.BlockIndex == 2).Hp);
        Assert.Equal(before - 1, TerrainCount(game));
        Assert.Equal(1, mega.CarriedBlocks);
        Assert.Equal(2, mega.BucketCount);
        Assert.Equal(0, mega.HeldBucketUnits);
    }

    [Fact]
    public void RealSimulationCanRebuildAllFourDestroyedBlocksWithoutInventingAnotherBackpack()
    {
        var (game, mega) = Create();
        Acquire(game, mega);
        foreach (var block in game.MegaWeapons.ToArray()) Invoke(game, "DamageKiloCover", mega.Id, block.BlockIndex, 1000f);
        int before = TerrainCount(game);
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;

        for (int tick = 0; tick < 320 && game.MegaWeapons.Count < 4; tick++) game.Update(.05f);

        Assert.Equal(4, game.MegaWeapons.Count);
        Assert.Equal(before - 4, TerrainCount(game));
        Assert.Equal(1, mega.CarriedBlocks);
        Assert.Equal(2, mega.BucketCount);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    public void BothTwoBlockHandsFollowTheFrontFaceAndStopRealPelletsBeforeTheBody(int x, int z)
    {
        var (game, mega) = Create();
        Acquire(game, mega);
        var facing = new Vector3(x, 0, z);
        mega.FaceToward(facing);
        Invoke(game, "UpdateMegaHeldPoses");
        float bodyBefore = mega.BodyHp;
        var blocks = game.MegaWeapons.ToArray();
        Assert.Equal(4, blocks.Length);
        foreach (var block in blocks)
        {
            var center = (block.Min + block.Max) * .5f;
            Assert.Equal(1, Vector3.Dot(center - mega.Position, facing), 5);
            Assert.True(block.Min.X >= mega.Position.X - 1.5f && block.Max.X <= mega.Position.X + 1.5f);
            Assert.True(block.Min.Y >= mega.Position.Y - 1.5f && block.Max.Y <= mega.Position.Y + 1.5f);
            Assert.True(block.Min.Z >= mega.Position.Z - 1.5f && block.Max.Z <= mega.Position.Z + 1.5f);
        }
        Assert.Equal(2, blocks.Count(block => block.HandIndex == 0));
        Assert.Equal(2, blocks.Count(block => block.HandIndex == 1));
        var selectedBlock = Assert.Single(blocks, block => block.BlockIndex == 0);
        var aim = (selectedBlock.Min + selectedBlock.Max) * .5f;
        game.Projectiles.Add(new Projectile
        {
            Origin = aim + facing * 4, Target = aim - facing * 2,
            Kind = ShotKind.Pellet, Damage = 7, Duration = .1f
        });

        game.Update(.15f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(bodyBefore, mega.BodyHp);
        Assert.Equal(selectedBlock.Hp - 7, Assert.Single(game.MegaWeapons, block => block.BlockIndex == 0).Hp);
        Assert.All(game.MegaWeapons.Where(block => block.BlockIndex != 0), block =>
            Assert.Equal(Assert.Single(blocks, before => before.BlockIndex == block.BlockIndex).Hp, block.Hp));
    }

    private static SavedMegaWeaponFlight Flight(bool debris, float damage) => new()
    {
        OwnerId = 100, Formation = MegaWeaponFormation.Chunk, IsDebris = debris,
        X = 5, Y = 20.5f, Z = 8.5f, VelocityX = 40, Damage = damage,
        Blocks = [new() { BlockIndex = 0, Material = Material.Stone, Hp = 311, WasBarrier = true }]
    };

    private static (GameSession Game, Zombie Mega) Create()
    {
        var game = new GameSession();
        var mega = new Zombie { Id = 100, Size = ZombieSize.Mega, Attack = 9, WaveIndex = 0 };
        mega.Position = mega.StandAt(new Cell(5, 16, 8));
        mega.FaceToward(Vector3.UnitX);
        game.Zombies.Add(mega);
        return (game, mega);
    }

    private static void Acquire(GameSession game, Zombie mega)
    {
        for (int tick = 0; tick < 8 && game.MegaWeapons.Count < 4; tick++) Invoke(game, "UpdateMegas", .36f);
        Assert.Equal(4, game.MegaWeapons.Count);
        Assert.Equal(1, mega.CarriedBlocks);
    }

    private static int TerrainCount(GameSession game)
    {
        int total = 0;
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 0; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                    if (Balance.IsTerrainMaterial(game.World.GetMaterial(new Cell(x, y, z))) ||
                        game.World.GetMaterial(new Cell(x, y, z)) == Material.Barrier) total++;
        return total;
    }

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
