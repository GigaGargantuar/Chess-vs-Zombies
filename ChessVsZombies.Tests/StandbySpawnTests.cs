using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class StandbySpawnTests
{
    [Fact]
    public void BulletsDamageOnlyTheExposedPositionAndStopAtTheFirstBody()
    {
        var game = Battle(new ZombieGroup { Count = 80 });
        var slots = Slots(game);
        var a = slots[0];
        var b = slots.First(slot => Position(slot) != Position(a) &&
            Vector3.Distance(Position(slot), Position(a)) > 5);
        float bHp = Hp(b);
        var active = new Zombie { Id = 500, Position = Vector3.Lerp(Origin(a), Position(a), .5f) };
        game.Zombies.Add(active);
        Fire(game, a, 1000000);
        Assert.Equal(0, active.Hp);
        Assert.Equal(10, Hp(a));
        Assert.Equal(bHp, Hp(b));
        Assert.Equal("body", game.ProjectileDiagnostics!.Contacts[^1].Kind);

        game.Zombies.Remove(active);
        Fire(game, a, 1000000);
        Assert.Equal(0, Hp(a));
        Assert.Equal(bHp, Hp(b));
        Assert.Equal(79, game.LivingStandbyCount);
        Assert.Equal("standby-body", game.ProjectileDiagnostics.Contacts[^1].Kind);
    }

    [Fact]
    public void SamePositionStackExposesTheNextSlotOnlyAfterTheFirstDies()
    {
        var game = Battle(new ZombieGroup { Count = 500 });
        var stack = Slots(game).GroupBy(Position).First(group => group.Count() >= 2).ToArray();
        var front = stack[0];
        var next = stack[1];
        long revision = ColliderRevision(game);
        Fire(game, front, 1000000);
        Assert.True(ColliderRevision(game) > revision);
        Assert.Equal(0, Hp(front));
        Assert.Equal(10, Hp(next));
        Fire(game, next, 1);
        Assert.Equal(9, Hp(next));
    }

    [Fact]
    public void ABlockedScheduledPositionWaitsAtItsOriginalCell()
    {
        // This seed puts the first two ordinary slots in the same cell.
        var game = Battle(new ZombieGroup { Count = 2 });
        var slots = Slots(game);
        Assert.Equal(Position(slots[0]), Position(slots[1]));
        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);
        var first = Assert.Single(game.Zombies);
        var cell = first.Cell;
        game.DamageZombie(first, 1000000, DamageKind.Chemical);
        game.Update(.01f); // Resolve the dropped block on the shared cell.
        Assert.Equal(Material.Dirt, game.World.GetMaterial(cell));
        Assert.False((bool)Invoke(game, "Spawn", game.Waves[0])!);
        Assert.Equal(1, game.Waves[0].Spawned);
        Assert.Equal(cell, Get<Cell?>(slots[1], "Cell"));

        Assert.True((bool)game.World.GetType().GetMethod("Dig", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(game.World, [cell])!);
        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);
        Assert.Equal(cell, Assert.Single(game.Zombies).Cell);
    }

    [Fact]
    public void BlockedFirstSlotDoesNotDelayTheNextPositionOrResetItsDeadline()
    {
        var game = Battle(new ZombieGroup { Count = 3 }, interval: 1, seed: 1);
        var slots = Slots(game);
        Assert.NotEqual(Position(slots[0]), Position(slots[1]));
        var blocked = Get<Cell?>(slots[0], "Cell")!.Value;
        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", blocked, Material.Dirt)!);
        game.Waves[0].SpawnTimer = 1;

        game.Update(1.01f);
        Assert.Equal(0, game.Waves[0].Spawned);
        game.Update(.9f);
        Assert.Equal(0, game.Waves[0].Spawned);
        game.Update(.11f);
        Assert.Equal(1, game.Waves[0].Spawned);
        Assert.Single(game.Zombies);
        Assert.Equal(Get<Cell?>(slots[1], "Cell"), game.Zombies[0].Cell);
        float nextDeadline = game.Waves[0].SpawnTimer;

        Assert.True((bool)Invoke(game.World, "Dig", blocked)!);
        game.Update(.01f);
        Assert.Equal(2, game.Waves[0].Spawned);
        Assert.Contains(game.Zombies, zombie => zombie.Cell == blocked);
        Assert.InRange(game.Waves[0].SpawnTimer, nextDeadline - .011f, nextDeadline - .009f);
        game.Update(.95f);
        Assert.Equal(2, game.Waves[0].Spawned);
        game.Update(.05f);
        Assert.Equal(3, game.Waves[0].Spawned); // C keeps its original deadline.
    }

    [Fact]
    public void MultipleBlockedPositionsWaitIndependently()
    {
        var game = Battle(new ZombieGroup { Count = 3 }, interval: 1, seed: 1);
        var slots = Slots(game);
        var first = Get<Cell?>(slots[0], "Cell")!.Value;
        var second = Get<Cell?>(slots[1], "Cell")!.Value;
        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", first, Material.Dirt)!);
        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", second, Material.Dirt)!);

        game.Update(2.01f);
        Assert.Equal(1, game.Waves[0].Spawned);
        Assert.Equal(Get<Cell?>(slots[2], "Cell"), Assert.Single(game.Zombies).Cell);
        Assert.True((bool)Invoke(game.World, "Dig", second)!);
        game.Update(.01f);
        Assert.Equal(2, game.Waves[0].Spawned);
        Assert.DoesNotContain(game.Zombies, zombie => zombie.Cell == first);
        Assert.True((bool)Invoke(game.World, "Dig", first)!);
        game.Update(.01f);
        Assert.Equal(3, game.Waves[0].Spawned);
        Assert.Contains(game.Zombies, zombie => zombie.Cell == first);
    }

    [Fact]
    public void DueSlotsAtTheSamePositionKeepTheirStackOrder()
    {
        var game = Battle(new ZombieGroup { Count = 2 }, interval: 1);
        var slots = Slots(game);
        var cell = Get<Cell?>(slots[0], "Cell")!.Value;
        Assert.Equal(cell, Get<Cell?>(slots[1], "Cell"));
        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", cell, Material.Dirt)!);
        game.Update(1.01f);
        Assert.Equal(0, game.Waves[0].Spawned);

        Assert.True((bool)Invoke(game.World, "Dig", cell)!);
        game.Update(.01f);
        Assert.Equal(2, game.Waves[0].Spawned);
        Assert.Equal([1, 2], game.Zombies.Select(zombie => zombie.Id).ToArray());
        Assert.All(game.Zombies, zombie => Assert.Equal(cell, zombie.Cell));
    }

    [Fact]
    public void DeadDueSlotStillSeparatesAnOnTimeArrivalBehindABlockedSlot()
    {
        var game = Battle(new ZombieGroup { Count = 3 }, interval: 1, seed: 1);
        var slots = Slots(game);
        var blocked = Get<Cell?>(slots[0], "Cell")!.Value;
        Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", blocked, Material.Dirt)!);
        Fire(game, slots[1], 1000000);
        game.Update(.01f);
        Assert.Equal(0, game.Waves[0].Spawned);
        game.Update(.99f);
        Assert.Equal(1, game.Waves[0].Spawned); // B's empty time slot.
        Assert.Empty(game.Zombies);
        game.Update(1f);
        Assert.Equal(2, game.Waves[0].Spawned); // C arrives while A still waits.
        Assert.Equal(1, game.LivingStandbyCount);
        Assert.Equal(GamePhase.Combat, game.Phase);
    }

    [Fact]
    public void DeadScheduledSlotsKeepTheirIntervalsAndLivingTailPreventsEarlyClear()
    {
        var game = Battle(new ZombieGroup { Count = 4 }, interval: 1, seed: 1);
        var slots = Slots(game);
        game.Update(.01f); // A arrives.
        Assert.Equal(1, game.Waves[0].Spawned);
        foreach (var slot in slots.Skip(1).Take(2)) Fire(game, slot, 1000000);
        Assert.Equal(1, game.LivingStandbyCount); // A is active; only D remains queued.
        game.Update(.99f);
        Assert.Equal(2, game.Waves[0].Spawned); // B consumes its own slot.
        game.Update(1f);
        Assert.Equal(3, game.Waves[0].Spawned); // C consumes its own slot.
        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.Equal(10, Hp(slots[3]));
        game.Update(1f);
        Assert.Equal(4, game.Waves[0].Spawned);
    }

    [Fact]
    public void GiantCoverTakesTheBreakingShotAndRemainingDamageSurvivesActivation()
    {
        var game = Battle(new ZombieGroup { Count = 1, Kind = ZombieKind.Basic, Size = ZombieSize.Kilo, IsBoss = true });
        var slot = Assert.Single(Slots(game));
        float body = Hp(slot);
        Fire(game, slot, Balance.DirtBlockHp - 7);
        Assert.Equal(body, Hp(slot));
        Assert.Equal(7, CoverHp(slot));
        long revision = ColliderRevision(game);
        Fire(game, slot, 1000000);
        Assert.True(ColliderRevision(game) > revision);
        Assert.Equal(0, CoverHp(slot));
        Assert.Equal(body, Hp(slot));
        Fire(game, slot, 3);
        Assert.Equal(body - 3, Hp(slot));
        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);
        var giant = Assert.Single(game.Zombies);
        Assert.Equal(body - 3, giant.Hp);
        Assert.Equal(0, giant.CarriedBlocks);
    }

    [Fact]
    public void PartiallyDamagedGiantCoverActivatesWithItsRemainingHp()
    {
        var game = Battle(new ZombieGroup { Count = 1, Size = ZombieSize.Kilo, IsBoss = true });
        var slot = Assert.Single(Slots(game));
        Fire(game, slot, 13);
        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);
        var giant = Assert.Single(game.Zombies);
        Assert.Equal(0, giant.CarriedBlocks);
        Assert.Equal(Balance.DirtBlockHp - 13, Assert.Single(game.KiloCovers).Hp);
    }

    [Fact]
    public void ArmoredGiantBlockConsumesItsHitThenWearableArmorUsesOrdinaryDamageRules()
    {
        var game = Battle(new ZombieGroup { Count = 1, Kind = ZombieKind.Armored,
            Tier = GearTier.Iron, Size = ZombieSize.Kilo, IsBoss = true });
        var slot = Assert.Single(Slots(game));
        float original = Hp(slot);
        Fire(game, slot, 1000000); // Carried iron block.
        Assert.Equal(original, Hp(slot));
        Fire(game, slot, Balance.GearHp(GearTier.Iron) * 4 + 1);
        Assert.Equal(original - 201, Hp(slot)); // Shield breaks; one point reaches the chestplate.
        Fire(game, slot, 1000000);
        Assert.Equal(0, Hp(slot)); // Ordinary armor can pass remaining damage to the body.
    }

    [Fact]
    public void ArmoredGiantActivatesWithItsDamagedWearableArmorAfterCoverBreaks()
    {
        var game = Battle(new ZombieGroup { Count = 1, Kind = ZombieKind.Armored,
            Tier = GearTier.Iron, Size = ZombieSize.Kilo, IsBoss = true });
        var slot = Assert.Single(Slots(game));
        Fire(game, slot, 1000000); // Break only the carried block.
        Fire(game, slot, 3);
        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);
        var giant = Assert.Single(game.Zombies);
        Assert.Equal(40, giant.BodyHp);
        Assert.Equal(197, giant.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Shield).Hp);
        Assert.Equal(0, giant.CarriedBlocks);
    }

    [Fact]
    public void ArmoredStandbySpawnsWithTheSameDamagedArmor()
    {
        var game = Battle(new ZombieGroup { Count = 1, Kind = ZombieKind.Armored, Tier = GearTier.Iron });
        var slot = Assert.Single(Slots(game));
        Fire(game, slot, 3);
        Assert.Equal(207, Hp(slot));
        Assert.True((bool)Invoke(game, "Spawn", game.Waves[0])!);
        var zombie = Assert.Single(game.Zombies);
        Assert.Equal(207, zombie.Hp);
        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(47, zombie.ArmorPieces.Single(piece => piece.Slot == ArmorSlot.Shield).Hp);
    }

    [Fact]
    public void KillingTheWholeStandbyRosterClearsTheLevelAndPaysOnce()
    {
        var game = Battle(new ZombieGroup { Count = 3 }, interval: 10, seed: 1);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(5, 16, 5)));
        int before = game.Economy.Currency;
        foreach (var slot in Slots(game)) Fire(game, slot, 1000000);
        Assert.Equal(0, game.LivingStandbyCount);
        game.Update(.01f);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(2, game.LevelNumber);
        Assert.Equal(before + Balance.SurvivorIncome, game.Economy.Currency);
        game.Update(1);
        Assert.Equal(before + Balance.SurvivorIncome, game.Economy.Currency);
    }

    [Fact]
    public void PlanningIsRepeatableAndDoesNotConsumeTheGameplayRandomStream()
    {
        var planned = Battle(new ZombieGroup { Count = 10 });
        var control = new GameSession();
        Assert.Equal(Slots(planned).Select(Position), Slots(Battle(new ZombieGroup { Count = 10 })).Select(Position));
        var random = typeof(GameSession).GetField("_random", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(((Random)random.GetValue(control)!).Next(), ((Random)random.GetValue(planned)!).Next());
        var first = Position(Slots(planned)[0]);
        planned.Reset();
        Assert.Equal(0, planned.LivingStandbyCount);
        Assert.True(planned.StartLevel());
        Assert.Equal(first, Position(Slots(planned)[0]));
    }

    [Fact]
    public void LargeQueueProjectileQueryVisitsPositionsRatherThanAllRecords()
    {
        var game = Battle(new ZombieGroup { Count = 10000 });
        var first = Slots(game)[0];
        Fire(game, first, 1);
        Assert.True(game.ProjectileDiagnostics!.StandbyStackQueries < 1000);
        Assert.Equal(10000, game.LivingStandbyCount);
    }

    [Fact]
    public void CachedProjectileForecastIsReevaluatedWhenQueueTargetOrCoverChanges()
    {
        var death = Battle(new ZombieGroup { Count = 1 });
        var deadSlot = Assert.Single(Slots(death));
        AssertForecastInvalidated(death, deadSlot, () => Invoke(death, "DamageStandby", deadSlot,
            1000000f, DamageKind.Chemical, ShotKind.Pellet, Vector3.UnitX));

        var activation = Battle(new ZombieGroup { Count = 1 });
        var arriving = Assert.Single(Slots(activation));
        AssertForecastInvalidated(activation, arriving, () => Invoke(activation, "Spawn", activation.Waves[0]));

        var cover = Battle(new ZombieGroup { Count = 1, Size = ZombieSize.Kilo, IsBoss = true });
        var shielded = Assert.Single(Slots(cover));
        AssertForecastInvalidated(cover, shielded, () => Invoke(cover, "DamageStandby", shielded,
            1000000f, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitX));
    }

    private static void AssertForecastInvalidated(GameSession game, object slot, Action change)
    {
        var shot = new Projectile { Origin = Origin(slot), Target = Position(slot), Damage = 1,
            Kind = ShotKind.Pellet, Duration = .05f };
        game.Projectiles.Add(shot);
        Invoke(game, "BeginMegaInterceptionFrame", .05f);
        Invoke(game, "NextMegaInterceptionTime", shot, .05f);
        long evaluations = game.ProjectileDiagnostics!.ForecastEvaluations;
        Invoke(game, "NextMegaInterceptionTime", shot, .05f);
        Assert.Equal(evaluations, game.ProjectileDiagnostics.ForecastEvaluations);
        change();
        Invoke(game, "NextMegaInterceptionTime", shot, .05f);
        Assert.Equal(evaluations + 1, game.ProjectileDiagnostics.ForecastEvaluations);
        Invoke(game, "EndMegaInterceptionFrame");
    }

    private static GameSession Battle(ZombieGroup group, float interval = 10, int seed = 12345)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Number = 1, Waves = [new WaveDefinition { SpawnInterval = interval, Groups = [group] }] });
        var game = new GameSession(seed, catalog) { ProjectileDiagnostics = new ProjectileDiagnostics { CaptureContacts = true } };
        Assert.True(game.StartLevel());
        return game;
    }

    private static object[] Slots(GameSession game)
    {
        var waves = (IList)typeof(GameSession).GetField("_standbyWaves", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;
        return ((IEnumerable)waves[0]!).Cast<object>().ToArray();
    }

    private static T Get<T>(object slot, string name) => (T)slot.GetType().GetProperty(name)!.GetValue(slot)!;
    private static float Hp(object slot) => Get<float>(slot, "Hp");
    private static float CoverHp(object slot) => Get<float?>(slot, "CoverHp") ?? 0;
    private static long ColliderRevision(GameSession game) => (long)typeof(GameSession).GetField(
        "_projectileColliderRevision", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;
    private static Vector3 Position(object slot)
    {
        var group = Get<ZombieGroup>(slot, "Group");
        return new Zombie { Size = group.Size }.StandAt(Get<Cell?>(slot, "Cell")!.Value);
    }
    private static Vector3 Origin(object slot)
    {
        var cell = Get<Cell?>(slot, "Cell")!.Value;
        var group = Get<ZombieGroup>(slot, "Group");
        var direction = cell.X == 0 ? Vector3.UnitX : cell.X + new Zombie { Size = group.Size }.GridFootprint == WorldGrid.Size
            ? -Vector3.UnitX : cell.Z == 0 ? Vector3.UnitZ : -Vector3.UnitZ;
        return Position(slot) + direction * (group.Size == ZombieSize.Kilo ? 3f : group.Size == ZombieSize.Mega ? 4f : 2f);
    }
    private static void Fire(GameSession game, object slot, float damage)
    {
        game.Projectiles.Add(new Projectile { Origin = Origin(slot), Target = Position(slot), Damage = damage,
            Kind = ShotKind.Pellet, Duration = .05f });
        Invoke(game, "UpdateProjectiles", .05f);
    }
    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
}
