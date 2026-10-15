using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloActionBatchTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void SimultaneousRealDeathBlockPickupsFinishBeforeOneSharedMovementFieldRebuild()
    {
        var game = new GameSession();
        var sources = new List<Cell>();
        foreach (var (x, z) in new[] { (4, 4), (8, 4), (12, 4), (20, 4), (24, 4), (28, 4), (4, 8), (8, 8) })
        {
            var kilo = new Zombie { Id = 100 + game.Zombies.Count, Size = ZombieSize.Kilo, Attack = 1 };
            kilo.Position = kilo.StandAt(new Cell(x, 16, z));
            kilo.FaceToward(Vector3.UnitX);
            game.Zombies.Add(kilo);
            var source = new Cell(x + 2, 16, z);
            var dead = new Zombie { Id = 1000 + kilo.Id, Kind = ZombieKind.Armored, Tier = GearTier.Copper,
                Position = source.Center, Hp = 0 };
            Invoke(game, "QueueDeathBlock", dead);
            sources.Add(source);
        }
        Invoke(game, "ResolveBlockDrops");
        Assert.All(sources, source => Assert.Equal(Material.Copper, game.World.GetMaterial(source)));
        Invoke(game, "RebuildNavigation");
        Invoke(game, "TryGetSizedRoute", game.Zombies[0], game.Zombies[0].Cell, default(Cell), true);
        var fields = Field<IDictionary>(game, "_sizeRoutes");
        object originalField = fields[ZombieSize.Kilo]!;
        int originalRevision = game.World.Revision;
        long before = GC.GetAllocatedBytesForCurrentThread();

        Invoke(game, "UpdateKilos", .05f);

        Assert.Equal(originalRevision + sources.Count, game.World.Revision);
        Assert.All(sources, source => Assert.Equal(Material.Air, game.World.GetMaterial(source)));
        Assert.Equal(16, game.KiloCovers.Count);
        Assert.Equal(8, game.KiloCovers.Count(block => block.Material == Material.Copper));
        Assert.All(game.Zombies, kilo => Assert.Equal(0, kilo.CarriedBlocks));
        Assert.Same(originalField, fields[ZombieSize.Kilo]);
        Assert.Equal(originalRevision, Field<int>(game, "_navigationWorldRevision"));
        object? sharedField = null;
        foreach (var kilo in game.Zombies)
        {
            Invoke(game, "UpdateZombie", kilo, .05f);
            sharedField ??= fields[ZombieSize.Kilo];
            Assert.Same(sharedField, fields[ZombieSize.Kilo]);
        }
        Assert.NotSame(originalField, sharedField);
        Assert.Equal(game.World.Revision, Field<int>(game, "_navigationWorldRevision"));
        // A prior interleaved pickup rebuilt both fields for every one of the
        // eight edits (>100 MiB). This includes all real pickup/settlement work,
        // public cover snapshots, and both fresh movement fields.
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 40L * 1024 * 1024);
    }

    [Theory]
    [InlineData("moving")]
    [InlineData("fractional")]
    [InlineData("unsupported")]
    [InlineData("liquid")]
    [InlineData("enclosed")]
    public void BatchDefersCarriersWhosePhysicalRecoveryMustHappenFirst(string state)
    {
        var game = new GameSession();
        var kilo = new Zombie { Id = 100, Size = ZombieSize.Kilo };
        kilo.Position = kilo.StandAt(new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        game.Zombies.Add(kilo);
        if (state == "moving") kilo.NextCell = new Cell(6, 16, 5);
        if (state == "fractional") kilo.Position += Vector3.UnitX * .1f;
        if (state == "unsupported")
            for (int x = 5; x <= 6; x++)
                for (int z = 5; z <= 6; z++) Invoke(game.World, "Dig", new Cell(x, 15, z));
        if (state == "liquid") Assert.True(game.Liquids.TryAdd(new Cell(6, 16, 6), LiquidKind.Mercury, 1));
        if (state == "enclosed") Invoke(game.World, "RestoreTerrainPlacement", new Cell(6, 17, 6), Material.Dirt);
        int revision = game.World.Revision;

        Invoke(game, "UpdateKilos", .05f);

        Assert.Equal(revision, game.World.Revision);
        Assert.Empty(game.KiloCovers);
        Assert.Equal(1, kilo.CarriedBlocks);
    }

    private static T Field<T>(GameSession game, string name) =>
        (T)typeof(GameSession).GetField(name, Hidden)!.GetValue(game)!;

    private static object? Invoke(object instance, string name, params object?[] args) =>
        instance.GetType().GetMethod(name, Hidden)!.Invoke(instance, args);
}
