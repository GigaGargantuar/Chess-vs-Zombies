using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloLoadoutTests
{
    [Fact]
    public void TwoBucketsDoNotReplaceTheBackpackBlockOrTheRealExcavatedCover()
    {
        var game = new GameSession();
        var kilo = AddKilo(game);
        Assert.True(kilo.CarriesBucket);
        Assert.Equal(2, kilo.BucketCount);
        Assert.Equal(1, kilo.CarriedBlocks);
        var source = new Cell(6, 15, 7);
        Assert.True((bool)Invoke(game.World, "Dig", source)!);
        Assert.True((bool)Invoke(game.World, "AddBarrier", source, Material.Stone, (float?)123f)!);

        Invoke(game, "TryKiloAction", kilo, .02f);

        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.True(kilo.CarriesBucket);
        Assert.Equal(2, kilo.BucketCount);
        Assert.Equal(0, kilo.HeldBucketUnits);
        Assert.Equal(2, game.KiloCovers.Count);
        var excavated = Assert.Single(game.KiloCovers, cover => cover.BlockIndex == 0);
        Assert.Equal(Material.Stone, excavated.Material);
        Assert.Equal(123, excavated.Hp);
        Assert.True(excavated.WasBarrier);
        var backpack = Assert.Single(game.KiloCovers, cover => cover.BlockIndex == 1);
        Assert.Equal(Material.Dirt, backpack.Material);
        Assert.Equal(100, backpack.Hp);
        Assert.All(game.KiloCovers, cover => Assert.True(cover.IsActive));
        Assert.False(game.IsKiloUsingBuckets(kilo));
    }

    [Fact]
    public void ScoopingSwitchesBothHandsAwayFromCoverWithoutConsumingTheirBlocks()
    {
        var (game, kilo) = EquipAtShore();
        var covers = BlockInventory(game);
        AddSources(game);
        Assert.Equal(KiloEquipmentMode.Covers, kilo.KiloEquipmentMode);
        Assert.Equal(20, WorldUnits(game));

        Assert.True(BucketAction(game, kilo));

        Assert.True(game.IsKiloUsingBuckets(kilo));
        Assert.Equal(KiloEquipmentMode.Buckets, kilo.KiloEquipmentMode);
        Assert.Equal(0, WorldUnits(game));
        Assert.Collection(kilo.BucketLoads,
            load => { Assert.Equal(10, load.Units); Assert.Equal(LiquidKind.Water, load.Kind); },
            load => { Assert.Equal(10, load.Units); Assert.Equal(LiquidKind.Mercury, load.Kind); });
        Assert.Equal(covers, BlockInventory(game));
        Assert.All(game.KiloCovers, cover => Assert.False(cover.IsActive));
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void AResuppliedKiloRaisesItsTwoBackpackBlocksWithoutExcavatingOrLosingBucketPayloads()
    {
        var game = new GameSession();
        var kilo = AddKilo(game);
        typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(kilo, 2);
        typeof(ZombieBucketLoad).GetProperty(nameof(ZombieBucketLoad.Units))!.SetValue(kilo.BucketLoads[0], 7);
        typeof(ZombieBucketLoad).GetProperty(nameof(ZombieBucketLoad.Kind))!.SetValue(kilo.BucketLoads[0], LiquidKind.Water);
        typeof(ZombieBucketLoad).GetProperty(nameof(ZombieBucketLoad.Units))!.SetValue(kilo.BucketLoads[1], 10);
        typeof(ZombieBucketLoad).GetProperty(nameof(ZombieBucketLoad.Kind))!.SetValue(kilo.BucketLoads[1], LiquidKind.Mercury);
        int worldRevision = game.World.Revision;
        Assert.Empty(game.KiloCovers);

        Invoke(game, "TryKiloAction", kilo, .02f);

        Assert.Equal(worldRevision, game.World.Revision);
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Equal(2, game.KiloCovers.Count);
        Assert.Equal(new[] { 0, 1 }, game.KiloCovers.Select(cover => cover.BlockIndex).Order());
        Assert.All(game.KiloCovers, cover =>
        {
            Assert.Equal(Material.Dirt, cover.Material);
            Assert.Equal(100, cover.Hp);
            Assert.False(cover.WasBarrier);
            Assert.True(cover.IsActive);
        });
        Assert.True(kilo.CarriesBucket);
        Assert.Equal(2, kilo.BucketCount);
        Assert.Collection(kilo.BucketLoads,
            load => { Assert.Equal(7, load.Units); Assert.Equal(LiquidKind.Water, load.Kind); },
            load => { Assert.Equal(10, load.Units); Assert.Equal(LiquidKind.Mercury, load.Kind); });
        Assert.False(game.IsKiloUsingBuckets(kilo));
        Assert.Empty(game.PendingBlockDrops);
        Assert.Empty(game.Buckets.Flights);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARealPelletHitsCoverOnlyWhenTheCoverIsInUse(bool scooping)
    {
        var (game, kilo) = EquipAtShore();
        if (scooping)
        {
            AddSources(game);
            Assert.True(BucketAction(game, kilo));
        }
        float hp = kilo.BodyHp;
        var blocks = BlockInventory(game);
        float coverHp = game.KiloCovers.Sum(block => block.Hp);

        FireAtFirstHand(game, kilo, 3);

        Assert.Empty(game.Projectiles);
        if (scooping)
        {
            Assert.Equal(hp - 3, kilo.BodyHp);
            Assert.Equal(blocks, BlockInventory(game));
            Assert.Equal(20, kilo.HeldBucketUnits);
            Assert.True(game.IsKiloUsingBuckets(kilo));
        }
        else
        {
            Assert.Equal(hp, kilo.BodyHp);
            Assert.Equal(coverHp - 3, game.KiloCovers.Sum(block => block.Hp));
            Assert.Equal(0, kilo.HeldBucketUnits);
            Assert.False(game.IsKiloUsingBuckets(kilo));
        }
    }

    [Fact]
    public void IncomingFireCannotCancelTheScoopAndThrowWorkWindows()
    {
        var (game, kilo) = EquipAtShore();
        AddSources(game);
        Assert.True(BucketAction(game, kilo));
        var blocks = BlockInventory(game);

        Invoke(game, "UpdateZombie", kilo, .45f);
        FireAtFirstHand(game, kilo, 1);
        Invoke(game, "UpdateZombie", kilo, .45f);

        Assert.True(game.IsKiloUsingBuckets(kilo));
        Assert.Equal(20, kilo.HeldBucketUnits);
        Assert.Empty(game.Buckets.Flights);
        Assert.Equal(blocks, BlockInventory(game));
        Invoke(game, "UpdateZombie", kilo, .11f);
        Assert.Equal(0, kilo.HeldBucketUnits);
        Assert.Equal(2, game.Buckets.Flights.Count);
        Assert.Equal(20, game.Buckets.Flights.Sum(flight => flight.Units));
        Assert.True(game.IsKiloUsingBuckets(kilo));
        Invoke(game, "UpdateZombie", kilo, .45f);
        FireAtFirstHand(game, kilo, 1);
        Invoke(game, "UpdateZombie", kilo, .45f);
        Assert.True(game.IsKiloUsingBuckets(kilo));
        Assert.Equal(blocks, BlockInventory(game));

        Invoke(game, "UpdateZombie", kilo, .11f);

        Assert.False(game.IsKiloUsingBuckets(kilo));
        Assert.Equal(KiloEquipmentMode.Covers, kilo.KiloEquipmentMode);
        Assert.All(game.KiloCovers, cover => Assert.True(cover.IsActive));
        Assert.Equal(20, game.Buckets.Flights.Sum(flight => flight.Units));
        Assert.Equal(blocks, BlockInventory(game));
        float hp = kilo.BodyHp;
        float coverHp = game.KiloCovers.Sum(block => block.Hp);
        FireAtFirstHand(game, kilo, 1);
        Assert.Equal(hp, kilo.BodyHp);
        Assert.Equal(coverHp - 1, game.KiloCovers.Sum(block => block.Hp));
    }

    [Fact]
    public void RepeatedCallsDuringScoopingCannotFillOrLaunchAnExtraBucket()
    {
        var (game, kilo) = EquipAtShore();
        AddSources(game);
        Assert.True(BucketAction(game, kilo));
        Assert.True(game.Liquids.TryAdd(new Cell(12, 16, 12), LiquidKind.Water, 10));
        // An attack or another action may change the shared cooldown. It must
        // not bypass the physical bucket work already occupying both hands.
        kilo.Cooldown = 0;

        for (int index = 0; index < 8; index++) Assert.True(BucketAction(game, kilo));

        Assert.Equal(20, kilo.HeldBucketUnits);
        Assert.Equal(10, WorldUnits(game));
        Assert.Empty(game.Buckets.Flights);
        Assert.Equal(2, game.KiloCovers.Count);
        Assert.All(game.KiloCovers, cover => Assert.False(cover.IsActive));
    }

    [Fact]
    public void RemovingSupportDuringAScoopAllowsTheRealBodyToFallWhileBothHandsRemainBusy()
    {
        var (game, kilo) = EquipAtShore();
        AddSources(game);
        Assert.True(BucketAction(game, kilo));
        var blocks = BlockInventory(game);
        var origin = kilo.Position;
        for (int x = 12; x <= 13; x++)
            for (int z = 10; z <= 11; z++)
                Assert.True((bool)Invoke(game.World, "Dig", new Cell(x, 15, z))!);

        Invoke(game, "UpdateZombie", kilo, .4f);

        Assert.True(kilo.Position.Y < origin.Y - .001f,
            "The bucket work window suspended a body after all four supporting floor blocks were removed.");
        Assert.True(kilo.Position.Y > origin.Y - 1,
            "The worker teleported to the lower floor instead of following the fall animation.");
        Assert.InRange(Math.Abs(origin.X - kilo.Position.X), 0, .00001f);
        Assert.InRange(Math.Abs(origin.Z - kilo.Position.Z), 0, .00001f);
        Assert.True(game.IsKiloUsingBuckets(kilo));
        Assert.All(game.KiloCovers, cover => Assert.False(cover.IsActive));
        Assert.Equal(20, kilo.HeldBucketUnits);
        Assert.Equal(blocks, BlockInventory(game));
        float hp = kilo.BodyHp;

        FireAtFirstHand(game, kilo, 1);

        Assert.Equal(hp - 1, kilo.BodyHp);
        Assert.Equal(blocks, BlockInventory(game));
        Assert.True(game.IsKiloUsingBuckets(kilo));
    }

    [Fact]
    public void DeathWhileScoopingDropsBothStowedBlocksAndSpillsEachBucketExactlyOnce()
    {
        var (game, kilo) = EquipAtShore();
        Invoke(game, "DamageKiloCover", kilo.Id, 0, 13f);
        var blocks = BlockInventory(game);
        AddSources(game);
        Assert.True(BucketAction(game, kilo));

        game.DamageZombie(kilo, 10_000, DamageKind.Chemical);
        game.DamageZombie(kilo, 10_000, DamageKind.Chemical);

        Assert.Equal(0, kilo.HeldBucketUnits);
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Empty(game.KiloCovers);
        Assert.Equal(2, game.PendingBlockDrops.Count);
        Assert.Contains(game.PendingBlockDrops, block => block.Content == blocks[0].Material && block.Hp == blocks[0].Hp);
        Assert.Contains(game.PendingBlockDrops, block => block.Content == blocks[1].Material && block.Hp == blocks[1].Hp);
        Assert.Equal(20, game.Buckets.PendingUnits);
        Assert.Equal(2, game.Buckets.PendingSplashes.Count);
        Assert.Equal(20, WorldUnits(game) + game.Buckets.PendingUnits + game.Buckets.Flights.Sum(flight => flight.Units));
    }

    [Theory]
    [InlineData(ZombieKind.Basic, GearTier.Wood, Material.Dirt)]
    [InlineData(ZombieKind.ArmedArmored, GearTier.Iron, Material.Iron)]
    public void DeathBeforeRaisingResuppliedCoversDropsBothBackpackBlocksExactlyOnce(
        ZombieKind kind, GearTier tier, Material material)
    {
        var game = new GameSession();
        var kilo = new Zombie { Id = -1, WaveIndex = -1, Size = ZombieSize.Kilo, Kind = kind, Tier = tier };
        kilo.Position = kilo.StandAt(new Cell(5, 16, 5));
        game.Zombies.Add(kilo);
        typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(kilo, 2);
        Assert.True(kilo.CarriesBucket);
        Assert.Empty(game.KiloCovers);

        game.DamageZombie(kilo, 100_000, DamageKind.Chemical);
        game.DamageZombie(kilo, 100_000, DamageKind.Chemical);

        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Empty(game.KiloCovers);
        Assert.Equal(2, game.PendingBlockDrops.Count);
        Assert.All(game.PendingBlockDrops, block =>
        {
            Assert.Equal(material, block.Content);
            Assert.Equal(kilo.Cell, block.Cell);
            Assert.False(block.WasBarrier);
            Assert.Null(block.Hp);
        });
        Assert.Equal(0, kilo.HeldBucketUnits);
        Assert.Empty(game.Buckets.PendingSplashes);
    }

    [Fact]
    public void OwningBucketsDoesNotPreventSpendingARealHeldBlockOnABridge()
    {
        var (game, kilo) = EquipAtShore();
        kilo.Position = kilo.StandAt(new Cell(5, 16, 5));
        Invoke(game, "DamageKiloCover", kilo.Id, 0, 13f);
        var from = kilo.Cell;
        var next = from + new Cell(1, 0, 0);
        for (int x = 6; x <= 7; x++)
            for (int z = 5; z <= 6; z++)
                if (game.World.GetMaterial(new Cell(x, 15, z)) != Material.Air)
                    Assert.True((bool)Invoke(game.World, "Dig", new Cell(x, 15, z))!);

        Assert.True((bool)Invoke(game, "TryBuildBridge", kilo, from, next)!);

        Assert.True(kilo.CarriesBucket);
        Assert.Equal(2, kilo.BucketCount);
        Assert.Equal(0, kilo.HeldBucketUnits);
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(new Cell(6, 15, 5)));
        Assert.Equal(87, game.World.GetTerrainHp(new Cell(6, 15, 5)));
        var remaining = Assert.Single(game.KiloCovers);
        Assert.Equal(1, remaining.BlockIndex);
        Assert.Equal(100, remaining.Hp);
    }

    private static Zombie AddKilo(GameSession game)
    {
        var kilo = new Zombie { Id = -1, WaveIndex = -1, Size = ZombieSize.Kilo };
        kilo.Position = kilo.StandAt(new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitZ);
        game.Zombies.Add(kilo);
        return kilo;
    }

    private static (GameSession Game, Zombie Kilo) EquipAtShore()
    {
        var game = new GameSession();
        var kilo = AddKilo(game);
        Invoke(game, "TryKiloAction", kilo, .02f);
        Assert.Equal(2, game.KiloCovers.Count);
        kilo.Position = kilo.StandAt(new Cell(12, 16, 10));
        return (game, kilo);
    }

    private static void AddSources(GameSession game)
    {
        Assert.True(game.Liquids.TryAdd(new Cell(12, 16, 12), LiquidKind.Water, 10));
        Assert.True(game.Liquids.TryAdd(new Cell(13, 16, 12), LiquidKind.Mercury, 10));
    }

    private static void FireAtFirstHand(GameSession game, Zombie kilo, float damage)
    {
        var cover = Assert.Single(game.KiloCovers, candidate => candidate.CarrierId == kilo.Id && candidate.BlockIndex == 0);
        var aim = (cover.Min + cover.Max) * .5f;
        var side = aim - kilo.Position;
        side.Y = 0;
        if (side.LengthSquared() < .00001f) side = Vector3.UnitZ;
        side = Vector3.Normalize(side);
        game.Projectiles.Add(new Projectile
        {
            Origin = aim + side * 3, Target = aim, Damage = damage, Kind = ShotKind.Pellet, Duration = .1f
        });
        Invoke(game, "UpdateProjectiles", .1f);
    }

    private static (int Index, Material Material, float Hp, bool Barrier)[] BlockInventory(GameSession game) => game.KiloCovers
        .OrderBy(cover => cover.BlockIndex).Select(cover => (cover.BlockIndex, cover.Material, cover.Hp, cover.WasBarrier)).ToArray();

    private static int WorldUnits(GameSession game) => game.Liquids.Cells.Sum(cell => cell.Water + cell.Mercury + cell.Lava);
    private static bool BucketAction(GameSession game, Zombie kilo) => (bool)Invoke(game, "TryBucketAction", kilo)!;
    private static object? Invoke(object target, string name, params object?[] arguments) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
