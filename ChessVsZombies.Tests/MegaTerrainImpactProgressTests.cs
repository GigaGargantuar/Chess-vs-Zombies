using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class MegaTerrainImpactProgressTests
{
    [Fact]
    public void ABlockReachingTheGroundAtTheRoundedEndpointStillHitsIt()
    {
        var game = new GameSession();
        Restore(game, new SavedMegaWeaponFlight
        {
            OwnerId = 100, Formation = MegaWeaponFormation.Chunk, IsDebris = true,
            X = 8.5f, Y = 16.54199981689453f, Z = 8.5f,
            VelocityY = -1.1262508630752563f, Damage = 12,
            Blocks = [new SavedMegaWeaponPart { BlockIndex = 0, Material = Material.Stone, Hp = 311 }]
        });

        Invoke(game, "UpdateMegaProjectiles", .03334999829530716f);

        Assert.Empty(game.MegaProjectiles);
        Assert.Equal(90.67f, game.World.GetTerrainHp(new Cell(8, 15, 8)), 4);
        Assert.Equal(311, Assert.Single(game.PendingBlockDrops).Hp);
    }

    [Fact]
    public void ABlockReachingAnOverheadCellAtTheRoundedEndpointStillHitsIt()
    {
        var game = new GameSession();
        Assert.True((bool)Invoke(game.World, "Dig", new Cell(8, 15, 8))!);
        var ceiling = new Cell(8, 17, 8);
        Assert.True((bool)Invoke(game.World, "AddBarrier", ceiling, Material.Dirt, null)!);
        Restore(game, new SavedMegaWeaponFlight
        {
            OwnerId = 100, Formation = MegaWeaponFormation.Chunk, IsDebris = true,
            X = 8.5f, Y = 16.486000061035156f, Z = 8.5f,
            VelocityY = .51214998960495f, Damage = 12,
            Blocks = [new SavedMegaWeaponPart { BlockIndex = 0, Material = Material.Stone, Hp = 311 }]
        });

        // The exact arc reaches Y=17's expanded lower face, although float
        // endpoint arithmetic rounds its scan bound just below that cell.
        Invoke(game, "UpdateMegaProjectiles", .039604999125003815f);

        Assert.Empty(game.MegaProjectiles);
        Assert.Equal(90.67f, game.World.GetBarrier(ceiling)!.Hp, 4);
        Assert.Equal(311, Assert.Single(game.PendingBlockDrops).Hp);
    }

    [Fact]
    public async Task AChunkJustAboveTheGroundHitsAtThePhysicalTimeWhileOtherBulletsAreFlying()
    {
        var game = new GameSession();
        var flight = FallingChunk();
        // The bottom row is one float ULP above the expanded dirt surface.
        // Repeatedly forecasting and shortening a parabola used to discard
        // every sub-ULP displacement, delaying the hit by thousands of slices.
        const float floorContact = 16 + .49999f;
        flight.Y = MathF.BitIncrement(floorContact) + .5f;
        flight.VelocityY = 0;
        Restore(game, flight);
        game.Projectiles.Add(new Projectile
        {
            Origin = new Vector3(24, 24, 24), Target = new Vector3(25, 24, 24),
            Duration = 1, Kind = ShotKind.Pellet, Damage = 1
        });
        float clearance = flight.Y - .5f - floorContact;
        float expectedContactTime = MathF.Sqrt(2 * clearance / GameSession.MegaWeaponGravity);

        await Task.Run(() => Invoke(game, "UpdateProjectiles", .05f))
            .WaitAsync(TimeSpan.FromSeconds(5));

        var fragments = Export(game);
        Assert.All(fragments, fragment => Assert.True(fragment.IsDebris));
        foreach (int upperPart in new[] { 2, 3 })
        {
            var fragment = Assert.Single(fragments,
                candidate => candidate.Blocks.Single().BlockIndex == upperPart);
            Assert.InRange(Math.Abs(fragment.Age - (.05f - expectedContactTime)), 0, .0001f);
        }
        Assert.Equal(4, game.MegaProjectiles.Count + game.PendingBlockDrops.Count);
        AssertMaterialAndHpArePreserved(game);
        Assert.Single(game.Projectiles);
        Assert.Equal(.05f, game.Projectiles[0].Age, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChunkHittingTheGroundFinishesTheUpdateAndPreservesEveryRealPart(bool simultaneousPellet)
    {
        var game = new GameSession();
        Restore(game, FallingChunk());
        if (simultaneousPellet)
            game.Projectiles.Add(new Projectile
            {
                Origin = new Vector3(24, 24, 24), Target = new Vector3(25, 24, 24),
                Duration = 1, Kind = ShotKind.Pellet, Damage = 1
            });
        string update = simultaneousPellet ? "UpdateProjectiles" : "UpdateMegaProjectiles";

        // Run on a background worker so a physics progress regression fails
        // this bounded wait instead of permanently hanging the test runner.
        await Task.Run(() => Invoke(game, update, .1f)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, game.MegaProjectiles.Count + game.PendingBlockDrops.Count);
        Assert.All(game.MegaProjectiles, block =>
        {
            Assert.True(block.IsDebris);
            Assert.True(float.IsFinite(block.Min.X) && float.IsFinite(block.Min.Y) && float.IsFinite(block.Min.Z));
        });
        AssertMaterialAndHpArePreserved(game);
        Assert.Contains(new[] { new Cell(7, 15, 8), new Cell(8, 15, 8) },
            cell => game.World.GetTerrainHp(cell) < Balance.DirtBlockHp);
        Assert.Equal(0, game.Inventory.DirtCount);

        await Task.Run(() =>
        {
            for (int tick = 0; tick < 160 && game.MegaProjectiles.Count > 0; tick++)
                Invoke(game, update, .05f);
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(game.MegaProjectiles);
        Assert.Equal(4, game.PendingBlockDrops.Count);
        AssertMaterialAndHpArePreserved(game);
    }

    [Theory]
    [InlineData(.000001f)]
    [InlineData(.000000000001f)]
    [InlineData(float.Epsilon)]
    public async Task AnUnobstructedFlightConsumesEvenATinyFinalTimeSlice(float duration)
    {
        var game = new GameSession();
        var flight = FallingChunk();
        flight.Y = 24;
        flight.VelocityY = 0;
        flight.VelocityX = 40;
        Restore(game, flight);

        await Task.Run(() => Invoke(game, "UpdateMegaProjectiles", duration))
            .WaitAsync(TimeSpan.FromSeconds(5));

        var saved = Assert.Single(Export(game));
        Assert.Equal(duration, saved.Age);
        Assert.Equal(4, saved.Blocks.Count);
        Assert.False(saved.IsDebris);
        Assert.Empty(game.PendingBlockDrops);
        AssertMaterialAndHpArePreserved(game);
    }

    private static SavedMegaWeaponFlight FallingChunk() => new()
    {
        OwnerId = 100, Formation = MegaWeaponFormation.Chunk,
        X = 8, Y = 17.5f, Z = 8.5f, VelocityY = -20, Damage = 12,
        Blocks =
        [
            Part(0, Material.Dirt, 71, -.5f, -.5f),
            Part(1, Material.Stone, 311, .5f, -.5f),
            Part(2, Material.Wood, 123, -.5f, .5f),
            Part(3, Material.Copper, 617, .5f, .5f)
        ]
    };

    private static SavedMegaWeaponPart Part(int index, Material material, float hp, float x, float y) => new()
    {
        BlockIndex = index, Material = material, Hp = hp, WasBarrier = true, OffsetX = x, OffsetY = y
    };

    private static void AssertMaterialAndHpArePreserved(GameSession game)
    {
        var actual = game.MegaProjectiles.Select(block => (block.Material, block.Hp))
            .Concat(game.PendingBlockDrops.Select(drop => (drop.Content, drop.Hp!.Value)))
            .OrderBy(part => part.Item1).ToArray();
        var expected = FallingChunk().Blocks.Select(block => (block.Material, block.Hp))
            .OrderBy(part => part.Material).ToArray();
        Assert.Equal(expected, actual);
    }

    private static void Restore(GameSession game, params SavedMegaWeaponFlight[] flights) =>
        Invoke(game, "RestoreMegaWeaponFlights", (object)flights);

    private static List<SavedMegaWeaponFlight> Export(GameSession game) =>
        Assert.IsType<List<SavedMegaWeaponFlight>>(Invoke(game, "ExportMegaWeaponFlights"));

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
