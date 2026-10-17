using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MicroBehaviorTests
{
    [Fact]
    public void AnIncomingBulletAimedAtAnotherZombieStillTriggersEvasion()
    {
        var (game, micro) = Isolated();
        var (control, ordinaryMovement) = Isolated();
        var start = micro.Position;
        game.Projectiles.Add(Pellet(start - Vector3.UnitX * .4f, start + Vector3.UnitX * .4f, duration: .08f, targetId: 999));

        TickMicro(game, micro, .05f);
        TickMicro(control, ordinaryMovement, .05f);

        Assert.True(Vector3.DistanceSquared(micro.Position, ordinaryMovement.Position) > .0001f,
            "An incidental incoming bullet produced no change from the normal flank approach.");
        Assert.True(Math.Abs(micro.Position.Z - start.Z) > .01f || micro.Position.Y > start.Y + .01f,
            "The dodge must move across the bullet path or jump out of it.");
    }

    [Theory]
    [InlineData(ShotKind.Pellet, true)]
    [InlineData(ShotKind.Melee, false)]
    public void OutgoingBulletsAndMeleeEffectsDoNotTriggerBulletEvasion(ShotKind kind, bool outgoing)
    {
        var (game, micro) = Isolated();
        var (control, ordinaryMovement) = Isolated();
        var start = micro.Position;
        game.Projectiles.Add(new Projectile
        {
            Origin = start - Vector3.UnitX * (outgoing ? 1 : 2),
            Target = start + Vector3.UnitX * (outgoing ? -3 : 2),
            TargetId = micro.Id, Kind = kind, Damage = 1, Duration = .5f
        });

        TickMicro(game, micro, .05f);
        TickMicro(control, ordinaryMovement, .05f);

        Assert.Equal(ordinaryMovement.Position, micro.Position);
        Assert.Equal(ordinaryMovement.MicroDashRemaining, micro.MicroDashRemaining);
    }

    [Fact]
    public void ALobThatActuallyPassesAboveTheBodyDoesNotTriggerAChordBasedDodge()
    {
        var (game, micro) = Isolated();
        var (control, ordinaryMovement) = Isolated();
        var start = micro.Position;
        var lob = new Projectile
        {
            Origin = start - Vector3.UnitX * 2, Target = start + Vector3.UnitX * 2,
            TargetId = micro.Id, Kind = ShotKind.Lob, Damage = 1, Duration = .5f
        };
        Assert.True(lob.GetPositionAt(.25f).Y > start.Y + 1);
        game.Projectiles.Add(lob);

        TickMicro(game, micro, .05f);
        TickMicro(control, ordinaryMovement, .05f);

        Assert.Equal(ordinaryMovement.Position, micro.Position);
    }

    [Fact]
    public void PublicCombatLetsAMicroDodgeBeforeAnIncomingBulletCrossesItsOriginalBody()
    {
        var game = Combat();
        var micro = AddMicro(game, new Cell(8, 16, 8));
        var start = micro.Position;
        // This ray crosses the old body center in the first simulation slice.
        game.Projectiles.Add(Pellet(start - Vector3.UnitX * .4f, start + Vector3.UnitX * .4f, duration: .08f));

        game.Update(.05f);

        Assert.Equal(2, micro.BodyHp);
        Assert.Contains(micro, game.Zombies);
        Assert.True(Vector3.DistanceSquared(start, micro.Position) > .01f);
    }

    [Theory]
    [InlineData(.1f, true)]
    [InlineData(.15f, false)]
    public void ActualProjectileContactUsesAQuarterOfTheRegularBodyRadius(float sidewaysOffset, bool hits)
    {
        var (game, micro) = Isolated();
        var start = micro.Position;
        game.Projectiles.Add(Pellet(start - Vector3.UnitX + Vector3.UnitZ * sidewaysOffset,
            start + Vector3.UnitX + Vector3.UnitZ * sidewaysOffset, duration: .2f));

        Invoke(game, "UpdateProjectiles", .2f);

        Assert.Equal(hits ? 1 : 2, micro.BodyHp);
        Assert.Equal(start, micro.Position);
    }

    [Fact]
    public void AJumpingMicroContinuesToStrafeWhileItHasNoGroundSupport()
    {
        var (game, micro) = Isolated();
        float standingHeight = micro.Position.Y;
        var start = micro.Position;
        game.Projectiles.Add(Pellet(start - Vector3.UnitX * .4f, start + Vector3.UnitX * .4f, duration: .08f));
        bool sawAirborneStrafe = false;

        for (int frame = 0; frame < 30; frame++)
        {
            var before = micro.Position;
            TickMicro(game, micro, .025f);
            if (before.Y > standingHeight + .05f && micro.Position.Y > standingHeight + .05f &&
                Math.Abs(micro.Position.Z - before.Z) > .005f)
                sawAirborneStrafe = true;
            Assert.True(IsBodyClear(game, micro));
        }

        Assert.True(sawAirborneStrafe, "The incoming bullet never caused a jump with continuing sideways movement in flight.");
    }

    [Fact]
    public void NaturalAirborneSteeringPreservesTheElapsedPhysicalFallCurve()
    {
        var game = Combat();
        var micro = AddMicro(game, new Cell(8, 16, 8));
        micro.Position += Vector3.UnitY * 5;
        var start = micro.Position;

        game.Update(.5f);
        Assert.InRange(Math.Abs(start.Y - FallAnimations.DistanceAfter(.5f) - micro.Position.Y), 0, .00002f);
        game.Update(.5f);
        Assert.InRange(Math.Abs(start.Y - FallAnimations.DistanceAfter(1) - micro.Position.Y), 0, .00002f);
        game.Update(.25f);
        Assert.InRange(Math.Abs(start.Y - FallAnimations.DistanceAfter(1.25f) - micro.Position.Y), 0, .00002f);

        Assert.True(Vector2.Distance(new(start.X, start.Z), new(micro.Position.X, micro.Position.Z)) > 1,
            "The falling Micro never moved horizontally.");
        Assert.Equal(2, micro.BodyHp);
        Assert.True(IsBodyClear(game, micro));
    }

    [Fact]
    public void PublicDashTimeExpiresWhileBuoyancyHandlesMovement()
    {
        var game = Combat();
        var micro = AddMicro(game, new Cell(8, 16, 8));
        var pool = new List<LiquidCell>();
        for (int x = 4; x <= 12; x++)
            for (int z = 4; z <= 12; z++) pool.Add(new(new Cell(x, 16, z), 0, 10, 0));
        game.Liquids.RestoreState(new LiquidState { Cells = pool });
        typeof(Zombie).GetProperty(nameof(Zombie.MicroDashRemaining))!.SetValue(micro, .2f);
        float standingHeight = micro.Position.Y;

        game.Update(.25f);

        Assert.Equal(0, micro.MicroDashRemaining);
        Assert.Equal(new Zombie().MovementSpeed * 3, micro.MovementSpeed, 5);
        Assert.True(micro.Position.Y > standingHeight + .4f);
        Assert.True(micro.BodyHp > 0);
    }

    [Fact]
    public void DashDurationsStayWithinTheSpecifiedRangeAndAreDeterministicForASeed()
    {
        var durations = new List<float>();
        for (int seed = 1; seed <= 16; seed++)
        {
            var (game, micro) = Isolated(seed);
            var (sameSeed, sameMicro) = Isolated(seed);
            // Let the initial dash cooldown elapse, with enough distance to
            // the core for an ordinary running dash to be useful.
            TickMicro(game, micro, .75f);
            TickMicro(sameSeed, sameMicro, .75f);

            Assert.InRange(micro.MicroDashRemaining, .199f, 2f);
            Assert.Equal(micro.MicroDashRemaining, sameMicro.MicroDashRemaining);
            Assert.Equal(micro.Position, sameMicro.Position);
            durations.Add(micro.MicroDashRemaining);
        }
        Assert.True(durations.Max() - durations.Min() > .1f, "Every dash used the same duration.");
    }

    [Fact]
    public void AMicroHitsAnAdjacentTurretWithoutStoppingItsFlankMovement()
    {
        var game = Combat();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 9)));
        var turret = Assert.Single(game.Turrets);
        turret.Cooldown = 1000;
        var micro = AddMicro(game, new Cell(8, 16, 8));
        var start = micro.Position;
        float hp = turret.Hp;

        game.Update(.05f);

        Assert.Equal(hp - micro.Attack, turret.Hp);
        Assert.True(Vector3.DistanceSquared(start, micro.Position) > .001f);
        var afterSwipe = micro.Position;
        game.Update(.15f);
        Assert.True(Vector3.DistanceSquared(afterSwipe, micro.Position) > .001f);
        Assert.Equal(hp - micro.Attack, turret.Hp);
    }

    [Fact]
    public void AStrafingDodgeTakesPriorityOverStandingStillToAttackTheCore()
    {
        var game = new GameSession();
        var micro = AddMicro(game, new Cell(14, 16, 15));
        var start = micro.Position;
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(new Cell(14, 16, 15), 3, CrustFace.Top, 15)]
        });
        game.Projectiles.Add(Pellet(start - Vector3.UnitZ * .3f, start + Vector3.UnitZ * .5f, duration: .08f));

        TickMicro(game, micro, .05f);

        Assert.Equal(start.Y, micro.Position.Y, 5);
        Assert.True(Vector3.DistanceSquared(start, micro.Position) > .001f);
        Assert.Equal(Balance.CoreHp, game.CoreHp);
        Assert.True(IsBodyClear(game, micro));
    }

    [Fact]
    public void AnUnthreatenedMicroApproachesTheCoreThroughAFlank()
    {
        var game = Combat();
        var micro = AddMicro(game, new Cell(5, 16, 15));
        var start = micro.Position;
        var core = new Vector3(16, start.Y, 16);

        game.Update(.8f);

        Assert.True(Vector3.DistanceSquared(micro.Position, core) < Vector3.DistanceSquared(start, core));
        Assert.True(Math.Abs(micro.Position.Z - start.Z) > .25f,
            "The Micro approached straight down the central lane instead of flanking.");
        Assert.True(IsBodyClear(game, micro));
    }

    [Fact]
    public void DodgeAndDashMovementCannotTunnelThroughASolidWall()
    {
        var (game, micro) = Isolated();
        for (int z = 0; z < WorldGrid.Size; z++)
            for (int y = 16; y <= 19; y++)
                Place(game, new Cell(9, y, z));
        var start = micro.Position;
        game.Projectiles.Add(Pellet(start - Vector3.UnitZ * .4f, start + Vector3.UnitZ * .4f, duration: .08f));

        for (int frame = 0; frame < 35; frame++)
        {
            TickMicro(game, micro, .05f);
            Assert.True(IsBodyClear(game, micro), $"Micro intersected the wall at {micro.Position}.");
            Assert.True(micro.Position.X + micro.BodyHalfExtents.X <= 9.001f);
        }
        Assert.Equal(2, micro.BodyHp);
    }

    [Fact]
    public void AReactiveJumpRespectsALowSolidCeiling()
    {
        var (game, micro) = Isolated();
        var start = micro.Position;
        game.Projectiles.Add(Pellet(start - Vector3.UnitX * .4f, start + Vector3.UnitX * .4f, duration: .08f));
        TickMicro(game, micro, .025f);
        Assert.True(micro.Position.Y > start.Y + .05f);
        // A crust appears over an already rising body; its actual lower face
        // is below the unimpeded jump apex, so ascent must stop at the slab.
        var ceiling = new List<ObsidianCell>();
        for (int x = 0; x < 15; x++)
            for (int z = 0; z < 15; z++)
                ceiling.Add(new(new Cell(x, 16, z), 4, CrustFace.Top, 20));
        game.Liquids.RestoreState(new LiquidState { Obsidian = ceiling });

        for (int frame = 0; frame < 30; frame++)
        {
            TickMicro(game, micro, .025f);
            Assert.True(IsBodyClear(game, micro), $"Micro intersected the ceiling at {micro.Position}.");
            Assert.True(micro.Position.Y + micro.BodyHalfExtents.Y <= 16.601f);
        }
        Assert.Equal(2, micro.BodyHp);
    }

    private static (GameSession Game, Zombie Micro) Isolated(int seed = 12345)
    {
        var game = new GameSession(seed);
        return (game, AddMicro(game, new Cell(8, 16, 8)));
    }

    private static GameSession Combat()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Waves = [new() { SpawnInterval = 1000, Groups = [new() { Size = ZombieSize.Micro }] }]
        });
        var game = new GameSession(12345, levels);
        Assert.True(game.StartLevel());
        foreach (var wave in game.Waves) wave.Spawned = wave.TotalCount;
        return game;
    }

    private static Zombie AddMicro(GameSession game, Cell cell)
    {
        var micro = new Zombie { Id = 100 + game.Zombies.Count, WaveIndex = -1, Size = ZombieSize.Micro, Attack = 1 };
        micro.Position = micro.StandAt(cell);
        game.Zombies.Add(micro);
        return micro;
    }

    private static Projectile Pellet(Vector3 from, Vector3 to, float duration = .5f, int targetId = 100) =>
        new() { Origin = from, Target = to, TargetId = targetId, Damage = 1, Kind = ShotKind.Pellet, Duration = duration };

    private static bool TickMicro(GameSession game, Zombie micro, float dt)
    {
        Invoke(game, "AdvanceMicroTimers", micro, dt);
        return (bool)Invoke(game, "TryUpdateMicro", micro, dt)!;
    }
    private static bool IsBodyClear(GameSession game, Zombie micro) => (bool)Invoke(game, "IsBodyClear", micro.Position, micro)!;
    private static void Place(GameSession game, Cell cell) => Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", cell, Material.Stone)!);
    private static object? Invoke(object target, string name, params object?[] arguments) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
