using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GigaCombatTests
{
    [Fact]
    public void TakingFireDoesNotLockAnUnshieldedGigasCannonAwayFromItsTarget()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        var target = Assert.Single(game.Turrets);
        game.DamageZombie(giga, 20, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitX);

        game.Update(.05f);

        var direction = target.Cell.Center - giga.Position;
        direction.Y = 0;
        Assert.True(Vector3.Dot(giga.Facing, Vector3.Normalize(direction)) > .9999f);
        Assert.Single(game.GigaProjectiles);
    }

    [Fact]
    public void PublicCombatLaunchesARealShotAndOnlyItsImpactAdvancesAttack()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        var turret = Assert.Single(game.Turrets);
        float hp = turret.Hp;

        game.Update(.05f);

        var shot = Assert.Single(game.GigaProjectiles);
        Assert.Equal(giga.Id, shot.OwnerId);
        Assert.Equal(turret.Id, shot.TargetId);
        Assert.Equal(hp, turret.Hp);
        Assert.Equal(1, giga.Giga!.AttackCounter);
        Assert.Equal(12, Vector3.Distance(shot.Origin, shot.Target) / shot.Duration, 4);

        game.Update(.5f);

        Assert.Empty(game.GigaProjectiles);
        Assert.Equal(hp - shot.Damage, turret.Hp, 4);
        Assert.Equal(2, giga.Giga.AttackCounter);
    }

    [Fact]
    public void TerrainInTheFlightPathTakesTheShotBeforeTheTurret()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        var wall = new Cell(10, 17, 5);
        Place(game, new Cell(10, 16, 5));
        Place(game, wall);
        float hp = Assert.Single(game.Turrets).Hp;

        game.Update(.05f);
        var shot = Assert.Single(game.GigaProjectiles);
        game.Update(.5f);

        Assert.Empty(game.GigaProjectiles);
        Assert.Equal(hp, Assert.Single(game.Turrets).Hp);
        Assert.Equal(Balance.StoneBlockHp - shot.Damage, game.World.GetTerrainHp(wall), 4);
        Assert.Equal(2, giga.Giga!.AttackCounter);
    }

    [Fact]
    public void AFixedThreeCellBlastDamagesNearbyTurretsOnceAndLeavesDistantTurretsAlone()
    {
        Cell[] cells = [new(12, 16, 5), new(12, 16, 6), new(12, 16, 8)];
        var (game, giga) = Combat(cells);
        for (int hit = 0; hit < 3; hit++) giga.Giga!.RecordSuccessfulAttack(false);

        game.Update(.05f);
        var shot = Assert.Single(game.GigaProjectiles);
        Assert.Equal(1.5f, shot.Radius);
        Assert.Equal(3, shot.BlastSize);
        game.Update(.5f);

        Assert.Equal(Balance.TurretHp - shot.Damage, game.TrySelectTurret(cells[0])!.Hp, 4);
        Assert.Equal(Balance.TurretHp - shot.Damage, game.TrySelectTurret(cells[1])!.Hp, 4);
        Assert.Equal(Balance.TurretHp, game.TrySelectTurret(cells[2])!.Hp);
        Assert.Equal(5, giga.Giga!.AttackCounter);
    }

    [Fact]
    public void KillingTheLockedTargetResetsAttackAndRemovesItsLiveOccupancy()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        var turret = Assert.Single(game.Turrets);
        turret.Hp = 1;
        for (int hit = 0; hit < 4; hit++) giga.Giga!.RecordSuccessfulAttack(false);

        game.Update(.05f);
        Assert.Equal(turret.Id, Assert.Single(game.GigaProjectiles).TargetId);
        game.Update(.5f);

        Assert.Empty(game.Turrets);
        Assert.Null(game.TrySelectTurret(turret.Cell));
        Assert.Equal(1, giga.Giga!.AttackCounter);
    }

    [Fact]
    public void KillingCollateralResetsAttackWhileKeepingTheSurvivingTargetLocked()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5), new Cell(12, 16, 7));
        var locked = game.Turrets[0];
        var collateral = game.Turrets[1];
        collateral.Hp = 1;
        giga.RestoreGigaState(10, 10, 10, 5, 0);
        Locks(game)[giga.Id] = locked.Id;

        Explode(game, giga, collateral.Cell.Center, 2, locked.Id);

        Assert.DoesNotContain(collateral, game.Turrets);
        Assert.Contains(locked, game.Turrets);
        Assert.Equal(Balance.TurretHp, locked.Hp);
        Assert.Equal(1, giga.Giga!.AttackCounter);
        Assert.Equal(locked.Id, Locks(game)[giga.Id]);
    }

    [Fact]
    public void AnOlderShotKillingItsLaunchTargetDoesNotClearANewerLivingTargetLock()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5), new Cell(12, 16, 7));
        var older = game.Turrets[0];
        var current = game.Turrets[1];
        older.Hp = 1;
        giga.RestoreGigaState(10, 10, 10, 5, 0);
        Locks(game)[giga.Id] = current.Id;

        Explode(game, giga, older.Cell.Center, 2, older.Id);

        Assert.DoesNotContain(older, game.Turrets);
        Assert.Contains(current, game.Turrets);
        Assert.Equal(1, giga.Giga!.AttackCounter);
        Assert.Equal(current.Id, Locks(game)[giga.Id]);
    }

    [Theory]
    [InlineData("terrain", false)]
    [InlineData("barrier", false)]
    [InlineData("obsidian", false)]
    [InlineData("terrain", true)]
    [InlineData("barrier", true)]
    [InlineData("obsidian", true)]
    public void DestroyingAnyMaterialResetsAttackWithoutKillingTheLockedTurret(string kind, bool infiniteDamage)
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        var locked = Assert.Single(game.Turrets);
        var cell = new Cell(10, 17, 5);
        Place(game, new Cell(10, 16, 5));
        var center = cell.Center;
        if (kind == "terrain")
        {
            Place(game, cell);
            Assert.True((bool)typeof(WorldGrid).GetMethod("RestoreTerrainHp", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(game.World, [cell, 1f])!);
        }
        else if (kind == "barrier")
            Assert.True((bool)typeof(WorldGrid).GetMethod("AddBarrier", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(game.World, [cell, Material.Stone, (float?)1])!);
        else
        {
            game.Liquids.RestoreState(new LiquidState { Obsidian = [new(cell, 1, CrustFace.Bottom, 1)] });
            var part = Assert.Single(game.World.GetObsidian(cell)!.Parts);
            center = (part.Min + part.Max) * .5f;
        }
        giga.RestoreGigaState(10, 10, 10, 5, 0);
        Locks(game)[giga.Id] = locked.Id;

        Explode(game, giga, center, infiniteDamage ? float.PositiveInfinity : 2, locked.Id);

        Assert.Equal(Material.Air, game.World.GetMaterial(cell));
        Assert.Contains(locked, game.Turrets);
        Assert.Equal(Balance.TurretHp, locked.Hp);
        Assert.Equal(1, giga.Giga!.AttackCounter);
        Assert.Equal(locked.Id, Locks(game)[giga.Id]);
    }

    [Fact]
    public void KillingTheCoreResetsAttackWithoutRequiringTheLockedTurretToDie()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        var locked = Assert.Single(game.Turrets);
        giga.RestoreGigaState(10, 10, 10, 5, 0);
        Locks(game)[giga.Id] = locked.Id;

        Explode(game, giga, new Cell(15, 15, 15).Center, game.CoreHp, locked.Id);

        Assert.Equal(0, game.CoreHp);
        Assert.Contains(locked, game.Turrets);
        Assert.Equal(1, giga.Giga!.AttackCounter);
        Assert.Equal(locked.Id, Locks(game)[giga.Id]);
    }

    [Fact]
    public void AHighCounterShotKeepsItsUnboundedDamageWhileItsBlastStaysLocal()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        giga.InitializeGigaCounter(1389);
        var wall = new Cell(10, 17, 5);
        Place(game, new Cell(10, 16, 5));
        Place(game, wall);

        game.Update(.05f);

        var shot = Assert.Single(game.GigaProjectiles);
        Assert.True(float.IsPositiveInfinity(shot.Damage));
        Assert.Equal(1.5f, shot.Radius);
        Assert.Equal(3, shot.BlastSize);
        game.Update(.5f);

        Assert.Equal(Material.Air, game.World.GetMaterial(wall));
        Assert.Equal(Balance.TurretHp, Assert.Single(game.Turrets).Hp);
        Assert.Equal(Balance.CoreHp, game.CoreHp);
        Assert.True(float.IsFinite(game.CoreHp));
        Assert.Equal(1, giga.Giga!.AttackCounter);
    }

    [Fact]
    public void AShotKeepsItsTrajectoryWhenTheTargetIsRecoveredAndAMissDoesNotAdvanceAttack()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        var turret = Assert.Single(game.Turrets);
        game.Update(.05f);
        var shot = Assert.Single(game.GigaProjectiles);
        var aim = shot.Target;
        Assert.True(game.Remove(turret.Cell));
        // A fixed three-cell cube reaches the ground below this aim point.
        // Clear those possible collateral cells to keep this a genuine miss.
        for (int x = turret.Cell.X - 1; x <= turret.Cell.X + 1; x++)
            for (int z = turret.Cell.Z - 1; z <= turret.Cell.Z + 1; z++)
                Assert.True((bool)typeof(WorldGrid).GetMethod("Dig", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(game.World, [new Cell(x, turret.Cell.Y - 1, z)])!);

        game.Update(.1f);

        Assert.Contains(shot, game.GigaProjectiles);
        Assert.Equal(aim, shot.Target);
        Assert.True(shot.Age > 0);
        game.Update(.5f);
        Assert.Empty(game.GigaProjectiles);
        Assert.Equal(1, giga.Giga!.AttackCounter);
    }

    [Fact]
    public void DamageIsCapturedAtLaunchAndTheBlastStaysFixedDespiteLaterAttackGrowth()
    {
        Cell[] cells = [new(12, 16, 5), new(12, 16, 7)];
        var (game, giga) = Combat(cells);
        game.Update(.05f);
        var shot = Assert.Single(game.GigaProjectiles);
        float damage = shot.Damage, radius = shot.Radius;
        for (int hit = 0; hit < 8; hit++) giga.Giga!.RecordSuccessfulAttack(false);
        Assert.Equal(1.5f, giga.Giga!.BlastRadius);

        game.Update(.5f);

        Assert.Equal(damage, shot.Damage);
        Assert.Equal(radius, shot.Radius);
        Assert.Equal(Balance.TurretHp - damage, game.TrySelectTurret(cells[0])!.Hp, 4);
        Assert.Equal(Balance.TurretHp, game.TrySelectTurret(cells[1])!.Hp);
    }

    [Fact]
    public void AnOwnersDeathDoesNotEraseItsAlreadyLaunchedProjectile()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        var turret = Assert.Single(game.Turrets);
        game.Update(.05f);
        var shot = Assert.Single(game.GigaProjectiles);
        game.DamageZombie(giga, giga.Hp + 100);

        game.Update(.5f);

        Assert.DoesNotContain(giga, game.Zombies);
        Assert.Equal(Balance.TurretHp - shot.Damage, turret.Hp, 4);
        Assert.Empty(game.GigaProjectiles);
    }

    [Fact]
    public void HavingATurretInRangeDoesNotSuspendAnUnsupportedGigasFall()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        giga.Position = giga.StandAt(new Cell(4, 22, 4));
        float start = giga.Position.Y;

        game.Update(.5f);

        Assert.Equal(start - FallAnimations.DistanceAfter(.5f), giga.Position.Y, 4);
        Assert.True(giga.Position.Y < start);
        Assert.Empty(game.GigaProjectiles);
    }

    [Fact]
    public void HavingATurretInRangeDoesNotFreezeAnOngoingPhysicalStep()
    {
        var (game, giga) = Combat(new Cell(12, 16, 5));
        var start = giga.Position;
        giga.NextCell = new Cell(5, 16, 4);

        game.Update(.1f);

        Assert.True(giga.Position.X > start.X);
        Assert.Equal(start.Y, giga.Position.Y);
        Assert.Equal(start.Z, giga.Position.Z);
        Assert.NotNull(giga.NextCell);
        Assert.Empty(game.GigaProjectiles);
    }

    [Fact]
    public void WithoutAnInRangeTurretAGigaStillWalksTowardTheCore()
    {
        var (game, giga) = Combat();
        var start = giga.Position;

        game.Update(.5f);

        Assert.True(Vector3.DistanceSquared(start, giga.Position) > .001f);
        Assert.True(Vector2.Distance(new(giga.Position.X, giga.Position.Z), new(16, 16)) <
            Vector2.Distance(new(start.X, start.Z), new(16, 16)));
    }

    [Fact]
    public void HpGrowthAddsSurvivingWaveHpWithoutUndoingDamageOrChargingTheSpawnPool()
    {
        var (game, giga) = Combat();
        giga.WaveIndex = 0;
        giga.RestoreGigaState(10, 10, 1, 1, .75);
        var wave = Assert.Single(game.Waves);
        wave.TotalHp = giga.Hp;
        wave.BudgetSpent = giga.Giga!.SpawnCost;
        float oldHp = giga.Hp;
        long paid = wave.BudgetSpent;

        game.DamageZombie(giga, 10);

        float expectedHp = (oldHp - 10) * giga.MaxBodyHp / oldHp;
        Assert.InRange(Math.Abs(expectedHp - giga.BodyHp), 0, .0001f);
        Assert.Equal(10, wave.DamageTaken);
        Assert.InRange(Math.Abs(giga.Hp - (wave.TotalHp - wave.DamageTaken)), 0, .0001f);
        Assert.Equal(paid, wave.BudgetSpent);
        Assert.Equal(11, giga.Giga.HpCounter);
        Assert.Equal(0, giga.Giga.RecoverySeconds);
        Assert.Equal(10, Assert.Single(game.Feedback.Hits).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChronologicalImpactsResetAttackOnTheEarlierKillThenAdvanceForLaterCollateral(bool appendEarlierShotLast)
    {
        var (game, giga) = Combat(new Cell(12, 16, 5), new Cell(12, 16, 7));
        var locked = game.Turrets[0];
        var collateral = game.Turrets[1];
        locked.Hp = 1;
        for (int hit = 0; hit < 4; hit++) giga.Giga!.RecordSuccessfulAttack(false);
        var early = new GigaProjectile
        {
            OwnerId = giga.Id, TargetId = locked.Id, Origin = new Vector3(9, 16.5f, 5.5f),
            Target = locked.Cell.Center, Damage = 2, Duration = .2f
        };
        var late = new GigaProjectile
        {
            OwnerId = giga.Id, TargetId = locked.Id, Origin = new Vector3(9, 16.5f, 7.5f),
            Target = collateral.Cell.Center, Damage = 2, Duration = .8f
        };
        game.GigaProjectiles.AddRange(appendEarlierShotLast ? [late, early] : [early, late]);

        typeof(GameSession).GetMethod("UpdateProjectiles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(game, [1f]);

        Assert.DoesNotContain(locked, game.Turrets);
        Assert.Equal(Balance.TurretHp - 2, collateral.Hp);
        Assert.Equal(2, giga.Giga!.AttackCounter);
        Assert.Empty(game.GigaProjectiles);
    }

    private static (GameSession Game, Zombie Giga) Combat(params Cell[] towers)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Waves = [new WaveDefinition { SpawnInterval = 1000, Groups = [new ZombieGroup { Kind = ZombieKind.Basic }] }]
        });
        var game = new GameSession(12345, levels);
        foreach (var cell in towers) Assert.True(game.Deploy(PieceKind.Rook, cell));
        Assert.True(game.StartLevel());
        foreach (var wave in game.Waves) wave.Spawned = wave.TotalCount;
        foreach (var turret in game.Turrets) turret.Cooldown = 1000;
        var giga = new Zombie { Id = 100, WaveIndex = -1, Size = ZombieSize.Giga, IsBoss = true };
        // These mechanics fixtures isolate low-damage impacts from the natural
        // p0-initialized attack progression, covered by the spawn/domain tests.
        giga.RestoreGigaState(10, 10, 10, 1, 0);
        giga.Position = giga.StandAt(new Cell(4, 16, 4));
        game.Zombies.Add(giga);
        return (game, giga);
    }

    private static void Place(GameSession game, Cell cell) => Assert.True((bool)typeof(WorldGrid)
        .GetMethod("RestoreTerrainPlacement", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(game.World, [cell, Material.Stone])!);

    private static Dictionary<int, int> Locks(GameSession game) => (Dictionary<int, int>)typeof(GameSession)
        .GetField("_gigaTargets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;

    private static void Explode(GameSession game, Zombie owner, Vector3 center, float damage, int targetId) =>
        typeof(GameSession).GetMethod("ExplodeGigaProjectile", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(game, [new GigaProjectile { OwnerId = owner.Id, TargetId = targetId, Damage = damage }, center]);
}
