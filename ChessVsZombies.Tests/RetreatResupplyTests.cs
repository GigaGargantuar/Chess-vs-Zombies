using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class RetreatResupplyTests
{
    [Fact]
    public void SpentEnemiesWalkToTheEdgeAndReturnWithTheirOriginalHealthAndWaveIdentity()
    {
        var game = CreateDefense();
        ExcavateMoat(game);
        var zombie = BeginAt(game, new Cell(8, 16, 15), ZombieKind.Armored);
        Set(zombie, nameof(Zombie.CarriedBlocks), 0);
        game.DamageZombie(zombie, 3, DamageKind.Chemical);
        float bodyHp = zombie.BodyHp;
        var armorHp = zombie.ArmorPieces.Select(piece => piece.Hp).ToArray();
        var wave = game.Waves[0];
        int spawned = wave.Spawned, total = wave.TotalCount;
        float budgetHp = wave.TotalHp, damage = wave.DamageTaken;
        long spent = wave.BudgetSpent;
        var origin = zombie.Position;

        AdvanceUntil(game, 3, () => game.IsZombieRetreating(zombie));

        Assert.True(game.IsZombieRetreating(zombie));
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Same(zombie, Assert.Single(game.Zombies));
        Assert.True(zombie.Position.X <= origin.X + .001f);
        AdvanceUntil(game, 20, () => !game.IsZombieRetreating(zombie) && zombie.CarriedBlocks == 1);

        Assert.False(game.IsZombieRetreating(zombie));
        Assert.Equal(1, zombie.CarriedBlocks);
        Assert.True(zombie.Position.X < 1 || zombie.Position.Z < 1 || zombie.Position.Z > 31,
            $"Supply appeared before the actual outside edge: {zombie.Position}.");
        Assert.Same(zombie, Assert.Single(game.Zombies));
        Assert.Equal(bodyHp, zombie.BodyHp);
        Assert.Equal(armorHp, zombie.ArmorPieces.Select(piece => piece.Hp).ToArray());
        Assert.Equal(0, zombie.WaveIndex);
        Assert.Equal(spawned, wave.Spawned);
        Assert.Equal(total, wave.TotalCount);
        Assert.Equal(budgetHp, wave.TotalHp);
        Assert.Equal(damage, wave.DamageTaken);
        Assert.Equal(spent, wave.BudgetSpent);
        Assert.Equal(GamePhase.Combat, game.Phase);

        var supplied = zombie.Position;
        AdvanceUntil(game, 4, () => zombie.Position.X > supplied.X + 1);
        Assert.True(zombie.Position.X > supplied.X + 1, "The same supplied zombie never rejoined its wave's approach.");
    }

    [Fact]
    public void ARealAvailableBridgePlacementKeepsTheArmyInTheField()
    {
        var game = CreateDefense();
        ExcavateMoat(game);
        var builder = BeginAt(game, new Cell(8, 16, 15));

        game.Update(.05f);

        Assert.Equal(0, builder.CarriedBlocks);
        Assert.False(game.IsZombieRetreating(builder));
        Assert.Equal(Material.Dirt, game.World.GetMaterial(new Cell(9, 15, 15)));
    }

    [Fact]
    public void ASpentZombieStillBreachesFiniteCoverInsteadOfRetreating()
    {
        var game = CreateDefense();
        var zombie = BeginAt(game, new Cell(8, 16, 15));
        Set(zombie, nameof(Zombie.CarriedBlocks), 0);
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, Material, bool>>(game.World);
        // A complete wall is expensive, but its finite material remains a route.
        for (int z = 0; z < WorldGrid.Size; z++)
            for (int y = 16; y < WorldGrid.Size; y++) Assert.True(place(new Cell(10, y, z), Material.Dirt));

        AdvanceUntil(game, 4, () => game.World.DamagedTerrain.Any());

        Assert.False(game.IsZombieRetreating(zombie));
        Assert.NotEmpty(game.World.DamagedTerrain);
    }

    [Fact]
    public void ALongPhysicalClimbDoesNotTriggerRetreatBetweenSupportedGridNodes()
    {
        var game = CreateDefense();
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, Material, bool>>(game.World);
        for (int z = 0; z < WorldGrid.Size; z++) Assert.True(place(new Cell(9, 16, z), Material.Dirt));
        var zombie = BeginAt(game, new Cell(8, 16, 15), ZombieKind.Armed);
        Set(zombie, nameof(Zombie.CarriedBlocks), 0);
        var origin = zombie.Position;
        bool aboveLedge = false;

        for (float elapsed = 0; elapsed < 4; elapsed += .02f)
        {
            game.Update(.02f);
            aboveLedge |= zombie.Position.Y > 17;
            Assert.False(game.IsZombieRetreating(zombie), $"A moving climber retreated at {zombie.Position}.");
        }

        Assert.True(aboveLedge);
        Assert.True(zombie.Position.X > origin.X + 1);
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Empty(game.World.DamagedTerrain);
    }

    [Fact]
    public void SupplyCannotAppearBeforeReachingTheOutsideEdge()
    {
        var game = CreateDefense();
        ExcavateMoat(game);
        var zombie = BeginAt(game, new Cell(8, 16, 15));
        Set(zombie, nameof(Zombie.CarriedBlocks), 0);

        game.Update(3);

        Assert.True(game.IsZombieRetreating(zombie));
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.True(zombie.Position.X is > 1 and < 8.6f);
        Assert.Equal(Balance.ZombieHp, zombie.Hp);
    }

    [Fact]
    public void AnIrrelevantFullBucketCanBeDisposedBeforeAStrandedArmyRetreats()
    {
        var game = CreateDefense();
        ExcavateMoat(game);
        var zombie = BeginAt(game, new Cell(8, 16, 15));
        Set(zombie, nameof(Zombie.CarriesBucket), true);
        Set(zombie, nameof(Zombie.BucketKind), LiquidKind.Water);
        Set(zombie, nameof(Zombie.BucketUnits), 10);

        Assert.False((bool)Invoke(game, "CanZombieOpenCoreRoute", zombie)!);
        var origin = zombie.Position;
        game.Update(1.2f);

        Assert.False(game.IsZombieRetreating(zombie));
        Assert.True(Vector3.Distance(origin, zombie.Position) > .1f,
            "A real full bucket did not begin its reachable disposal trip.");
        AdvanceUntil(game, 25, () => game.IsZombieRetreating(zombie));

        Assert.True(game.IsZombieRetreating(zombie));
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Equal(0, zombie.HeldBucketUnits);
        Assert.Equal(10, game.Liquids.GetEscapedUnits(LiquidKind.Water) +
            game.Buckets.Flights.Sum(flight => flight.Units) + game.Buckets.PendingUnits + game.Liquids.Cells.Sum(cell => cell.Water));
    }

    [Fact]
    public void AnIsolatedRetreaterDoesNotTeleportAcrossMissingSupport()
    {
        var game = CreateDefense();
        ExcavateMoat(game);
        // Remove the other exit as well, leaving a real narrow island.
        ExcavateColumns(game, 5, 7);
        var zombie = BeginAt(game, new Cell(8, 16, 15));
        Set(zombie, nameof(Zombie.CarriedBlocks), 0);

        game.Update(8);

        Assert.True(game.IsZombieRetreating(zombie));
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.InRange(zombie.Position.X, 8, 9);
        Assert.Equal(16.5f, zombie.Position.Y, 3);
        Assert.Equal(Balance.ZombieHp, zombie.Hp);
    }

    [Fact]
    public void AResupplyExchangeConservesTheOldBucketsLiquidAndDoesNotHealItsCarrier()
    {
        var game = CreateDefense();
        ExcavateMoat(game);
        var zombie = BeginAt(game, new Cell(8, 16, 15));
        Set(zombie, nameof(Zombie.CarriedBlocks), 0);
        AdvanceUntil(game, 3, () => game.IsZombieRetreating(zombie));
        Assert.True(game.IsZombieRetreating(zombie));
        // Represent a payload acquired earlier in the same journey. Exchanging
        // it must release that real matter instead of deleting it at the edge.
        Set(zombie, nameof(Zombie.CarriesBucket), true);
        Set(zombie, nameof(Zombie.BucketKind), LiquidKind.Water);
        Set(zombie, nameof(Zombie.BucketUnits), 10);
        game.DamageZombie(zombie, 2, DamageKind.Chemical);

        AdvanceUntil(game, 25, () => !game.IsZombieRetreating(zombie));

        Assert.False(game.IsZombieRetreating(zombie));
        Assert.False(zombie.CarriesBucket);
        Assert.Equal(1, zombie.CarriedBlocks);
        Assert.Equal(8, zombie.BodyHp);
        Assert.Equal(0, zombie.BucketUnits);
        Assert.Equal(10, game.Liquids.Cells.Sum(cell => cell.Water) + game.Buckets.PendingUnits);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void AKiloMustReachTheEdgeWithItsWholeContainedCoverBeforeResupplying()
    {
        var game = CreateDefense();
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var kilo = new Zombie
        {
            Id = -1, WaveIndex = 0, Size = ZombieSize.Kilo,
            Kind = ZombieKind.Basic, Attack = 1
        };
        kilo.Position = kilo.StandAt(new Cell(5, 16, 8));
        kilo.FaceToward(-Vector3.UnitX);
        game.Zombies.Add(kilo);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Invoke(game, "UpdateKilos", 2f);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.Equal(2, game.KiloCovers.Count);
        Assert.Equal(0, kilo.CarriedBlocks);
        var heldHp = game.KiloCovers.Select(cover => cover.Hp).ToArray();

        var states = (System.Collections.IDictionary)typeof(GameSession)
            .GetField("_retreating", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var stateType = typeof(GameSession).GetNestedType("RetreatState", BindingFlags.NonPublic)!;
        states[kilo.Id] = Activator.CreateInstance(stateType, nonPublic: true);
        // Cover no longer protrudes beyond the body or qualifies it for an
        // early refill while it is still away from the outer spawn band.
        kilo.Position = new Vector3(2.4f, 17, 9);
        kilo.NextCell = new Cell(0, 16, 8);
        var reached = kilo.Position;

        Assert.False((bool)Invoke(game, "TryCompleteRetreat", kilo, 1.1f)!);
        Assert.True(game.IsZombieRetreating(kilo));
        Assert.Equal(reached, kilo.Position);
        kilo.Position = kilo.StandAt(new Cell(0, 16, 8));
        reached = kilo.Position;
        Assert.True((bool)Invoke(game, "TryCompleteRetreat", kilo, 1.1f)!);

        Assert.False(game.IsZombieRetreating(kilo));
        Assert.Equal(reached, kilo.Position);
        Assert.Null(kilo.NextCell);
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Equal(2, game.KiloCovers.Count);
        Assert.Equal(heldHp, game.KiloCovers.Select(cover => cover.Hp).ToArray());
    }

    [Fact]
    public void ARetreatingKiloFacesTheLatestIncomingShotWhileWalkingAway()
    {
        var (game, kilo) = BeginRetreatingKilo(withCover: false);
        var origin = kilo.Position;

        game.DamageZombie(kilo, 1, DamageKind.Pierce, ShotKind.Pellet, -Vector3.UnitX);
        game.Update(.25f);

        Assert.True(kilo.Position.X < origin.X, "Guarding against fire stopped physical retreat.");
        Assert.Equal(Vector3.UnitX, kilo.Facing);
        game.DamageZombie(kilo, 1, DamageKind.Blunt, ShotKind.Lob, Vector3.UnitZ);
        game.Update(.25f);
        Assert.True(kilo.Position.X < origin.X);
        Assert.Equal(-Vector3.UnitZ, kilo.Facing);
    }

    [Fact]
    public void ARealCoverHitMakesARetreatingKiloGuardWithOneHandAndKeepMoving()
    {
        var (game, kilo) = BeginRetreatingKilo(withCover: true);
        float bodyHp = kilo.BodyHp;
        float coverHp = game.KiloCovers.Sum(cover => cover.Hp);
        var cover = game.KiloCovers.OrderBy(block => block.Min.Y).First();
        var inactive = game.KiloCovers.Single(block => block.BlockIndex != cover.BlockIndex);
        var inactiveOffset = (inactive.Min + inactive.Max) * .5f - kilo.Position;
        var aim = (cover.Min + cover.Max) * .5f;
        game.Projectiles.Add(new Projectile
        {
            Origin = aim + Vector3.UnitX * 3,
            Target = aim,
            Kind = ShotKind.Pellet,
            Damage = 1,
            Duration = .1f
        });

        Invoke(game, "UpdateProjectiles", .2f);
        var origin = kilo.Position;
        game.Update(.25f);

        Assert.Equal(bodyHp, kilo.BodyHp);
        Assert.Equal(coverHp - 1, game.KiloCovers.Sum(block => block.Hp));
        Assert.True(kilo.Position.X < origin.X);
        Assert.Equal(Vector3.UnitX, kilo.Facing);
        inactive = game.KiloCovers.Single(block => block.BlockIndex == inactive.BlockIndex);
        Assert.InRange(Vector3.Distance(inactiveOffset, (inactive.Min + inactive.Max) * .5f - kilo.Position), 0, .00001f);
    }

    [Theory]
    [InlineData("supply")]
    [InlineData("death")]
    [InlineData("reset")]
    public void GuardFacingIsReleasedWhenTheRetreatEnds(string ending)
    {
        var (game, kilo) = BeginRetreatingKilo(withCover: false);
        game.DamageZombie(kilo, 1, DamageKind.Pierce, ShotKind.Pellet, -Vector3.UnitX);
        kilo.FaceToward(Vector3.UnitZ);
        Assert.Equal(Vector3.UnitX, kilo.Facing);

        if (ending == "supply")
        {
            kilo.Position = kilo.StandAt(new Cell(0, 16, 8));
            Assert.True((bool)Invoke(game, "TryCompleteRetreat", kilo, 1.1f)!);
        }
        else if (ending == "death") game.DamageZombie(kilo, 100_000, DamageKind.Chemical);
        else game.Reset();

        kilo.FaceToward(Vector3.UnitZ);
        Assert.Equal(Vector3.UnitZ, kilo.Facing);
    }

    [Fact]
    public void StandardRetreatersKeepTheirOrdinaryTravelFacing()
    {
        var game = CreateDefense();
        var zombie = BeginAt(game, new Cell(5, 16, 8));
        MarkRetreating(game, zombie);
        game.DamageZombie(zombie, 1, DamageKind.Pierce, ShotKind.Pellet, -Vector3.UnitX);

        zombie.FaceToward(Vector3.UnitZ);

        Assert.Equal(Vector3.UnitZ, zombie.Facing);
    }

    private static GameSession CreateDefense()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Name = "Retreat and resupply",
            Waves = [new() { Groups = [new() { Kind = ZombieKind.Basic, Count = 1 }] }]
        });
        return new GameSession(12345, levels);
    }

    private static (GameSession Game, Zombie Kilo) BeginRetreatingKilo(bool withCover)
    {
        var game = CreateDefense();
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var kilo = new Zombie
        {
            Id = -1, WaveIndex = 0, Size = ZombieSize.Kilo,
            Kind = ZombieKind.Basic, Attack = Balance.BaseAttack
        };
        kilo.Position = kilo.StandAt(new Cell(6, 16, 8));
        kilo.FaceToward(Vector3.UnitX);
        game.Zombies.Add(kilo);
        if (withCover)
        {
            Invoke(game, "TryKiloAction", kilo, .05f);
            Invoke(game, "UpdateKilos", 2f);
            Invoke(game, "TryKiloAction", kilo, .05f);
            Assert.Equal(2, game.KiloCovers.Count);
        }
        else Set(kilo, nameof(Zombie.CarriedBlocks), 0);
        MarkRetreating(game, kilo);
        return (game, kilo);
    }

    private static void MarkRetreating(GameSession game, Zombie zombie)
    {
        var states = (System.Collections.IDictionary)typeof(GameSession)
            .GetField("_retreating", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var stateType = typeof(GameSession).GetNestedType("RetreatState", BindingFlags.NonPublic)!;
        states[zombie.Id] = Activator.CreateInstance(stateType, nonPublic: true);
    }

    private static Zombie BeginAt(GameSession game, Cell cell, ZombieKind kind = ZombieKind.Basic)
    {
        Assert.True(game.StartLevel(), game.Message);
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var zombie = new Zombie
        {
            Id = -1, WaveIndex = 0, Kind = kind, Tier = GearTier.Wood,
            Position = cell.Center, Attack = Balance.BaseAttack, Lane = 0
        };
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static void ExcavateMoat(GameSession game)
    {
        ExcavateColumns(game, 9, 12);
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        // The core anchors the far bank's surface slab. Its underside is too
        // high to reach from the bottom plane without constructing a bridge.
        for (int x = 13; x < WorldGrid.Size; x++)
            for (int y = 0; y < 15; y++)
                for (int z = 0; z < WorldGrid.Size; z++) Assert.True(dig(new Cell(x, y, z)));
    }

    private static void ExcavateColumns(GameSession game, int minX, int maxX)
    {
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        for (int x = minX; x <= maxX; x++)
            for (int y = 0; y < 16; y++)
                for (int z = 0; z < WorldGrid.Size; z++) Assert.True(dig(new Cell(x, y, z)));
    }

    private static void Set(Zombie zombie, string name, object value) => typeof(Zombie).GetProperty(name)!.SetValue(zombie, value);

    private static object? Invoke(GameSession game, string name, params object[] arguments) => typeof(GameSession)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, arguments);

    private static void AdvanceUntil(GameSession game, float seconds, Func<bool> complete)
    {
        for (float elapsed = 0; elapsed < seconds && !complete(); elapsed += .05f) game.Update(.05f);
    }
}
