using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ProjectilePredictionInvalidationTests
{
    [Fact]
    public void OuterForecastAndStableSortShareOnlyAnIdenticalFrameQuery()
    {
        var game = Create();
        game.Projectiles.Add(new Projectile { Origin = new(1, 25, 3), Target = new(2, 25, 3),
            Kind = ShotKind.Pellet, Damage = 1, Duration = 100 });

        Invoke(game, "UpdateProjectiles", .05f);

        Assert.Equal(2, game.ProjectileDiagnostics!.ForecastRequests);
        Assert.Equal(1, game.ProjectileDiagnostics.ForecastEvaluations);
        Assert.Single(game.Projectiles);
    }

    [Fact]
    public void HpOnlyContactsKeepTheOriginalReverseAppendTieOrderWithoutRepeatedSorts()
    {
        var game = Create(Debris(8));
        for (int damage = 1; damage <= 3; damage++) game.Projectiles.Add(Shot(damage));

        Invoke(game, "UpdateProjectiles", .05f);

        var contacts = game.ProjectileDiagnostics!.Contacts;
        Assert.Equal(new[] { 2, 1, 0 }, contacts.Select(contact => contact.Shot));
        Assert.Equal(new[] { 500f, 497f, 495f }, contacts.Select(contact => contact.HpBefore));
        Assert.Equal(new[] { 497f, 495f, 494f }, contacts.Select(contact => contact.HpAfter));
        Assert.All(contacts, contact => Assert.Equal("flight", contact.Kind));
        Assert.Equal(1, game.ProjectileDiagnostics.PendingSorts);
        Assert.Equal(494, Assert.Single(game.MegaProjectiles).Hp);
    }

    [Fact]
    public void BodyDeathRevealsTheLaterBodyEvenWhenUnusedArmorRemains()
    {
        var game = Create();
        var front = Body(game, 10, 8, ZombieKind.Armored);
        Body(game, 11, 12);
        var shot = Shot(1);
        Begin(game, shot);
        float before = Next(game, shot);

        game.DamageZombie(front, 10, DamageKind.Chemical);
        float after = Next(game, shot);

        Assert.Equal(0, front.BodyHp);
        Assert.True(front.ArmorPieces.Sum(piece => piece.Hp) > 0);
        Assert.True(after > before);
        AssertExactScalar(game, shot, after);
    }

    [Fact]
    public void TerrainAdditionAndDestructionInvalidateEarlierPredictionsWithinTheFrame()
    {
        var game = Create();
        Body(game, 10, 12);
        var shot = Shot(1);
        Begin(game, shot);
        float open = Next(game, shot);
        var cell = new Cell(5, 20, 8);
        Assert.True((bool)Invoke(game.World, "AddBarrier", cell, Material.Dirt, (float?)1)!);
        float blocked = Next(game, shot);
        Assert.True(blocked < open);
        AssertExactScalar(game, shot, blocked);

        Invoke(game, "DamageProjectileTerrain", cell, 2f, null);
        float reopened = Next(game, shot);
        Assert.Equal(BitConverter.SingleToInt32Bits(open), BitConverter.SingleToInt32Bits(reopened));
        AssertExactScalar(game, shot, reopened);
    }

    [Fact]
    public void FlyingPartDamageKeepsTheKeyUntilDestructionRevealsTheBodyBehindIt()
    {
        var game = Create(Debris(8), Debris(26, 26));
        Body(game, 10, 12);
        var shot = Shot(1);
        Begin(game, shot);
        float before = Next(game, shot);
        var flight = ((IList)Field(game, "_megaFlights"))[0]!;
        var block = ((IList)flight.GetType().GetField("Blocks")!.GetValue(flight)!)[0]!;
        long evaluated = game.ProjectileDiagnostics!.ForecastEvaluations;
        Invoke(game, "DamageMegaFlightBlock", flight, block, 2f);
        Assert.Equal(before, Next(game, shot));
        Assert.Equal(evaluated, game.ProjectileDiagnostics.ForecastEvaluations);

        Invoke(game, "DamageMegaFlightBlock", flight, block, 1000f);
        float after = Next(game, shot);
        Assert.True(after > before);
        AssertExactScalar(game, shot, after);
        Assert.Single(game.MegaProjectiles);
    }

    [Fact]
    public void ReactiveHandMotionAndHeldPartDestructionBothInvalidateTheFrame()
    {
        var game = Create();
        var carrier = new Zombie { Id = 10, Size = ZombieSize.Kilo, Position = new(8, 20.5f, 8.5f) };
        game.Zombies.Add(carrier);
        var block = Hold(game, carrier, new Vector3(-.5f, 0, -.5f));
        var shot = Shot(1);
        Begin(game, shot);
        Next(game, shot);
        long evaluated = game.ProjectileDiagnostics!.ForecastEvaluations;
        var from = new Vector3(2, 20.9f, 9.3f);
        var to = new Vector3(14, 20.9f, 9.3f);
        Invoke(game, "TryInterceptKiloShot", carrier, from, to, 5f / 12);
        Assert.NotEqual(new Vector3(-.5f, 0, -.5f), (Vector3)block.GetType().GetProperty("Offset")!.GetValue(block)!);
        float moved = Next(game, shot);
        Assert.True(game.ProjectileDiagnostics.ForecastEvaluations > evaluated);
        AssertExactScalar(game, shot, moved);

        evaluated = game.ProjectileDiagnostics.ForecastEvaluations;
        Invoke(game, "DamageKiloCover", carrier.Id, 0, 1000f);
        float destroyed = Next(game, shot);
        Assert.True(game.ProjectileDiagnostics.ForecastEvaluations > evaluated);
        AssertExactScalar(game, shot, destroyed);
        Assert.Empty(game.KiloCovers);
    }

    [Fact]
    public void NewFramesObserveBodyMovementAndFlyingBlockMotionWithUnchangedShotAge()
    {
        var game = Create();
        var body = Body(game, 10, 8);
        var shot = Shot(1);
        Begin(game, shot);
        float before = Next(game, shot);
        Invoke(game, "EndMegaInterceptionFrame");
        body.Position += Vector3.UnitX * 3;
        Begin(game, shot);
        float after = Next(game, shot);
        Assert.True(after > before);
        AssertExactScalar(game, shot, after);

        var moving = Debris(8);
        moving.VelocityX = 100;
        var flightGame = Create(moving);
        Begin(flightGame, shot);
        before = Next(flightGame, shot);
        Invoke(flightGame, "EndMegaInterceptionFrame");
        Invoke(flightGame, "UpdateMegaProjectiles", .02f);
        Begin(flightGame, shot);
        after = Next(flightGame, shot);
        Assert.True(after > before);
        AssertExactScalar(flightGame, shot, after);
    }

    [Fact]
    public void ShotAgeAndDurationRemainPartOfThePredictionKey()
    {
        var game = Create(Debris(8));
        var shot = Shot(1);
        Begin(game, shot);
        Next(game, shot);
        shot.Age = .01f;
        AssertExactScalar(game, shot, Next(game, shot));
        float shortWindow = (float)Invoke(game, "NextMegaInterceptionTime", shot, .001f)!;
        Assert.Equal(BitConverter.SingleToInt32Bits((float)Invoke(game, "ComputeNextMegaInterceptionTime", shot, .001f)!),
            BitConverter.SingleToInt32Bits(shortWindow));
    }

    private static GameSession Create(params SavedMegaWeaponFlight[] flights)
    {
        var game = new GameSession { ProjectileDiagnostics = new() { CaptureContacts = true } };
        Invoke(game, "RestoreMegaWeaponFlights", (object)(flights.Length == 0 ? new[] { Debris(26, 26) } : flights));
        return game;
    }
    private static Zombie Body(GameSession game, int id, float x, ZombieKind kind = ZombieKind.Basic)
    {
        var zombie = new Zombie { Id = id, Position = new(x, 20.5f, 8.5f), Kind = kind, Tier = GearTier.Osmium, WaveIndex = -1 };
        game.Zombies.Add(zombie);
        return zombie;
    }
    private static Projectile Shot(float damage) => new() { Origin = new(2, 20.5f, 8.5f), Target = new(20, 20.5f, 8.5f),
        Kind = ShotKind.Pellet, Damage = damage, Duration = .05f };
    private static SavedMegaWeaponFlight Debris(float x, float z = 8.5f) => new()
    {
        OwnerId = (int)x + 100, Formation = MegaWeaponFormation.Stick, IsDebris = true, X = x, Y = 20.5f, Z = z,
        Damage = 13.5f, Blocks = [new() { BlockIndex = 0, Material = Material.Stone, Hp = 500 }]
    };
    private static object Hold(GameSession game, Zombie carrier, Vector3 offset)
    {
        var type = typeof(GameSession).GetNestedType("HeldKiloBlock", BindingFlags.NonPublic)!;
        var blocks = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        var block = Activator.CreateInstance(type, [0, Material.Stone, 500f, false])!;
        type.GetProperty("Offset")!.SetValue(block, offset);
        blocks.Add(block);
        ((IDictionary)Field(game, "_kiloBlocks"))[carrier.Id] = blocks;
        return block;
    }
    private static void Begin(GameSession game, Projectile shot)
    {
        if (!game.Projectiles.Contains(shot)) game.Projectiles.Add(shot);
        Invoke(game, "BeginMegaInterceptionFrame", .05f);
    }
    private static float Next(GameSession game, Projectile shot) => (float)Invoke(game, "NextMegaInterceptionTime", shot, .05f)!;
    private static void AssertExactScalar(GameSession game, Projectile shot, float cached) =>
        Assert.Equal(BitConverter.SingleToInt32Bits((float)Invoke(game, "ComputeNextMegaInterceptionTime", shot, .05f)!),
            BitConverter.SingleToInt32Bits(cached));
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(target, args);
}
