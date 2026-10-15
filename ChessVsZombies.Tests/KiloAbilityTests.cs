using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloAbilityTests
{
    [Fact]
    public void ALoneKiloUsesOneRealGroundBlockAndItsBackpackBlockWithoutCreatingInventory()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        var source = GroundSource(kilo);
        Invoke(game.World, "DamageTerrain", source, 53f);

        Invoke(game, "TryKiloAction", kilo, .05f);

        var cover = Cover(game, kilo, 0);
        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        Assert.Equal(Material.Dirt, cover.Material);
        Assert.Equal(47, cover.Hp);
        Assert.False(cover.WasBarrier);
        Assert.Equal(1, cover.Max.Y - cover.Min.Y);
        Assert.Equal(16.5f, cover.Min.Y);
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Equal(2, game.KiloCovers.Count);
        var backpack = Cover(game, kilo, 1);
        Assert.Equal(Material.Dirt, backpack.Material);
        Assert.Equal(100, backpack.Hp);
        Assert.Equal(cover.Min.Y, backpack.Min.Y);
        Assert.Equal(cover.Max.Z, backpack.Min.Z);
        Invoke(game, "TryKiloAction", kilo, 5f);
        Assert.Equal(2, game.KiloCovers.Count);
    }

    [Fact]
    public void NearbyAlliesPreventAnIdleKiloFromExcavatingAnotherCoverBlock()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        // Another Kilo cannot be thrown, but still means this carrier is not alone.
        Add(game, ZombieSize.Kilo, new Cell(5, 16, 8));

        Invoke(game, "TryKiloAction", kilo, .05f);

        Assert.Empty(game.KiloCovers);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(GroundSource(kilo)));
    }

    [Fact]
    public void TheHeldCubeHasItsOwnProjectileContactAndDestructionDoesNotDropMaterial()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        Invoke(game, "TryKiloAction", kilo, .05f);
        var cover = Cover(game, kilo, 0);
        var coverCenter = (cover.Min + cover.Max) * .5f;
        object?[] contact = [coverCenter + Vector3.UnitX * 3, coverCenter - Vector3.UnitX, 0f, 0, 0];

        Assert.True((bool)Invoke(game, "TryFindKiloCoverImpact", contact)!);
        Assert.Equal(kilo.Id, (int)contact[3]!);
        Assert.Equal(0, (int)contact[4]!);
        Assert.InRange((float)contact[2]!, .624f, .626f);
        Invoke(game, "DamageKiloCover", kilo.Id, 0, 1f);
        Assert.Equal(cover.Hp - 1, Cover(game, kilo, 0).Hp);
        Assert.Equal(40, kilo.BodyHp);
        Invoke(game, "DamageKiloCover", kilo.Id, 0, 1000f);
        Assert.Equal(100, Assert.Single(game.KiloCovers).Hp);
        Assert.Equal(1, Assert.Single(game.KiloCovers).BlockIndex);
        Invoke(game, "DamageKiloCover", kilo.Id, 1, 1000f);
        Assert.Empty(game.KiloCovers);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.DirtCount);
    }

    [Fact]
    public void AContactWithTheOtherHandSelectsOnlyItsOwnBlock()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        Invoke(game, "TryKiloAction", kilo, .05f);
        var upper = Cover(game, kilo, 1);
        var center = (upper.Min + upper.Max) * .5f;
        object?[] impact = [center + Vector3.UnitX * 3, center - Vector3.UnitX, 0f, 0, 0];

        Assert.True((bool)Invoke(game, "TryFindKiloCoverImpact", impact)!);
        Assert.Equal(1, (int)impact[4]!);
        Invoke(game, "DamageKiloCover", kilo.Id, (int)impact[4]!, 30f);

        Assert.Equal(70, Cover(game, kilo, 1).Hp);
        Assert.Equal(100, Cover(game, kilo, 0).Hp);
    }

    [Fact]
    public void ADeadCarrierReleasesItsExcavatedCoverWithRemainingHpAndProvenanceExactlyOnce()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Invoke(game, "DamageKiloCover", kilo.Id, 0, 13f);

        Invoke(game, "HandleKiloDeath", kilo);
        Invoke(game, "HandleKiloDeath", kilo);

        Assert.Empty(game.KiloCovers);
        Assert.Equal(2, game.PendingBlockDrops.Count);
        var drop = Assert.Single(game.PendingBlockDrops, block => block.Hp == 87);
        Assert.Equal(Material.Dirt, drop.Content);
        Assert.False(drop.WasBarrier);
        Assert.Equal(100, Assert.Single(game.PendingBlockDrops, block => block.Hp == 100).Hp);
        kilo.Hp = 0;
        Invoke(game, "QueueDeathBlock", kilo);
        Assert.Equal(2, game.PendingBlockDrops.Count);
    }

    [Theory]
    [InlineData(ZombieSize.Standard)]
    [InlineData(ZombieSize.Milli)]
    public void AKiloHurlsAnAllyOverATowerUsingAContinuousArc(ZombieSize size)
    {
        var (game, kilo, ally) = HurlScenario(size);
        var start = ally.Position;
        Assert.True((bool)Invoke(game, "CanKiloHurlForProgress", kilo)!);
        Assert.Equal(start, ally.Position);
        Assert.False(game.IsZombieHurled(ally));

        Invoke(game, "TryKiloAction", kilo, .05f);

        Assert.True(game.IsZombieHurled(ally));
        Assert.Equal(start, ally.Position);
        float highest = start.Y;
        for (int sample = 0; sample < 30 && game.IsZombieHurled(ally); sample++)
        {
            var previous = ally.Position;
            Assert.True((bool)Invoke(game, "TryUpdateKiloFlight", ally, .05f)!);
            highest = Math.Max(highest, ally.Position.Y);
            Assert.InRange(Vector3.Distance(previous, ally.Position), 0, .55f);
            Assert.False(game.World.IsSolidAt(ally.Position));
        }
        Assert.False(game.IsZombieHurled(ally));
        Assert.InRange(ally.Position.X, start.X + 3.99f, start.X + 4.01f);
        Assert.True(highest > start.Y + 2.4f);
        Assert.Equal(start.Y, ally.Position.Y, 3);
        Assert.Equal(ally.MaxBodyHp, ally.BodyHp);
        Assert.Equal(Balance.TurretHp, Assert.Single(game.Turrets).Hp);
    }

    [Fact]
    public void AKiloWithHandsBusyOnBucketWorkCannotAdvertiseOrPerformAnAllyThrow()
    {
        var (game, kilo, ally) = HurlScenario(ZombieSize.Standard);
        typeof(Zombie).GetProperty(nameof(Zombie.KiloEquipmentMode))!.SetValue(kilo, KiloEquipmentMode.Buckets);
        typeof(Zombie).GetProperty(nameof(Zombie.KiloBucketActionRemaining))!.SetValue(kilo, Balance.BucketActionSeconds);

        Assert.False((bool)Invoke(game, "CanKiloHurlForProgress", kilo)!);
        Invoke(game, "TryKiloAction", kilo, .05f);

        Assert.False(game.IsZombieHurled(ally));
        Assert.Empty(game.KiloCovers);
    }

    [Fact]
    public void AChangedObstacleInterruptsTheFlightAtItsLastClearPosition()
    {
        var (game, kilo, ally) = HurlScenario(ZombieSize.Standard);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.True(game.IsZombieHurled(ally));
        Invoke(game.World, "AddBarrier", new Cell(7, 18, 8), Material.Stone, (float?)500f);

        for (int sample = 0; sample < 24 && game.IsZombieHurled(ally); sample++)
            Invoke(game, "TryUpdateKiloFlight", ally, .05f);

        Assert.False(game.IsZombieHurled(ally));
        Assert.True(ally.Position.X < 6.801f);
        Assert.False(game.World.IsSolidAt(ally.Position));
        Assert.True(ally.Position.Y > 16.5f);
    }

    [Fact]
    public void AClosedLowCeilingPreventsTheKiloFromLaunchingAnImpossibleArc()
    {
        var (game, kilo, ally) = HurlScenario(ZombieSize.Standard);
        for (int x = 4; x <= 12; x++)
            for (int z = 7; z <= 9; z++) Invoke(game.World, "AddBarrier", new Cell(x, 18, z), Material.Stone, (float?)500f);
        Assert.False((bool)Invoke(game, "CanKiloHurlForProgress", kilo)!);

        Invoke(game, "TryKiloAction", kilo, .05f);

        Assert.False(game.IsZombieHurled(ally));
        Assert.Equal(ally.StandAt(new Cell(5, 16, 8)), ally.Position);
    }

    [Fact]
    public void AKiloCannotGrabAnAllyThroughASolidWall()
    {
        var (game, kilo, ally) = HurlScenario(ZombieSize.Standard);
        kilo.Position = kilo.StandAt(new Cell(2, 16, 7));
        Invoke(game.World, "AddBarrier", new Cell(4, 16, 8), Material.Stone, (float?)500f);
        Assert.False((bool)Invoke(game, "CanKiloHurlForProgress", kilo)!);

        Invoke(game, "TryKiloAction", kilo, .05f);

        Assert.False(game.IsZombieHurled(ally));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(22.5f)]
    [InlineData(45f)]
    [InlineData(67.5f)]
    [InlineData(90f)]
    [InlineData(135f)]
    [InlineData(180f)]
    [InlineData(225f)]
    [InlineData(270f)]
    [InlineData(315f)]
    public void BothFullCoverBlocksRemainInsideTheTwoByTwoByTwoBodyAtEveryFacing(float degrees)
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        float angle = degrees * MathF.PI / 180;
        kilo.FaceToward(new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)));
        var source = GroundSource(kilo);
        var footprint = kilo.Position - kilo.BodyHalfExtents;
        var footprintMax = kilo.Position + kilo.BodyHalfExtents;
        Assert.True(source.X + 1 <= footprint.X || source.X >= footprintMax.X ||
            source.Z + 1 <= footprint.Z || source.Z >= footprintMax.Z);

        Invoke(game, "TryKiloAction", kilo, .05f);

        Assert.Equal(2, game.KiloCovers.Count);
        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        Assert.True((bool)Invoke(game, "HasSolidFooting", kilo.Position, kilo)!);
        AssertCoversContained(game, kilo);
        foreach (var cover in game.KiloCovers)
            Assert.Equal(Vector3.One, cover.Max - cover.Min);
    }

    [Fact]
    public void HeldCoverTurnsImmediatelyAndTraversesAnExactlyTwoWideTwoHighPassage()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitZ);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.Equal(2, game.KiloCovers.Count);
        var origin = kilo.Position;
        // Walls and ceiling touch the body envelope; its two held blocks must
        // never introduce an additional width or height requirement.
        AddCorridor(game, 16, 18, ceiling: true);
        for (int turn = 0; turn < 16; turn++)
        {
            float angle = turn * MathF.PI / 8;
            kilo.FaceToward(new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)));
            AssertCoversContained(game, kilo); // No simulation tick is needed to turn the cubes.
            Invoke(game, "UpdateKilos", .01f);
            AssertCoversContained(game, kilo);
        }
        for (int z = 6; z <= 8; z++)
        {
            object?[] movement = [kilo, kilo.StandAt(new Cell(5, 16, z)), 1f];
            Assert.True((bool)Invoke(game, "MoveToward", movement)!);
            AssertCoversContained(game, kilo);
        }
        Assert.Equal(origin + Vector3.UnitZ * 3, kilo.Position);
        Assert.Equal(2, game.KiloCovers.Count);
        Assert.Equal(40, kilo.BodyHp);
    }

    [Fact]
    public void ContainedCoverKeepsOrdinaryBodyCollisionAgainstAWall()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5), attack: 1);
        kilo.FaceToward(Vector3.UnitZ);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Invoke(game.World, "AddBarrier", new Cell(6, 16, 7), Material.Stone, (float?)500f);
        kilo.Cooldown = 0;
        var origin = kilo.Position;
        object?[] movement = [kilo, kilo.StandAt(new Cell(5, 16, 6)), 1f];

        Assert.False((bool)Invoke(game, "MoveToward", movement)!);

        Assert.Equal(origin, kilo.Position);
        Assert.Equal(499, game.World.GetBarrier(new Cell(6, 16, 7))!.Hp);
        Assert.Equal(2, game.KiloCovers.Count);
        AssertCoversContained(game, kilo);
    }

    [Fact]
    public void ContainedCoversDoNotRejectABodyClearJumpInATwoWideCorridor()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        Invoke(game, "TryKiloAction", kilo, .05f);
        AddCorridor(game, 16, 21, ceiling: false);
        var from = new Cell(5, 16, 5);
        var to = new Cell(5, 16, 7);
        Assert.True((bool)Invoke(game, "IsJumpArcClear", from, to, 1f, 0f, 1f, kilo)!);
        kilo.NextCell = to;
        Field<Dictionary<int, Cell>>(game, "_stepOrigins")[kilo.Id] = from;
        Field<Dictionary<int, float>>(game, "_jumpHeights")[kilo.Id] = 1;
        var origin = kilo.Position;

        Invoke(game, "UpdateZombie", kilo, .5f);

        Assert.True(kilo.Position.Z > origin.Z);
        Assert.True(kilo.Position.Y > origin.Y);
        Assert.Equal(to, kilo.NextCell);
        AssertCoversContained(game, kilo);
    }

    [Fact]
    public void ContainedCoversCanFallInAnExactlyTwoWideShaft()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        Invoke(game, "TryKiloAction", kilo, .05f);
        AddCorridor(game, 16, 22, ceiling: false);
        kilo.Position += Vector3.UnitY * 3;
        var origin = kilo.Position;

        Invoke(game, "UpdateZombie", kilo, 1.1f);

        Assert.True(kilo.Position.Y < origin.Y);
        Assert.True(kilo.Position.Y >= 17);
        Assert.Equal(2, game.KiloCovers.Count);
        AssertCoversContained(game, kilo);
        Assert.Equal(40, kilo.BodyHp);
    }

    [Fact]
    public void EachCoverRetainsItsOwnMaterialDurabilityAndBarrierProvenance()
    {
        var game = new GameSession();
        var kilo = new Zombie { Id = 100, Size = ZombieSize.Kilo, Kind = ZombieKind.ArmedArmored, Tier = GearTier.Copper };
        kilo.Position = kilo.StandAt(new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        game.Zombies.Add(kilo);
        var source = GroundSource(kilo);
        Invoke(game.World, "Dig", source);
        Invoke(game.World, "AddBarrier", source, Material.Stone, (float?)123f);

        Invoke(game, "TryKiloAction", kilo, .05f);

        var ground = Cover(game, kilo, 0);
        var backpack = Cover(game, kilo, 1);
        Assert.Equal(Material.Stone, ground.Material);
        Assert.Equal(123, ground.Hp);
        Assert.True(ground.WasBarrier);
        Assert.Equal(Material.Copper, backpack.Material);
        Assert.Equal(1000, backpack.Hp);
        Assert.False(backpack.WasBarrier);
        Invoke(game, "DamageKiloCover", kilo.Id, 1, 20f);
        Assert.Equal(123, Cover(game, kilo, 0).Hp);
        Assert.Equal(980, Cover(game, kilo, 1).Hp);
        Invoke(game, "HandleKiloDeath", kilo);
        var drops = game.PendingBlockDrops;
        Assert.Equal(2, drops.Count);
        Assert.Contains(drops, block => block.Content == Material.Stone && block.Hp == 123 && block.WasBarrier);
        Assert.Contains(drops, block => block.Content == Material.Copper && block.Hp == 980 && !block.WasBarrier);
    }

    [Fact]
    public void UsedUpBackpackMaterialCannotBeInventedAsASecondCoverBlock()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(kilo, 0);

        Invoke(game, "TryKiloAction", kilo, .05f);

        Assert.Equal(0, Assert.Single(game.KiloCovers).BlockIndex);
    }

    [Fact]
    public void HeldCoverCanBeSpentOnConstructionWithoutDuplicatingTheBackpack()
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.Equal(2, (int)Invoke(game, "GetKiloConstructionBlocks", kilo)!);
        Assert.True((bool)Invoke(game, "HasKiloBackpackCover", kilo)!);
        object?[] arguments = [kilo, null];
        Assert.True((bool)Invoke(game, "TryPeekKiloConstructionBlock", arguments)!);
        var block = Assert.IsType<BlockDrop>(arguments[1]);
        Assert.Equal(Material.Dirt, block.Content);
        Assert.Equal(100, block.Hp);

        Invoke(game, "ConsumeKiloConstructionBlock", kilo);
        Assert.Equal(1, (int)Invoke(game, "GetKiloConstructionBlocks", kilo)!);
        Invoke(game, "ConsumeKiloConstructionBlock", kilo);
        Assert.Empty(game.KiloCovers);
        Assert.False((bool)Invoke(game, "HasKiloBackpackCover", kilo)!);
        Invoke(game, "HandleKiloDeath", kilo);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, kilo.CarriedBlocks);
    }

    [Fact]
    public void ASpentBackpackKiloAdvancesWithBothCoversAndDamagesTheCoreWithoutEnteringIt()
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Size = ZombieSize.Kilo }] }] });
        var game = new GameSession(12345, catalog);
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var kilo = new Zombie { Id = 100, WaveIndex = 0, Size = ZombieSize.Kilo, Attack = 1 };
        kilo.Position = kilo.StandAt(new Cell(10, 16, 14));
        kilo.FaceToward(Vector3.UnitX);
        game.Zombies.Add(kilo);
        Invoke(game, "TryKiloAction", kilo, .02f);
        Assert.Equal(2, game.KiloCovers.Count);
        Assert.Equal(0, kilo.CarriedBlocks);
        var origin = kilo.Position;

        for (int step = 0; step < 800 && game.CoreHp == Balance.CoreHp; step++)
        {
            game.Update(.02f);
            Assert.Equal(2, game.KiloCovers.Count);
            Assert.Equal(0, kilo.CarriedBlocks);
            Assert.Equal(17, kilo.Position.Y, precision: 4);
            Assert.False(game.IsZombieRetreating(kilo));
            foreach (var cover in game.KiloCovers)
                Assert.True(cover.Max.X <= 15.00001f || cover.Min.X >= 16.99999f ||
                    cover.Max.Y <= 15.00001f || cover.Min.Y >= 16.99999f ||
                    cover.Max.Z <= 15.00001f || cover.Min.Z >= 16.99999f,
                    $"Held block entered the objective: {cover.Min} to {cover.Max}.");
        }

        Assert.True(kilo.Position.X > origin.X + 1.9f, $"Kilo did not approach the core: {kilo.Position}.");
        Assert.Equal(Balance.CoreHp - kilo.Attack, game.CoreHp);
        var afterHit = game.CoreHp;
        game.Update(.1f);
        Assert.Equal(afterHit, game.CoreHp); // Contact still uses the ordinary one-second attack cooldown.
        Assert.Equal(kilo.MaxBodyHp, kilo.BodyHp);
        Assert.Equal(2, game.KiloCovers.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASpentBackpackKiloActuallyBuildsABridgeFromHeldCoverWithoutLosingItsHpOrProvenance(bool wasBarrier)
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        var source = GroundSource(kilo);
        Assert.True((bool)Invoke(game.World, "Dig", source)!);
        if (wasBarrier) Assert.True((bool)Invoke(game.World, "AddBarrier", source, Material.Stone, (float?)123f)!);
        else
        {
            Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", source, Material.Stone)!);
            Invoke(game.World, "DamageTerrain", source, 377f);
        }
        Invoke(game, "TryKiloAction", kilo, .02f);
        Assert.Equal(2, game.KiloCovers.Count);
        Assert.Equal(0, kilo.CarriedBlocks);
        Invoke(game, "DamageKiloCover", kilo.Id, 0, 13f);
        var from = new Cell(5, 16, 5);
        var next = new Cell(6, 16, 5);
        // Remove the whole destination footprint while retaining the bank
        // beneath the carrier's rear half and the new floor's side support.
        for (int x = 6; x <= 7; x++)
            for (int z = 5; z <= 6; z++)
                if (game.World.GetMaterial(new Cell(x, 15, z)) != Material.Air)
                    Assert.True((bool)Invoke(game.World, "Dig", new Cell(x, 15, z))!);
        Assert.False((bool)Invoke(game, "HasZombieSupport", next, kilo)!);

        Assert.True((bool)Invoke(game, "TryBuildBridge", kilo, from, next)!);

        var floor = new Cell(6, 15, 5);
        Assert.Equal(wasBarrier ? Material.Barrier : Material.Stone, game.World.GetMaterial(floor));
        if (wasBarrier)
        {
            var barrier = Assert.IsType<ChessVsZombies.Core.Barrier>(game.World.GetBarrier(floor));
            Assert.Equal(110, barrier.Hp);
            Assert.Equal(Material.Air, game.World.GetNaturalMaterial(floor));
        }
        else
        {
            Assert.Null(game.World.GetBarrier(floor));
            Assert.Equal(Material.Stone, game.World.GetNaturalMaterial(floor));
            Assert.Equal(110, game.World.GetTerrainHp(floor));
        }
        var remaining = Assert.Single(game.KiloCovers);
        Assert.Equal(1, remaining.BlockIndex);
        Assert.Equal(Material.Dirt, remaining.Material);
        Assert.Equal(100, remaining.Hp);
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Equal(0, game.Inventory.StoneCount);
    }

    private static void AssertCoversContained(GameSession game, Zombie kilo)
    {
        var min = kilo.Position - Vector3.One;
        var max = kilo.Position + Vector3.One;
        foreach (var cover in game.KiloCovers.Where(cover => cover.CarrierId == kilo.Id))
        {
            Assert.True(cover.Min.X >= min.X - .00001f && cover.Max.X <= max.X + .00001f);
            Assert.True(cover.Min.Y >= min.Y - .00001f && cover.Max.Y <= max.Y + .00001f);
            Assert.True(cover.Min.Z >= min.Z - .00001f && cover.Max.Z <= max.Z + .00001f);
        }
    }

    private static void AddCorridor(GameSession game, int bottomY, int topY, bool ceiling)
    {
        for (int z = 3; z <= 11; z++)
        {
            for (int y = bottomY; y < topY; y++)
                foreach (int x in new[] { 4, 7 })
                    Assert.True((bool)Invoke(game.World, "AddBarrier", new Cell(x, y, z), Material.Stone, (float?)500f)!);
            if (ceiling)
                for (int x = 5; x <= 6; x++)
                    Assert.True((bool)Invoke(game.World, "AddBarrier", new Cell(x, topY, z), Material.Stone, (float?)500f)!);
        }
    }

    private static KiloCover Cover(GameSession game, Zombie kilo, int index) =>
        Assert.Single(game.KiloCovers, cover => cover.CarrierId == kilo.Id && cover.BlockIndex == index);

    private static Cell GroundSource(Zombie kilo)
    {
        var projection = Math.Max(Math.Abs(kilo.Facing.X), Math.Abs(kilo.Facing.Z));
        var front = kilo.Position + kilo.Facing / projection * (Math.Max(kilo.BodyHalfExtents.X, kilo.BodyHalfExtents.Z) + .5f);
        return new Cell((int)MathF.Floor(front.X), (int)MathF.Floor(kilo.Position.Y - kilo.StandingHeight - .001f), (int)MathF.Floor(front.Z));
    }

    private static Cell CellAt(Vector3 position) => new((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), (int)MathF.Floor(position.Z));

    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static (GameSession Game, Zombie Kilo, Zombie Ally) HurlScenario(ZombieSize size)
    {
        var game = new GameSession();
        var kilo = Add(game, ZombieSize.Kilo, new Cell(3, 16, 7));
        var ally = Add(game, size, new Cell(5, 16, 8));
        game.Turrets.Add(new Turret { Id = 50, Cell = new Cell(7, 16, 8), Kind = PieceKind.Pawn });
        // Collision uses the live index, as it does during ordinary combat.
        Invoke(game, "SettleDefenses");
        return (game, kilo, ally);
    }

    private static Zombie Add(GameSession game, ZombieSize size, Cell cell, int attack = 0)
    {
        var zombie = new Zombie { Id = 100 + game.Zombies.Count, WaveIndex = -1, Size = size, Attack = attack };
        zombie.Position = zombie.StandAt(cell);
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static object? Invoke(object owner, string method, params object?[] arguments) =>
        owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
}
