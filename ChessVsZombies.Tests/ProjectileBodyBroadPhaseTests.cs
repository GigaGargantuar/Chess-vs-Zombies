using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ProjectileBodyBroadPhaseTests
{
    [Fact]
    public void BodyQueriesMatchTheLegacyExhaustiveScanAcrossRandomSegments()
    {
        var game = new GameSession();
        var terrainOnly = new GameSession();
        var query = QueryFor(game);
        var terrainQuery = QueryFor(terrainOnly);
        var random = new Random(409300);
        float Number(float min, float max) => min + (max - min) * random.NextSingle();
        for (int i = 0; i < 80; i++)
        {
            var zombie = new Zombie
            {
                Id = i + 1,
                Size = (ZombieSize)(i % 4),
                Kind = i % 3 == 0 ? ZombieKind.Armored : ZombieKind.Basic,
                Tier = GearTier.Iron,
                Position = new Vector3(Number(-1, 33), Number(15, 32), Number(-1, 33))
            };
            if (i % 13 == 0) zombie.Hp = 0;
            game.Zombies.Add(zombie);
        }
        for (int i = 0; i < 1000; i++)
        {
            var from = new Vector3(Number(0, 31.999f), Number(0, 31.999f), Number(0, 31.999f));
            var to = i % 2 == 0
                ? game.Zombies[random.Next(game.Zombies.Count)].Position + new Vector3(Number(-.6f, .6f), Number(-.6f, .6f), Number(-.6f, .6f))
                : new Vector3(Number(-2, 34), Number(-2, 34), Number(-2, 34));
            AssertLegacyContact(game, query, terrainQuery, from, to);
        }
    }

    [Theory]
    [InlineData(ZombieSize.Standard)]
    [InlineData(ZombieSize.Milli)]
    [InlineData(ZombieSize.Kilo)]
    [InlineData(ZombieSize.Mega)]
    public void TangenciesEndpointsAndOverlappingBodiesRetainExactContacts(ZombieSize size)
    {
        var game = new GameSession();
        var terrainOnly = new GameSession();
        var query = QueryFor(game);
        var terrainQuery = QueryFor(terrainOnly);
        var center = new Vector3(10.5f, 20.5f, 10.5f);
        var dead = new Zombie { Id = 1, Size = size, Position = center };
        dead.Hp = 0;
        var first = new Zombie { Id = 2, Size = size, Position = center };
        var second = new Zombie { Id = 3, Size = size, Position = center };
        game.Zombies.AddRange([dead, first, second]);
        float radius = first.IsGiant ? first.BodyHalfExtents.Y : .5f * first.BodyScale;
        float tangent = center.Y + radius;
        foreach (float y in new[] { MathF.BitDecrement(tangent), tangent, MathF.BitIncrement(tangent) })
            AssertLegacyContact(game, query, terrainQuery, new Vector3(5.5f, y, 10.5f), new Vector3(15.5f, y, 10.5f));

        AssertLegacyContact(game, query, terrainQuery, center, center);
        AssertLegacyContact(game, query, terrainQuery, new Vector3(5.5f, 20.5f, 10.5f), center - Vector3.UnitX * radius);
        var overlap = Read(query, center - Vector3.UnitX * 5, center + Vector3.UnitX * 5);
        Assert.Same(first, overlap.Zombie);
        first.Hp = 0;
        AssertLegacyContact(game, query, terrainQuery, center - Vector3.UnitX * 5, center + Vector3.UnitX * 5);
        Assert.Same(second, Read(query, center - Vector3.UnitX * 5, center + Vector3.UnitX * 5).Zombie);
    }

    [Fact]
    public void SmallBodiesKeepTheirHalfBlockProjectileRadius()
    {
        var game = new GameSession();
        var zombie = new Zombie { Id = 1, Position = new Vector3(10.5f, 20.5f, 10.5f) };
        game.Zombies.Add(zombie);
        var from = new Vector3(8.5f, 20.5f, 10.9f);
        var to = new Vector3(12.5f, 20.5f, 10.9f);
        Assert.True(from.Z > zombie.Position.Z + zombie.BodyHalfExtents.Z);

        AssertLegacyContact(game, QueryFor(game), QueryFor(new GameSession()), from, to);
        Assert.Same(zombie, Read(QueryFor(game), from, to).Zombie);
    }

    [Fact]
    public void WorldBoundaryAndTerrainTiesKeepTheirOriginalPriority()
    {
        var game = new GameSession();
        var terrainOnly = new GameSession();
        var query = QueryFor(game);
        var terrainQuery = QueryFor(terrainOnly);
        game.Zombies.Add(new Zombie { Id = 1, Position = new Vector3(32.5f, 20.5f, 10.5f) });
        game.Zombies.Add(new Zombie { Id = 2, Position = new Vector3(10.5f, 15.5f, 10.5f) });

        AssertLegacyContact(game, query, terrainQuery, new Vector3(31, 20.5f, 10.5f), new Vector3(33, 20.5f, 10.5f));
        AssertLegacyContact(game, query, terrainQuery, new Vector3(10.5f, 18, 10.5f), new Vector3(10.5f, 14, 10.5f));
        AssertLegacyContact(game, query, terrainQuery, new Vector3(-.01f, 20.5f, 10.5f), new Vector3(2, 20.5f, 10.5f));
        AssertLegacyContact(game, query, terrainQuery, new Vector3(31, 20.5f, 10.5f), new Vector3(10000, 20.5f, 10.5f));
    }

    private static void AssertLegacyContact(GameSession game, ImpactQuery query, ImpactQuery terrainQuery, Vector3 from, Vector3 to)
    {
        var expected = Read(terrainQuery, from, to);
        // Start with exactly the pre-existing terrain/world-boundary contact,
        // then freeze the old exhaustive body scan with its original ordering.
        // The reference has no broad rejection and shares only exact geometry.
        if (PointInside(from))
            foreach (var candidate in game.Zombies)
            {
                if (candidate.Hp <= 0) continue;
                float hit;
                bool intersects = candidate.IsGiant
                    ? Box(from, to - from, candidate.Position - candidate.BodyHalfExtents,
                        candidate.Position + candidate.BodyHalfExtents, out hit)
                    : Sphere(from, to - from, candidate.Position, out hit, candidate.BodyScale);
                if (!intersects || hit >= expected.Fraction) continue;
                expected = new Contact(true, hit, null, candidate, null);
            }

        var actual = Read(query, from, to);
        Assert.Equal(expected.Hit, actual.Hit);
        Assert.Equal(expected.Fraction, actual.Fraction);
        Assert.Equal(expected.Terrain, actual.Terrain);
        Assert.Same(expected.Zombie, actual.Zombie);
        Assert.Equal(expected.CrustFace, actual.CrustFace);
    }

    private static bool PointInside(Vector3 point) => point.X >= 0 && point.X < WorldGrid.Size &&
        point.Y >= 0 && point.Y < WorldGrid.Size && point.Z >= 0 && point.Z < WorldGrid.Size;

    private static Contact Read(ImpactQuery query, Vector3 from, Vector3 to)
    {
        bool hit = query(from, to, out float fraction, out var terrain, out var zombie, out var face);
        return new Contact(hit, fraction, terrain, zombie, face);
    }

    private sealed record Contact(bool Hit, float Fraction, Cell? Terrain, Zombie? Zombie, CrustFace? CrustFace);
    private delegate bool ImpactQuery(Vector3 from, Vector3 to, out float fraction, out Cell? terrain, out Zombie? zombie, out CrustFace? face);
    private delegate bool SphereQuery(Vector3 from, Vector3 delta, Vector3 center, out float hit, float bodyScale);
    private delegate bool BoxQuery(Vector3 from, Vector3 delta, Vector3 min, Vector3 max, out float hit);
    private static ImpactQuery QueryFor(GameSession game) => Method("FindProjectileImpact", false).CreateDelegate<ImpactQuery>(game);
    private static readonly SphereQuery Sphere = Method("ProjectileBodyIntersection", true).CreateDelegate<SphereQuery>();
    private static readonly BoxQuery Box = Method("ProjectileBoxIntersection", true).CreateDelegate<BoxQuery>();
    private static MethodInfo Method(string name, bool isStatic) => typeof(GameSession).GetMethod(name,
        BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance))!;
}
