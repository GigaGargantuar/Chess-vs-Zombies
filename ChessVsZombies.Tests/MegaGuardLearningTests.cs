using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MegaGuardLearningTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    public void UntrainedMegaKeepsItsTwoRealHandsOnTheFacingBodyFace(int x, int z)
    {
        var facing = new Vector3(x, 0, z);
        var (game, mega) = Create(facing);
        Invoke(game, "UpdateMegaHeldPoses");
        var before = game.MegaWeapons.ToArray();
        var selected = before.Single(block => block.BlockIndex == 0);
        var center = Center(selected);
        float bodyBefore = mega.BodyHp;
        AddPellet(game, center + facing * 5, center - facing * 3, 7);

        Invoke(game, "UpdateProjectiles", .4f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(bodyBefore, mega.BodyHp);
        Assert.Equal(selected.Hp - 7, game.MegaWeapons.Single(block => block.BlockIndex == selected.BlockIndex).Hp);
        Assert.All(game.MegaWeapons, block => Assert.Equal(1, Vector3.Dot(Center(block) - mega.Position, facing), 5));
        AssertPhysicalHands(game, mega);
    }

    [Fact]
    public void GuardFacingAveragesObservedDirectionsWithoutWeightingProjectileSpeed()
    {
        var (game, mega) = Create(Vector3.UnitX);
        for (int hit = 0; hit < GameSession.MegaGuardLearningHits - 1; hit++)
            ObserveBodyHit(game, mega, -Vector3.UnitX * 10000);
        ObserveBodyHit(game, mega, -Vector3.UnitZ * .001f);

        var expected = Vector3.Normalize(new Vector3(GameSession.MegaGuardLearningHits - 1, 0, 1));
        Assert.True(Vector3.Dot(expected, mega.Facing) > .99999f);
        mega.FaceToward(-Vector3.UnitZ);
        Assert.True(Vector3.Dot(expected, mega.Facing) > .99999f);
    }

    [Fact]
    public void FewerThanFourObservedHitsDoesNotPredictAnOffCenterPellet()
    {
        var (game, mega) = Create(Vector3.UnitX);
        Train(game, mega, GameSession.MegaGuardLearningHits - 1);
        float bodyBefore = mega.BodyHp;
        float blockHp = game.MegaWeapons.Sum(block => block.Hp);
        AddEdgeVolley(game, mega, 1);

        Invoke(game, "UpdateProjectiles", .4f);

        Assert.Equal(bodyBefore - 2, mega.BodyHp);
        Assert.Equal(blockHp, game.MegaWeapons.Sum(block => block.Hp));
    }

    [Fact]
    public void LearnedChunkPlacementBlocksTheWholeOffCenterVolleyRatherThanOnlyTheLastBullet()
    {
        var (coldGame, coldMega) = Create(Vector3.UnitX);
        var (game, mega) = Create(Vector3.UnitX);
        Train(game, mega);
        float bodyBefore = mega.BodyHp;
        float coldBodyBefore = coldMega.BodyHp;
        float coverHp = game.MegaWeapons.Sum(block => block.Hp);
        AddEdgeVolley(coldGame, coldMega, 3);
        AddEdgeVolley(game, mega, 3);

        Invoke(coldGame, "UpdateProjectiles", .4f);
        Invoke(game, "UpdateProjectiles", .4f);

        Assert.Equal(coldBodyBefore - 6, coldMega.BodyHp);
        Assert.Equal(bodyBefore, mega.BodyHp);
        Assert.Equal(coverHp - 6, game.MegaWeapons.Sum(block => block.Hp));
        Assert.Empty(game.Projectiles);
        AssertPhysicalHands(game, mega);
    }

    [Fact]
    public void ActualHitsOnHeldBlocksTeachPredictionWithoutRequiringBodyDamage()
    {
        var (game, mega) = Create(Vector3.UnitX);
        float bodyBefore = mega.BodyHp;
        float coverHp = game.MegaWeapons.Sum(block => block.Hp);
        for (int hit = 0; hit < GameSession.MegaGuardLearningHits; hit++)
        {
            var aim = Center(game.MegaWeapons.Single(block => block.BlockIndex == 0));
            AddPellet(game, aim + Vector3.UnitX * 5, aim - Vector3.UnitX * 3, 1);
            Invoke(game, "UpdateProjectiles", .4f);
        }
        Assert.Equal(bodyBefore, mega.BodyHp);
        AddEdgeVolley(game, mega, 3);

        Invoke(game, "UpdateProjectiles", .4f);

        Assert.Equal(bodyBefore, mega.BodyHp);
        Assert.Equal(coverHp - GameSession.MegaGuardLearningHits - 6, game.MegaWeapons.Sum(block => block.Hp));
        AssertPhysicalHands(game, mega);
    }

    [Fact]
    public void LearnedChunkCanGuardTheTopFaceAgainstAnIncomingOverheadVolley()
    {
        var (game, mega) = Create(Vector3.UnitX);
        Train(game, mega);
        float bodyBefore = mega.BodyHp;
        float coverHp = game.MegaWeapons.Sum(block => block.Hp);
        for (int shot = 0; shot < 3; shot++)
        {
            // These rays are near the center of the top face. They cannot
            // graze a raised side guard at one of the body's outer corners.
            var aim = mega.Position + new Vector3(.15f + shot * .05f, 0, .2f + shot * .025f);
            AddPellet(game, aim + Vector3.UnitY * 6, aim - Vector3.UnitY * 3, 2);
        }

        Invoke(game, "UpdateProjectiles", .4f);

        Assert.Equal(bodyBefore, mega.BodyHp);
        Assert.Equal(coverHp - 6, game.MegaWeapons.Sum(block => block.Hp));
        AssertPhysicalHands(game, mega);
        Assert.All(game.MegaWeapons, block => Assert.Equal(mega.Position.Y + mega.BodyHalfExtents.Y, block.Max.Y));
    }

    [Fact]
    public void OneGuardPosePrioritizesThreeReachableBulletsOverTwoFromTheLearnedDirection()
    {
        var (game, mega) = Create(Vector3.UnitZ);
        for (int hit = 0; hit < GameSession.MegaGuardLearningHits; hit++)
            ObserveBodyHit(game, mega, -Vector3.UnitZ);
        float bodyBefore = mega.BodyHp;
        float coverHp = game.MegaWeapons.Sum(block => block.Hp);
        // Mid-height rays near the centers of perpendicular body faces cannot
        // all be intercepted by the same corner or a top-facing chunk.
        for (int shot = 0; shot < 3; shot++)
        {
            var aim = mega.Position + new Vector3(0, .1f, .1f + shot * .05f);
            AddPellet(game, aim + Vector3.UnitX * 6, aim - Vector3.UnitX * 3, 2);
        }
        for (int shot = 0; shot < 2; shot++)
        {
            var aim = mega.Position + new Vector3(.1f + shot * .05f, .1f, 0);
            AddPellet(game, aim + Vector3.UnitZ * 6, aim - Vector3.UnitZ * 3, 2);
        }

        Invoke(game, "UpdateProjectiles", .4f);

        Assert.Empty(game.Projectiles);
        Assert.Equal(bodyBefore - 4, mega.BodyHp);
        Assert.Equal(coverHp - 6, game.MegaWeapons.Sum(block => block.Hp));
        AssertPhysicalHands(game, mega);
        Assert.All(game.MegaWeapons, block => Assert.Equal(mega.Position.X + mega.BodyHalfExtents.X, block.Max.X));
    }

    [Fact]
    public void ShotsStoppedByTerrainDoNotOutvoteTheVolleyThatCanReachTheMega()
    {
        var (game, mega) = Create(Vector3.UnitX);
        Train(game, mega);
        float bodyBefore = mega.BodyHp;
        float coverHp = game.MegaWeapons.Sum(block => block.Hp);
        var wall = new Cell(7, 18, 12);
        Assert.True((bool)Invoke(game.World, "AddBarrier", wall, Material.Stone, (float?)500)!);
        AddEdgeVolley(game, mega, 3);
        for (int shot = 0; shot < 12; shot++)
        {
            var aim = mega.Position + new Vector3(1.2f, 1.2f, 0);
            AddPellet(game, aim + Vector3.UnitZ * 6, aim - Vector3.UnitZ * 3, 1);
        }

        Invoke(game, "UpdateProjectiles", .4f);

        Assert.Equal(bodyBefore, mega.BodyHp);
        Assert.Equal(coverHp - 6, game.MegaWeapons.Sum(block => block.Hp));
        Assert.Equal(488, game.World.GetBarrier(wall)!.Hp);
        AssertPhysicalHands(game, mega);
    }

    [Fact]
    public void LearnedCoverCannotBlockDuringBucketWork()
    {
        var (game, mega) = Create(Vector3.UnitX);
        Train(game, mega);
        Invoke(game, "BeginKiloBucketAction", mega);
        float bodyBefore = mega.BodyHp;
        var before = game.MegaWeapons.ToArray();
        AddEdgeVolley(game, mega, 3);

        Invoke(game, "UpdateProjectiles", .4f);

        Assert.Equal(bodyBefore - 6, mega.BodyHp);
        Assert.All(game.MegaWeapons, block => Assert.False(block.IsActive));
        Assert.Equal(before.Select(block => block.Hp), game.MegaWeapons.Select(block => block.Hp));
        Assert.Empty(game.MegaProjectiles);
    }

    [Fact]
    public void ChunkGuardsDuringThrowPreparationThenLaunchesTheSameDamagedBlocks()
    {
        var (game, mega) = Create(Vector3.UnitX);
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 16, 9)));

        Invoke(game, "UpdateMegas", GameSession.MegaGuardWindupSeconds * .25f);

        Assert.Equal(4, game.MegaWeapons.Count);
        Assert.Empty(game.MegaProjectiles);
        Assert.All(game.MegaWeapons, block => Assert.True(block.IsActive));
        var first = game.MegaWeapons.Single(block => block.BlockIndex == 0);
        float bodyBefore = mega.BodyHp;
        var aim = Center(first);
        AddPellet(game, aim + Vector3.UnitX * 5, aim - Vector3.UnitX * 3, 7);
        Invoke(game, "UpdateProjectiles", .4f);
        Assert.Equal(bodyBefore, mega.BodyHp);
        Assert.Equal(first.Hp - 7, game.MegaWeapons.Single(block => block.BlockIndex == first.BlockIndex).Hp);
        var guarded = game.MegaWeapons.OrderBy(block => block.BlockIndex)
            .Select(block => (block.BlockIndex, block.Material, block.Hp, block.WasBarrier)).ToArray();

        for (int tick = 0; tick < 20 && game.MegaProjectiles.Count == 0; tick++)
            Invoke(game, "UpdateMegas", .1f);

        Assert.Empty(game.MegaWeapons);
        Assert.Equal(guarded, game.MegaProjectiles.OrderBy(block => block.BlockIndex)
            .Select(block => (block.BlockIndex, block.Material, block.Hp, block.WasBarrier)).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetOrDeathCannotGiveAReusedZombieIdThePreviousMegasLearning(bool reset)
    {
        var (game, mega) = Create(Vector3.UnitX);
        Train(game, mega);
        if (reset) game.Reset();
        else
        {
            game.DamageZombie(mega, 10000, DamageKind.Pierce, ShotKind.Pellet, -Vector3.UnitX);
            game.Zombies.Remove(mega);
        }
        var replacement = SeedMega(game, mega.Id, Vector3.UnitX);
        float bodyBefore = replacement.BodyHp;
        float coverHp = game.MegaWeapons.Sum(block => block.Hp);
        AddEdgeVolley(game, replacement, 1);

        Invoke(game, "UpdateProjectiles", .4f);

        Assert.Equal(bodyBefore - 2, replacement.BodyHp);
        Assert.Equal(coverHp, game.MegaWeapons.Sum(block => block.Hp));
    }

    private static (GameSession Game, Zombie Mega) Create(Vector3 facing)
    {
        var game = new GameSession();
        return (game, SeedMega(game, 100, facing));
    }

    private static Zombie SeedMega(GameSession game, int id, Vector3 facing)
    {
        var mega = new Zombie { Id = id, Size = ZombieSize.Mega, Attack = 9, WaveIndex = 0 };
        mega.Position = mega.StandAt(new Cell(5, 16, 8));
        mega.FaceToward(facing);
        typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(mega, 0);
        var type = typeof(GameSession).GetNestedType("HeldKiloBlock", BindingFlags.NonPublic)!;
        var blocks = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        var side = new Vector3(facing.Z, 0, -facing.X);
        for (int index = 0; index < 4; index++)
        {
            var block = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [index, Material.Dirt, 100f, false], null)!;
            var offset = facing + side * (index % 2 == 0 ? -.5f : .5f) +
                Vector3.UnitY * (index / 2 == 0 ? -.5f : .5f);
            type.GetProperty("Offset")!.SetValue(block, offset);
            blocks.Add(block);
        }
        var owned = (IDictionary)typeof(GameSession).GetField("_kiloBlocks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;
        owned[id] = blocks;
        game.Zombies.Add(mega);
        return mega;
    }

    private static void Train(GameSession game, Zombie mega, int hits = GameSession.MegaGuardLearningHits)
    {
        for (int hit = 0; hit < hits; hit++) ObserveBodyHit(game, mega, -Vector3.UnitX);
    }

    private static void ObserveBodyHit(GameSession game, Zombie mega, Vector3 incoming) =>
        game.DamageZombie(mega, 1, DamageKind.Pierce, ShotKind.Pellet, incoming);

    private static void AddEdgeVolley(GameSession game, Zombie mega, int count)
    {
        for (int shot = 0; shot < count; shot++)
        {
            var aim = mega.Position + new Vector3(0, 1.1f + shot * .05f, 1.15f + shot * .025f);
            // TargetId is deliberately absent: physical trajectories, rather
            // than a target lock, determine which bullets threaten the Mega.
            AddPellet(game, aim + Vector3.UnitX * 6, aim - Vector3.UnitX * 3, 2);
        }
    }

    private static void AddPellet(GameSession game, Vector3 origin, Vector3 target, float damage) =>
        game.Projectiles.Add(new Projectile { Origin = origin, Target = target, Kind = ShotKind.Pellet, Damage = damage, Duration = .45f });

    private static Vector3 Center(MegaWeaponBlock block) => (block.Min + block.Max) * .5f;

    private static void AssertPhysicalHands(GameSession game, Zombie mega)
    {
        var blocks = game.MegaWeapons.ToArray();
        Assert.Equal(4, blocks.Length);
        foreach (var block in blocks)
        {
            Assert.Equal(Vector3.One, block.Max - block.Min);
            Assert.True(Vector3.Min(block.Min - (mega.Position - mega.BodyHalfExtents), Vector3.Zero).LengthSquared() < .000001f);
            Assert.True(Vector3.Max(block.Max - (mega.Position + mega.BodyHalfExtents), Vector3.Zero).LengthSquared() < .000001f);
        }
        foreach (var hand in blocks.GroupBy(block => block.HandIndex))
        {
            var pair = hand.ToArray();
            Assert.Equal(2, pair.Length);
            var separation = Vector3.Abs(Center(pair[0]) - Center(pair[1]));
            Assert.Equal(1, separation.X + separation.Y + separation.Z, 5);
            Assert.Equal(1, separation.LengthSquared(), 5);
        }
        for (int first = 0; first < blocks.Length; first++)
            for (int second = first + 1; second < blocks.Length; second++)
        {
            var a = blocks[first];
            var b = blocks[second];
            Assert.False(a.Min.X < b.Max.X && a.Max.X > b.Min.X && a.Min.Y < b.Max.Y &&
                         a.Max.Y > b.Min.Y && a.Min.Z < b.Max.Z && a.Max.Z > b.Min.Z);
        }
    }

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
