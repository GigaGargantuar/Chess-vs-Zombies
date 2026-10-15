using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloGuardTests
{
    [Fact]
    public void AnAdvancingKiloKeepsFacingTheLatestShooterWhileItsRouteStillApproachesTheCore()
    {
        var (game, kilo) = BeginAdvancing();
        var wave = game.Waves[0];
        int spawned = wave.Spawned, total = wave.TotalCount;
        long budgetSpent = wave.BudgetSpent;
        float totalHp = wave.TotalHp;
        var origin = kilo.Position;

        game.DamageZombie(kilo, 1, DamageKind.Pierce, ShotKind.Pellet, -Vector3.UnitZ);
        game.Update(.5f);

        Assert.Equal(Vector3.UnitZ, kilo.Facing);
        Assert.True(CoreDistance(kilo.Position) < CoreDistance(origin),
            "Facing a shooter stopped the Kilo's approach to the objective.");
        Assert.False(game.IsZombieRetreating(kilo));
        var intermediate = kilo.Position;

        // The shooter is now behind its advance, so facing must differ from travel.
        game.DamageZombie(kilo, 1, DamageKind.Blunt, ShotKind.Lob, Vector3.UnitX);
        game.Update(.5f);

        Assert.Equal(-Vector3.UnitX, kilo.Facing);
        Assert.True(kilo.Position.X > intermediate.X,
            "The Kilo followed its guard heading instead of its existing core route.");
        Assert.True(CoreDistance(kilo.Position) < CoreDistance(intermediate));
        Assert.Equal(kilo.MaxBodyHp - 2, kilo.BodyHp);
        Assert.Equal(spawned, wave.Spawned);
        Assert.Equal(total, wave.TotalCount);
        Assert.Equal(budgetSpent, wave.BudgetSpent);
        Assert.Equal(totalHp, wave.TotalHp);
        Assert.Equal(2, wave.DamageTaken);
        Assert.Same(kilo, Assert.Single(game.Zombies));
    }

    [Fact]
    public void ARealCoverHitTurnsTheBodyWhileOnlyTheActiveHandGuardsAndTheKiloAdvances()
    {
        var (game, kilo) = BeginAdvancing();
        kilo.FaceToward(Vector3.UnitX);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.Equal(2, game.KiloCovers.Count);
        float hp = kilo.BodyHp;
        float coverHp = game.KiloCovers.Sum(block => block.Hp);
        var lower = game.KiloCovers.Single(block => block.BlockIndex == 0);
        var inactive = game.KiloCovers.Single(block => block.BlockIndex == 1);
        var inactiveOffset = (inactive.Min + inactive.Max) * .5f - kilo.Position;
        var aim = (lower.Min + lower.Max) * .5f;
        var shooterDirection = Vector3.Normalize(Vector3.UnitX + Vector3.UnitZ * .25f);
        game.Projectiles.Add(new Projectile
        {
            Origin = aim + shooterDirection * 3,
            Target = aim,
            Damage = 1,
            Kind = ShotKind.Pellet,
            Duration = .1f
        });
        var origin = kilo.Position;

        game.Update(.5f);

        AssertVectorNear(shooterDirection, kilo.Facing);
        Assert.True(CoreDistance(kilo.Position) < CoreDistance(origin));
        Assert.False(game.IsZombieRetreating(kilo));
        Assert.Equal(hp, kilo.BodyHp);
        Assert.Equal(coverHp - 1, game.KiloCovers.Sum(block => block.Hp));
        inactive = game.KiloCovers.Single(block => block.BlockIndex == 1);
        AssertVectorNear(inactiveOffset, (inactive.Min + inactive.Max) * .5f - kilo.Position);
        Assert.Equal(lower.Hp - 1, game.KiloCovers.Single(block => block.BlockIndex == 0).Hp);
        Assert.Equal(0, game.Waves[0].DamageTaken);
    }

    [Theory]
    [InlineData(ZombieSize.Standard)]
    [InlineData(ZombieSize.Milli)]
    public void SmallerZombiesStillFaceTheirTravelDirectionAfterBeingShot(ZombieSize size)
    {
        var (game, zombie) = BeginAdvancing(size);
        var origin = zombie.Position;

        game.DamageZombie(zombie, 1, DamageKind.Pierce, ShotKind.Pellet, -Vector3.UnitZ);
        game.Update(.5f);

        var direction = zombie.Position - origin;
        direction.Y = 0;
        AssertVectorNear(Vector3.Normalize(direction), zombie.Facing);
        zombie.FaceToward(-Vector3.UnitX);
        Assert.Equal(-Vector3.UnitX, zombie.Facing);
    }

    [Theory]
    [InlineData("death")]
    [InlineData("reset")]
    public void AnAdvancingGuardReleasesItsHeadingOnDeathOrReset(string ending)
    {
        var (game, kilo) = BeginAdvancing();
        game.DamageZombie(kilo, 1, DamageKind.Pierce, ShotKind.Pellet, -Vector3.UnitZ);
        kilo.FaceToward(Vector3.UnitX);
        Assert.Equal(Vector3.UnitZ, kilo.Facing);

        if (ending == "death") game.DamageZombie(kilo, 100_000, DamageKind.Chemical);
        else game.Reset();

        kilo.FaceToward(Vector3.UnitX);
        Assert.Equal(Vector3.UnitX, kilo.Facing);
    }

    private static (GameSession Game, Zombie Zombie) BeginAdvancing(ZombieSize size = ZombieSize.Kilo)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Name = "Advancing Kilo guard",
            Waves = [new() { Groups = [new() { Kind = ZombieKind.Basic, Count = 1 }] }]
        });
        var game = new GameSession(12345, levels);
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var zombie = new Zombie
        {
            Id = -1, WaveIndex = 0, Size = size,
            Kind = ZombieKind.Basic, Attack = Balance.BaseAttack
        };
        zombie.Position = zombie.StandAt(new Cell(6, 16, 15));
        zombie.FaceToward(Vector3.UnitX);
        game.Zombies.Add(zombie);
        return (game, zombie);
    }

    private static float CoreDistance(Vector3 position) =>
        Vector2.DistanceSquared(new Vector2(position.X, position.Z), new Vector2(16));

    private static void AssertVectorNear(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, .00001f);

    private static object? Invoke(GameSession game, string name, params object[] arguments) => typeof(GameSession)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, arguments);
}
