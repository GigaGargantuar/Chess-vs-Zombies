using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MegaGuardMovementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LearnedMegaKeepsAdvancingAcrossCellsWhileItsRealBlocksInterceptSideFire(bool naturalChunk)
    {
        var (game, mega) = BeginAdvancing(naturalChunk);
        var origin = mega.Position;
        var visited = new HashSet<Cell> { mega.Cell };
        float initialBodyHp = mega.BodyHp;
        float initialCoverHp = game.MegaWeapons.Sum(block => block.Hp);

        // Real contacts on the held chunk teach the guard. The side shooter is
        // perpendicular to the core route, so facing it must not change travel.
        for (int hit = 0; hit < GameSession.MegaGuardLearningHits; hit++)
        {
            FireAtHeldBlock(game);
            game.Update(.15f);
        }
        // Before it has seen the first hit, ordinary travel may turn an
        // untrained guard away. Learning conserves those real contacts whether
        // they touched the body or its chunk; trained volleys must hit cover.
        float trainedBodyHp = mega.BodyHp;
        float trainedCoverHp = game.MegaWeapons.Sum(block => block.Hp);
        float trainingWaveDamage = game.Waves[0].DamageTaken;
        Assert.Equal(GameSession.MegaGuardLearningHits,
            initialBodyHp - trainedBodyHp + initialCoverHp - trainedCoverHp);

        for (int volley = 0; volley < 32; volley++)
        {
            FireAtHeldBlock(game);
            game.Update(.25f);
            visited.Add(mega.Cell);

            Assert.Same(mega, Assert.Single(game.Zombies));
            Assert.False(game.IsZombieHiding(mega));
            Assert.False(game.IsZombieRetreating(mega));
            Assert.InRange(Vector3.Distance(Vector3.UnitZ, mega.Facing), 0, .00001f);
            AssertContainedHands(game, mega);
        }

        Assert.True(mega.Position.X > origin.X + 3,
            $"The Mega guarded without advancing far enough: {origin} -> {mega.Position}.");
        Assert.True(CoreDistance(mega.Position) < CoreDistance(origin) - 3);
        Assert.True(visited.Count >= 4, "Guarding never completed several physical route steps.");
        Assert.Equal(trainedBodyHp, mega.BodyHp);
        Assert.Equal(trainedCoverHp - 32, game.MegaWeapons.Sum(block => block.Hp));
        Assert.Empty(game.Projectiles);
        Assert.Empty(game.MegaProjectiles);
        Assert.Equal(trainingWaveDamage, game.Waves[0].DamageTaken);
    }

    [Fact]
    public void ThrowWindupDoesNotRootAGuardingMegaBeforeItsChunkIsLaunched()
    {
        var (game, mega) = BeginAdvancing();
        // This nearby tower is outside the guard's path and cannot melee it.
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 16, 9)));
        for (int hit = 0; hit < GameSession.MegaGuardLearningHits; hit++)
            game.DamageZombie(mega, 1, DamageKind.Pierce, ShotKind.Pellet, -Vector3.UnitZ);
        var origin = mega.Position;
        float bodyHp = mega.BodyHp;
        float coverHp = game.MegaWeapons.Sum(block => block.Hp);
        FireAtHeldBlock(game);

        game.Update(GameSession.MegaGuardWindupSeconds * .6f);

        Assert.True(mega.Position.X > origin.X + .15f);
        Assert.True(CoreDistance(mega.Position) < CoreDistance(origin));
        Assert.InRange(Vector3.Distance(Vector3.UnitZ, mega.Facing), 0, .00001f);
        Assert.False(game.IsZombieHiding(mega));
        Assert.Equal(bodyHp, mega.BodyHp);
        Assert.Equal(coverHp - 1, game.MegaWeapons.Sum(block => block.Hp));
        Assert.Empty(game.MegaProjectiles);
        AssertContainedHands(game, mega);
    }

    [Fact]
    public void AdvancingGuardStillRespectsTheWholeBodyTerrainCollision()
    {
        var (game, mega) = BeginAdvancing();
        // A wall taller than its step height and spanning the map leaves no
        // route around. Advancing while guarding must breach it, never clip it.
        for (int z = 0; z < WorldGrid.Size; z++)
            for (int y = 16; y <= 20; y++)
                Assert.True((bool)Invoke(game.World, "AddBarrier", new Cell(9, y, z),
                    Material.Stone, (float?)Balance.StoneBlockHp)!);
        for (int hit = 0; hit < GameSession.MegaGuardLearningHits; hit++)
            game.DamageZombie(mega, 1, DamageKind.Pierce, ShotKind.Pellet, -Vector3.UnitZ);
        float bodyHp = mega.BodyHp;

        FireAtHeldBlock(game);
        game.Update(1);

        Assert.True(mega.Position.X + mega.BodyHalfExtents.X <= 9.00001f);
        Assert.Equal(Material.Stone, game.World.GetBarrier(new Cell(9, 16, 15))!.Content);
        Assert.Equal(bodyHp, mega.BodyHp);
        Assert.False(game.IsZombieHiding(mega));
        Assert.False(game.IsZombieRetreating(mega));
        AssertContainedHands(game, mega);
    }

    private static (GameSession Game, Zombie Mega) BeginAdvancing(bool naturalChunk = false)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Name = "Advancing Mega guard",
            Waves = [new() { Groups = [new() { Kind = ZombieKind.Basic, Count = 1 }] }]
        });
        var game = new GameSession(12345, levels);
        Assert.True(game.StartLevel());
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        var mega = new Zombie { Id = 100, WaveIndex = 0, Size = ZombieSize.Mega, Attack = 9 };
        mega.Position = mega.StandAt(new Cell(6, 16, 15));
        mega.FaceToward(Vector3.UnitZ);
        game.Zombies.Add(mega);

        if (naturalChunk)
        {
            int terrainBefore = TerrainCount(game);
            game.Update(.05f);
            Assert.Equal(4, game.MegaWeapons.Count);
            Assert.Equal(terrainBefore - 4, TerrainCount(game));
            Assert.Equal(1, mega.CarriedBlocks);
            return (game, mega);
        }
        typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(mega, 0);

        // Seed actual finite held blocks without excavating a hole in the test
        // route. All subsequent movement, targeting and damage use public Update.
        var heldType = typeof(GameSession).GetNestedType("HeldKiloBlock", BindingFlags.NonPublic)!;
        var blocks = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(heldType))!;
        for (int index = 0; index < 4; index++)
        {
            var block = Activator.CreateInstance(heldType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [index, Material.Dirt, (float)Balance.DirtBlockHp, false], null)!;
            heldType.GetProperty("Offset")!.SetValue(block,
                Vector3.UnitZ + Vector3.UnitX * (index % 2 == 0 ? -.5f : .5f) +
                Vector3.UnitY * (index / 2 == 0 ? -.5f : .5f));
            blocks.Add(block);
        }
        var owned = (IDictionary)typeof(GameSession)
            .GetField("_kiloBlocks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;
        owned[mega.Id] = blocks;
        return (game, mega);
    }

    private static void FireAtHeldBlock(GameSession game)
    {
        var block = game.MegaWeapons.Single(block => block.BlockIndex == 0);
        var aim = (block.Min + block.Max) * .5f;
        game.Projectiles.Add(new Projectile
        {
            Origin = aim + Vector3.UnitZ * 3,
            Target = aim,
            Kind = ShotKind.Pellet,
            Damage = 1,
            Duration = .1f
        });
    }

    private static void AssertContainedHands(GameSession game, Zombie mega)
    {
        var blocks = game.MegaWeapons;
        Assert.Equal(4, blocks.Count);
        Assert.All(blocks, block =>
        {
            Assert.True(block.IsActive);
            Assert.Equal(Vector3.One, block.Max - block.Min);
            Assert.True(Vector3.Min(block.Min - (mega.Position - mega.BodyHalfExtents), Vector3.Zero).LengthSquared() < .000001f);
            Assert.True(Vector3.Max(block.Max - (mega.Position + mega.BodyHalfExtents), Vector3.Zero).LengthSquared() < .000001f);
        });
    }

    private static float CoreDistance(Vector3 position) =>
        Vector2.Distance(new Vector2(position.X, position.Z), new Vector2(16));

    private static int TerrainCount(GameSession game)
    {
        int count = 0;
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 0; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                    if (game.World.GetMaterial(new Cell(x, y, z)) != Material.Air) count++;
        return count;
    }

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
