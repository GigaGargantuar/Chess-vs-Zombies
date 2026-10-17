using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class SpentBuilderNavigationTests
{
    [Fact]
    public void ASpentBuilderDescendsFromItsOwnBridgeIntoASteppedPitBelowAFloatingCore()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Count = 1, Kind = ZombieKind.Basic }] }] });
        var game = new GameSession(12345, levels);
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        // The live battlefield's concentric one-block stairs: the core has no
        // continuous dry route, and descending initially moves farther below it.
        for (int inset = 0; inset < 4; inset++)
            for (int x = 12 + inset; x <= 19 - inset; x++)
                for (int z = 12 + inset; z <= 19 - inset; z++)
                {
                    var cell = new Cell(x, 15 - inset, z);
                    if (!WorldGrid.IsCore(cell)) Assert.True(dig(cell));
                }
        var initialTerrain = SnapshotTerrain(game.World);
        var builder = BeginAt(game, new Cell(12, 15, 16));
        var bridgeFloor = new Cell(13, 14, 16);
        var bridgeTop = bridgeFloor + new Cell(0, 1, 0);
        AdvanceUntil(game, 10, () => builder.CarriedBlocks == 0);
        Assert.Equal(0, builder.CarriedBlocks);
        Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(bridgeFloor));
        // Reproduce the reported standing position after the genuine build.
        // With the fix, it may choose the existing stairs before walking onto
        // its floor, so the test starts the departure from this observed state.
        builder.Position = bridgeTop.Center;
        builder.NextCell = null;
        builder.MoveProgress = 0;

        AdvanceUntil(game, 20, () => builder.Position.X >= 14.499f && builder.Position.Y <= 14.501f);

        Assert.True(builder.Position.X >= 14.499f && builder.Position.Y <= 14.501f,
            $"Builder could not leave its bridge for the existing stairs: {builder.Position}.");
        Assert.Equal(0, builder.CarriedBlocks);
        Assert.Equal(bridgeFloor, Assert.Single(ChangedTerrain(game.World, initialTerrain)));
        Assert.Empty(game.World.DamagedTerrain);
        Assert.Equal(Balance.BlockHp(Material.Dirt), game.World.GetTerrainHp(bridgeFloor));
    }

    [Fact]
    public void ASpentBuilderLeavesItsOwnBlockForASupportedShelfAndUsesALaterBridge()
    {
        var game = CreateMoatAndShelf();
        var initialTerrain = SnapshotTerrain(game.World);
        var bank = new Cell(8, 16, 15);
        var ownFloor = new Cell(9, 15, 15);
        var ownStand = ownFloor + new Cell(0, 1, 0);
        var lowerExit = new Cell(9, 15, 16);
        var shelfEnd = new Cell(10, 15, 17);
        var builder = BeginAt(game, bank);
        var initialCurrency = game.Economy.Currency;

        AdvanceUntil(game, 10, () => builder.CarriedBlocks == 0);

        Assert.Equal(0, builder.CarriedBlocks);
        Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(ownFloor));
        Assert.Equal(ownFloor, Assert.Single(ChangedTerrain(game.World, initialTerrain)));
        AdvanceUntil(game, 10, () => At(builder, ownStand));
        Assert.True(At(builder, ownStand), $"Builder never walked onto its own block: {builder.Position}.");
        // A real carrier is preparing the next placement, so the army has
        // useful work and the spent unit need not begin a resupply journey.
        var firstCarrier = AddCarrier(game, bank, -1);
        firstCarrier.Cooldown = 100;
        bool steppedDown = false;

        // The optimistic bridge route wants another unsupported floor straight
        // ahead. A spent builder can instead descend onto this real side shelf,
        // even though that shelf also lacks a complete route to the objective.
        AdvanceUntil(game, 15, () => At(builder, shelfEnd), () =>
        {
            steppedDown |= At(builder, lowerExit);
            Assert.Equal(0, builder.CarriedBlocks);
            Assert.Equal(Balance.BlockHp(Material.Dirt), game.World.GetTerrainHp(ownFloor));
        });

        Assert.True(steppedDown, $"Builder did not take the supported one-block descent: {builder.Position}.");
        Assert.True(At(builder, shelfEnd), $"Spent builder remained on its self-built block: {builder.Position}.");
        Assert.Equal(ownFloor, Assert.Single(ChangedTerrain(game.World, initialTerrain)));
        Assert.Equal(Balance.CoreHp, game.CoreHp);
        Assert.Equal(initialCurrency, game.Economy.Currency);
        Assert.Empty(game.World.DamagedTerrain);

        // It must not oscillate back to the unbuildable shortcut while waiting
        // for another carrier, regenerate inventory, or excavate the sound shelf.
        AdvanceUntil(game, 1, () => false, () => Assert.True(At(builder, shelfEnd)));
        Assert.Equal(0, builder.CarriedBlocks);
        Assert.Equal(ownFloor, Assert.Single(ChangedTerrain(game.World, initialTerrain)));

        // Actual later carriers extend the upper bridge. Their new support must
        // invalidate the waiting builder's decision and make the real route usable.
        firstCarrier.Cooldown = 0;
        AdvanceUntil(game, 15, () => firstCarrier.CarriedBlocks == 0);
        Assert.Equal(0, firstCarrier.CarriedBlocks);
        Assert.Equal(2, ChangedTerrain(game.World, initialTerrain).Length);
        var secondCarrier = AddCarrier(game, bank, -2);
        AdvanceUntil(game, 30, () => builder.Position.X >= 14.499f && game.CoreHp < Balance.CoreHp);

        Assert.Equal(0, secondCarrier.CarriedBlocks);
        Assert.True(builder.Position.X >= 14.499f, $"Spent builder ignored a later usable bridge: {builder.Position}.");
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Equal(3, ChangedTerrain(game.World, initialTerrain).Length);
        Assert.All(new[] { builder, firstCarrier, secondCarrier }, zombie =>
        {
            Assert.Equal(0, zombie.CarriedBlocks);
            Assert.Equal(Balance.ZombieHp, zombie.Hp);
        });
        Assert.Empty(game.World.DamagedTerrain);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(initialCurrency, game.Economy.Currency);
    }

    private static GameSession CreateMoatAndShelf()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Name = "Spent builder descent",
            Waves = [new() { Groups = [new() { Count = 1, Kind = ZombieKind.Basic }] }]
        });
        var game = new GameSession(12345, levels);
        // Only map setup uses the internal excavation helper; construction,
        // carried-block spending, navigation, and later cooperation are public flows.
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        for (int x = 9; x <= 12; x++)
            for (int y = 0; y < 16; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                {
                    bool shelfFloor = y == 14 && (x == 9 && z is 16 or 17 || x == 10 && z == 17);
                    if (!shelfFloor) Assert.True(dig(new Cell(x, y, z)));
                }
        // A single pillar grounds the outer slab without forming a walkable
        // staircase to the bottom plane. The shelf hangs from its one side stub.
        for (int x = 0; x <= 8; x++)
            for (int y = 0; y < 15; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                    if (!(x == 0 && z == 0 || x == 8 && y == 14 && z == 16))
                        Assert.True(dig(new Cell(x, y, z)));
        // Keep the core-connected far bank suspended above the usable bottom
        // plane, so its surface still requires the later carriers' bridge.
        for (int x = 13; x < WorldGrid.Size; x++)
            for (int y = 0; y < 15; y++)
                for (int z = 0; z < WorldGrid.Size; z++) Assert.True(dig(new Cell(x, y, z)));

        // This bank wall makes the first bridge block the shortest initial
        // approach, while leaving the lower side shelf accessible from that block.
        Assert.True(game.Inventory.Add(Material.Dirt, 16));
        for (int x = 1; x <= 8; x++)
            for (int y = 16; y <= 17; y++)
                Assert.True(game.BuildBarrier(new Cell(x, y, 16)));
        return game;
    }

    private static Zombie BeginAt(GameSession game, Cell cell)
    {
        Assert.True(game.StartLevel(), game.Message);
        game.Update(.001f);
        var builder = Assert.Single(game.Zombies);
        builder.Position = cell.Center;
        builder.NextCell = null;
        builder.MoveProgress = 0;
        builder.Cooldown = 0;
        builder.Lane = 0;
        return builder;
    }

    private static Zombie AddCarrier(GameSession game, Cell cell, int id)
    {
        var carrier = new Zombie
        {
            Id = id, WaveIndex = -1, Kind = ZombieKind.Basic,
            Position = cell.Center, Attack = Balance.BaseAttack, Lane = 0
        };
        game.Zombies.Add(carrier);
        return carrier;
    }

    private static bool At(Zombie zombie, Cell cell) => Vector3.DistanceSquared(zombie.Position, cell.Center) < .000001f;

    private static void AdvanceUntil(GameSession game, float seconds, Func<bool> complete, Action? inspect = null)
    {
        for (float elapsed = 0; elapsed < seconds && !complete(); elapsed += .02f)
        {
            var previous = game.Zombies.ToDictionary(zombie => zombie.Id, zombie => zombie.Position);
            game.Update(.02f);
            foreach (var zombie in game.Zombies)
            {
                Assert.Equal(Balance.ZombieHp, zombie.Hp);
                if (previous.TryGetValue(zombie.Id, out var origin))
                {
                    var horizontal = new Vector2(zombie.Position.X - origin.X, zombie.Position.Z - origin.Z);
                    Assert.InRange(horizontal.Length(), 0, zombie.MovementSpeed * .02f + .00001f);
                }
                AssertBodyClear(game, zombie);
            }
            inspect?.Invoke();
        }
    }

    private static void AssertBodyClear(GameSession game, Zombie zombie)
    {
        var min = zombie.Position - new Vector3(.2f, .39f, .2f);
        var max = zombie.Position + new Vector3(.2f, .39f, .2f);
        for (int x = (int)MathF.Floor(min.X); x <= (int)MathF.Floor(max.X); x++)
            for (int y = (int)MathF.Floor(min.Y); y <= (int)MathF.Floor(max.Y); y++)
                for (int z = (int)MathF.Floor(min.Z); z <= (int)MathF.Floor(max.Z); z++)
                    Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(x, y, z)));
    }

    private static Material[] SnapshotTerrain(WorldGrid world) => AllCells().Select(world.GetNaturalMaterial).ToArray();

    private static Cell[] ChangedTerrain(WorldGrid world, Material[] initial) => AllCells()
        .Where((cell, index) => world.GetNaturalMaterial(cell) != initial[index]).ToArray();

    private static IEnumerable<Cell> AllCells()
    {
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 0; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++) yield return new Cell(x, y, z);
    }
}
