using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloIndependentHandsTests
{
    [Fact]
    public void TheTwoHandsHoldSeparateSideBySideCubesInsteadOfAVerticalStack()
    {
        var (game, kilo) = EquippedKilo();
        var covers = Covers(game, kilo);
        var first = Center(covers[0]);
        var second = Center(covers[1]);

        Assert.Equal(first.Y, second.Y, 5);
        Assert.True(Math.Abs(first.X - second.X) >= .99999f,
            "A forward-facing Kilo should have one separate cube in each hand.");
        AssertGeometry(game, kilo);
        Assert.Equal(0, kilo.CarriedBlocks);
    }

    [Fact]
    public void ChangingTheBodyHeadingDoesNotRotateBothHeldBlocksAsOneWall()
    {
        var (game, kilo) = EquippedKilo();
        var before = Offsets(game, kilo);

        kilo.FaceToward(-Vector3.UnitX);
        Invoke(game, "UpdateKilos", .05f);

        Assert.Equal(-Vector3.UnitX, kilo.Facing);
        foreach (var cover in Covers(game, kilo))
            AssertNear(before[cover.BlockIndex], Center(cover) - kilo.Position);
        AssertGeometry(game, kilo);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(1f)]
    public void AnIncomingSideBulletMovesAndDamagesOnlyItsNearestHand(float side)
    {
        var (game, kilo) = EquippedKilo();
        var before = Covers(game, kilo);
        var offsets = Offsets(game, kilo);
        var aim = kilo.Position + Vector3.UnitY * .8f;
        var origin = aim + Vector3.UnitX * (side * 4);
        var entry = aim + Vector3.UnitX * side;
        var selected = before.OrderBy(cover => DistanceSquared(entry, cover)).First();
        var inactive = Assert.Single(before, cover => cover.BlockIndex != selected.BlockIndex);
        float hp = kilo.BodyHp;

        Fire(game, origin, aim, 7);

        Assert.Empty(game.Projectiles);
        Assert.Equal(hp, kilo.BodyHp);
        Assert.Equal(selected.Hp - 7, Cover(game, kilo, selected.BlockIndex).Hp);
        Assert.Equal(inactive.Hp, Cover(game, kilo, inactive.BlockIndex).Hp);
        AssertNear(offsets[inactive.BlockIndex], Center(Cover(game, kilo, inactive.BlockIndex)) - kilo.Position);
        Assert.True(Vector3.Distance(offsets[selected.BlockIndex],
            Center(Cover(game, kilo, selected.BlockIndex)) - kilo.Position) > .01f,
            "The nearest hand did not move to intercept a shot above its resting cube.");
        AssertGeometry(game, kilo);
    }

    [Fact]
    public void SeparateShotsFromOppositeSidesUseBothHandsWithoutMovingTheInactiveHand()
    {
        var (game, kilo) = EquippedKilo();
        var initial = Covers(game, kilo);
        var aim = kilo.Position + Vector3.UnitY * .8f;

        Fire(game, aim - Vector3.UnitX * 4, aim, 3);
        var afterLeft = Covers(game, kilo);
        var left = Assert.Single(afterLeft, cover => cover.Hp == initial.Single(before => before.BlockIndex == cover.BlockIndex).Hp - 3);
        var inactive = Assert.Single(afterLeft, cover => cover.BlockIndex != left.BlockIndex);
        var leftOffset = Center(left) - kilo.Position;

        Fire(game, aim + Vector3.UnitX * 4, aim, 5);

        Assert.Equal(left.Hp, Cover(game, kilo, left.BlockIndex).Hp);
        AssertNear(leftOffset, Center(Cover(game, kilo, left.BlockIndex)) - kilo.Position);
        Assert.Equal(inactive.Hp - 5, Cover(game, kilo, inactive.BlockIndex).Hp);
        Assert.Equal(kilo.MaxBodyHp, kilo.BodyHp);
        AssertGeometry(game, kilo);
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(-1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, 0f, -1f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(-1f, 1f, -1f)]
    public void InterceptingShotsFromDifferentAxesKeepsBothCubesInsideTheBodyAndApart(float x, float y, float z)
    {
        var (game, kilo) = EquippedKilo();
        var direction = Vector3.Normalize(new Vector3(x, y, z));
        // Use a point inside the nearest hand's lateral half, avoiding an
        // ambiguous shared face between the two full cubes on vertical fire.
        var aimOffset = y == 1 && x == 0 && z == 0 ? new Vector3(-.6f, 0, .6f) :
            x == 0 && y == 0 ? new Vector3(-.6f, .8f, 0) :
            y == 0 ? new Vector3(0, .8f, 0) : Vector3.Zero;
        var aim = kilo.Position + aimOffset;
        var origin = aim + direction * 4;
        float previousHp = Covers(game, kilo).Sum(cover => cover.Hp);

        Fire(game, origin, aim, 1);

        Assert.Empty(game.Projectiles);
        Assert.Equal(kilo.MaxBodyHp, kilo.BodyHp);
        Assert.Equal(previousHp - 1, Covers(game, kilo).Sum(cover => cover.Hp));
        AssertGeometry(game, kilo);
    }

    [Fact]
    public void TheHandChosenAtContactDoesNotDependOnTheProjectileUpdateSize()
    {
        var (wholeGame, wholeKilo) = EquippedKilo();
        var (splitGame, splitKilo) = EquippedKilo();
        var originOffset = new Vector3(-.5f, .8f, -5);
        var aimOffset = new Vector3(.8f, .8f, 0);
        // At launch this diagonal shot is nearer the left hand, but it enters
        // the body's rear face in the right hand's half.
        Fire(wholeGame, wholeKilo.Position + originOffset, wholeKilo.Position + aimOffset, 1);
        splitGame.Projectiles.Add(new Projectile
        {
            Origin = splitKilo.Position + originOffset,
            Target = splitKilo.Position + aimOffset,
            Damage = 1, Kind = ShotKind.Pellet, Duration = .1f
        });
        var offsetsBeforeArrival = Offsets(splitGame, splitKilo);
        Invoke(splitGame, "UpdateProjectiles", .05f);
        Assert.Single(splitGame.Projectiles);
        foreach (var cover in Covers(splitGame, splitKilo))
            AssertNear(offsetsBeforeArrival[cover.BlockIndex], Center(cover) - splitKilo.Position);
        Invoke(splitGame, "UpdateProjectiles", .025f);
        Invoke(splitGame, "UpdateProjectiles", .025f);

        Assert.Empty(splitGame.Projectiles);
        foreach (var cover in Covers(wholeGame, wholeKilo))
        {
            var split = Cover(splitGame, splitKilo, cover.BlockIndex);
            Assert.Equal(cover.Hp, split.Hp);
            AssertNear(Center(cover) - wholeKilo.Position, Center(split) - splitKilo.Position);
        }
        Assert.Equal(wholeKilo.MaxBodyHp, wholeKilo.BodyHp);
        Assert.Equal(splitKilo.MaxBodyHp, splitKilo.BodyHp);
        AssertGeometry(wholeGame, wholeKilo);
        AssertGeometry(splitGame, splitKilo);
    }

    [Fact]
    public void DestroyedHandsCannotAbsorbLaterBulletsOrRecreateTheirBlocks()
    {
        var (game, kilo) = EquippedKilo();
        var aim = kilo.Position + Vector3.UnitY * .8f;

        Fire(game, aim - Vector3.UnitX * 4, aim, 1_000);
        Assert.Single(Covers(game, kilo));
        Fire(game, aim + Vector3.UnitX * 4, aim, 1_000);
        Assert.Empty(Covers(game, kilo));
        float hp = kilo.BodyHp;

        Fire(game, aim - Vector3.UnitX * 4, aim, 2);

        Assert.Equal(hp - 2, kilo.BodyHp);
        Assert.Empty(Covers(game, kilo));
        Assert.Empty(game.Projectiles);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, kilo.CarriedBlocks);
    }

    [Fact]
    public void DeathDropsEachHandAtItsActualIndependentGuardPositionExactlyOnce()
    {
        var (game, kilo) = EquippedKilo();
        var aim = kilo.Position + Vector3.UnitY * .8f;
        Fire(game, aim - Vector3.UnitX * 4, aim, 7);
        var heldAtDeath = Covers(game, kilo);
        kilo.Hp = 0;

        Invoke(game, "HandleKiloDeath", kilo);
        Invoke(game, "HandleKiloDeath", kilo);

        Assert.Empty(game.KiloCovers);
        Assert.Equal(2, game.PendingBlockDrops.Count);
        foreach (var held in heldAtDeath)
        {
            var center = Center(held);
            var cell = new Cell((int)MathF.Floor(center.X), (int)MathF.Floor(center.Y), (int)MathF.Floor(center.Z));
            var drop = Assert.Single(game.PendingBlockDrops,
                drop => drop.Cell == cell && drop.Hp == held.Hp);
            Assert.Equal(held.Material, drop.Content);
            Assert.Equal(held.WasBarrier, drop.WasBarrier);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BoundaryResupplyRefillsTheFreeHandRegardlessOfWhichHandSurvived(int destroyedHand)
    {
        var (game, kilo) = EquippedKilo();
        int survivorIndex = 1 - destroyedHand;
        Invoke(game, "DamageKiloCover", kilo.Id, destroyedHand, 1_000f);
        Invoke(game, "DamageKiloCover", kilo.Id, survivorIndex, 13f);
        var survivor = Cover(game, kilo, survivorIndex);
        kilo.Position = kilo.StandAt(new Cell(0, 16, 5));
        MarkRetreating(game, kilo);

        Assert.True((bool)Invoke(game, "TryCompleteRetreat", kilo, 1.1f)!);

        Assert.False(game.IsZombieRetreating(kilo));
        Assert.Equal(1, kilo.CarriedBlocks);
        Assert.Equal(survivor.Hp, Assert.Single(Covers(game, kilo)).Hp);
        Invoke(game, "UpdateKilos", 2f);
        kilo.Cooldown = 0;
        Invoke(game, "TryKiloAction", kilo, .05f);

        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Equal(2, Covers(game, kilo).Length);
        Assert.Equal(survivor.Hp, Cover(game, kilo, survivorIndex).Hp);
        Assert.Equal(survivor.Material, Cover(game, kilo, survivorIndex).Material);
        Assert.Equal(survivor.WasBarrier, Cover(game, kilo, survivorIndex).WasBarrier);
        Assert.Equal(100, Cover(game, kilo, destroyedHand).Hp);
        Assert.All(Covers(game, kilo), cover => Assert.Equal(Material.Dirt, cover.Material));
        AssertGeometry(game, kilo);
    }

    [Fact]
    public void LosingBothHandsResuppliesTwoRealBlocksAndDoesNotRequireFurtherExcavation()
    {
        var (game, kilo) = EquippedKilo();
        Invoke(game, "DamageKiloCover", kilo.Id, 0, 1_000f);
        Invoke(game, "DamageKiloCover", kilo.Id, 1, 1_000f);
        kilo.Position = kilo.StandAt(new Cell(0, 16, 5));
        MarkRetreating(game, kilo);

        Assert.True((bool)Invoke(game, "TryCompleteRetreat", kilo, 1.1f)!);

        Assert.Equal(2, kilo.CarriedBlocks);
        Assert.Empty(Covers(game, kilo));
        var projection = Math.Max(Math.Abs(kilo.Facing.X), Math.Abs(kilo.Facing.Z));
        var front = kilo.Position + kilo.Facing / projection * 1.5f;
        var source = new Cell((int)MathF.Floor(front.X), 15, (int)MathF.Floor(front.Z));
        Assert.Equal(Material.Dirt, game.World.GetMaterial(source));
        int worldRevision = game.World.Revision;
        Invoke(game, "UpdateKilos", 2f);
        kilo.Cooldown = 0;
        Invoke(game, "TryKiloAction", kilo, .05f);

        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Equal(2, Covers(game, kilo).Length);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(source));
        Assert.Equal(worldRevision, game.World.Revision);
        Assert.All(Covers(game, kilo), cover => Assert.Equal(100, cover.Hp));
        AssertGeometry(game, kilo);
    }

    private static void MarkRetreating(GameSession game, Zombie kilo)
    {
        var states = (System.Collections.IDictionary)typeof(GameSession)
            .GetField("_retreating", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var stateType = typeof(GameSession).GetNestedType("RetreatState", BindingFlags.NonPublic)!;
        states[kilo.Id] = Activator.CreateInstance(stateType, nonPublic: true);
    }

    private static (GameSession Game, Zombie Kilo) EquippedKilo()
    {
        var game = new GameSession();
        var kilo = new Zombie { Id = 100, WaveIndex = -1, Size = ZombieSize.Kilo, Kind = ZombieKind.Basic };
        kilo.Position = kilo.StandAt(new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitZ);
        game.Zombies.Add(kilo);
        Invoke(game, "TryKiloAction", kilo, .05f);
        Assert.Equal(2, Covers(game, kilo).Length);
        return (game, kilo);
    }

    private static void Fire(GameSession game, Vector3 origin, Vector3 target, float damage)
    {
        game.Projectiles.Add(new Projectile
        {
            Origin = origin, Target = target, Damage = damage, Kind = ShotKind.Pellet, Duration = .1f
        });
        Invoke(game, "UpdateProjectiles", .1f);
    }

    private static KiloCover[] Covers(GameSession game, Zombie kilo) =>
        game.KiloCovers.Where(cover => cover.CarrierId == kilo.Id).OrderBy(cover => cover.BlockIndex).ToArray();

    private static KiloCover Cover(GameSession game, Zombie kilo, int blockIndex) =>
        Assert.Single(Covers(game, kilo), cover => cover.BlockIndex == blockIndex);

    private static Dictionary<int, Vector3> Offsets(GameSession game, Zombie kilo) =>
        Covers(game, kilo).ToDictionary(cover => cover.BlockIndex, cover => Center(cover) - kilo.Position);

    private static Vector3 Center(KiloCover cover) => (cover.Min + cover.Max) * .5f;

    private static float DistanceSquared(Vector3 point, KiloCover cover) =>
        Vector3.DistanceSquared(point, Vector3.Clamp(point, cover.Min, cover.Max));

    private static void AssertGeometry(GameSession game, Zombie kilo)
    {
        var covers = Covers(game, kilo);
        var min = kilo.Position - Vector3.One;
        var max = kilo.Position + Vector3.One;
        foreach (var cover in covers)
        {
            AssertNear(Vector3.One, cover.Max - cover.Min);
            Assert.True(cover.Min.X >= min.X - .00001f && cover.Max.X <= max.X + .00001f);
            Assert.True(cover.Min.Y >= min.Y - .00001f && cover.Max.Y <= max.Y + .00001f);
            Assert.True(cover.Min.Z >= min.Z - .00001f && cover.Max.Z <= max.Z + .00001f);
        }
        if (covers.Length == 2)
        {
            var overlap = Vector3.Min(covers[0].Max, covers[1].Max) - Vector3.Max(covers[0].Min, covers[1].Min);
            Assert.True(overlap.X <= .00001f || overlap.Y <= .00001f || overlap.Z <= .00001f,
                "The independently held cubes occupy overlapping volume.");
        }
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, .00001f);

    private static object? Invoke(GameSession game, string method, params object[] arguments) =>
        typeof(GameSession).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, arguments);
}
