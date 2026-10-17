using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class SpentBuilderYieldTests
{
    private static readonly Cell BuilderCell = new(8, 16, 15);
    private static readonly Cell BridgeCell = new(9, 16, 15);
    private static readonly Cell FloorCell = new(9, 15, 15);
    private static readonly Cell YieldCell = new(9, 15, 14);

    [Fact]
    public void ASpentBlockerStepsAsideAndWaitsUntilTheCarrierPlacesItsBlock()
    {
        var (game, builder, blocker) = CreateBlockedBridge();
        var tryBuild = TryBuild(game);
        int revision = game.World.Revision;

        Assert.False(tryBuild(builder, BuilderCell, BridgeCell));
        Assert.Equal(revision, game.World.Revision);
        Assert.Equal(1, builder.CarriedBlocks);
        Assert.True(YieldStep(game, blocker, out var initialYield));
        Assert.Equal(YieldCell, initialYield);
        // Hold the real carrier's construction cooldown while the blocker
        // physically finishes its one-cell yield and waits out several decisions.
        builder.Cooldown = 100;
        Advance(game, 2);
        Assert.True(At(blocker, YieldCell), $"Blocker did not step clear: {blocker.Position}; carrier: {builder.Position}.");

        Advance(game, 2, () => Assert.True(At(blocker, YieldCell)));

        Assert.True(At(builder, BuilderCell));
        Assert.Equal(1, builder.CarriedBlocks);
        Assert.Equal(0, blocker.CarriedBlocks);
        Assert.Equal(Material.Air, game.World.GetNaturalMaterial(FloorCell));
        Assert.False(game.IsOccupiedByZombie(FloorCell));
        Assert.Empty(game.World.DamagedTerrain);

        builder.Cooldown = 0;
        game.Update(.02f);

        Assert.Equal(0, builder.CarriedBlocks);
        Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(FloorCell));
        Assert.Equal(Balance.BlockHp(Material.Dirt), game.World.GetTerrainHp(FloorCell));
        Assert.Equal(0, blocker.CarriedBlocks);
        Assert.Equal(Balance.ZombieHp, blocker.Hp);
        Assert.False(YieldStep(game, blocker, out _));
    }

    [Fact]
    public void AYieldRequestSurvivesNavigationRebuildButEndsWhenItsCarrierLeaves()
    {
        var (game, builder, blocker) = CreateBlockedBridge();
        Assert.False(TryBuild(game)(builder, BuilderCell, BridgeCell));
        builder.Cooldown = 100;
        Advance(game, 2);
        Assert.True(At(blocker, YieldCell));

        // An unrelated terrain edit rebuilds the shared field without cancelling
        // this still-valid request or sending its blocker back into the floor.
        var dig = Dig(game.World);
        Assert.True(dig(new Cell(2, 15, 2)));
        game.Update(.02f);
        Assert.True(YieldStep(game, blocker, out var held));
        Assert.Equal(YieldCell, held);
        Assert.True(At(blocker, YieldCell));

        builder.Position = new Cell(7, 16, 15).Center;

        Assert.False(YieldStep(game, blocker, out _));
        Assert.Equal(1, builder.CarriedBlocks);
        Assert.Equal(0, blocker.CarriedBlocks);
        Assert.Equal(Material.Air, game.World.GetNaturalMaterial(FloorCell));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AConstructionRequestDoesNotInterruptAnExistingStepOrJump(int distance)
    {
        var (game, builder, blocker) = CreateBlockedBridge();
        var destination = FloorCell + new Cell(0, 0, -distance);
        blocker.NextCell = destination;
        blocker.MoveProgress = .25f;
        var position = blocker.Position;

        Assert.False(TryBuild(game)(builder, BuilderCell, BridgeCell));

        Assert.Equal(destination, blocker.NextCell);
        Assert.Equal(.25f, blocker.MoveProgress);
        Assert.Equal(position, blocker.Position);
        Assert.False(YieldStep(game, blocker, out _));
        Assert.Equal(1, builder.CarriedBlocks);
        Assert.Equal(0, blocker.CarriedBlocks);
        Assert.Equal(Material.Air, game.World.GetNaturalMaterial(FloorCell));
        // It must not leave a deferred redirect behind either. A fresh request
        // is needed if the unit later stops and still obstructs construction.
        blocker.NextCell = null;
        blocker.MoveProgress = 0;
        Assert.False(YieldStep(game, blocker, out _));
    }

    private static (GameSession Game, Zombie Builder, Zombie Blocker) CreateBlockedBridge()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Waves = [new() { SpawnInterval = .01f, Groups = [new() { Count = 2, Kind = ZombieKind.Basic }] }]
        });
        var game = new GameSession(12345, levels);
        var dig = Dig(game.World);
        // A moat and suspended far bank leave no complete core route. Two lower columns
        // give the spent blocker real footing and exactly one flat place to yield.
        for (int x = 9; x <= 12; x++)
            for (int y = 0; y < 16; y++)
                for (int z = 0; z < WorldGrid.Size; z++)
                    if (!(x == 9 && y <= 14 && z is 14 or 15)) Assert.True(dig(new Cell(x, y, z)));
        for (int x = 13; x < WorldGrid.Size; x++)
            for (int y = 0; y < 15; y++)
                for (int z = 0; z < WorldGrid.Size; z++) Assert.True(dig(new Cell(x, y, z)));
        // The far bank is one block higher. The carrier therefore needs the
        // upper bridge: descending onto the blocker's lower shelf would leave
        // a two-block climb at the far bank and is not its selected route.
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, Material, bool>>(game.World);
        for (int x = 13; x < WorldGrid.Size; x++)
            for (int z = 0; z < WorldGrid.Size; z++)
            {
                var cell = new Cell(x, 16, z);
                if (!WorldGrid.IsCore(cell)) Assert.True(place(cell, Material.Dirt));
            }
        Assert.True(game.StartLevel(), game.Message);
        game.Update(.03f);
        Assert.Equal(2, game.Zombies.Count);
        var builder = game.Zombies[0];
        var blocker = game.Zombies[1];
        foreach (var zombie in game.Zombies)
        {
            zombie.NextCell = null;
            zombie.MoveProgress = 0;
            zombie.Cooldown = 0;
            zombie.Lane = 0;
        }
        builder.Position = BuilderCell.Center;
        blocker.Position = FloorCell.Center;
        typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(blocker, 0);
        return (game, builder, blocker);
    }

    private static void Advance(GameSession game, float seconds, Action? inspect = null)
    {
        for (float elapsed = 0; elapsed < seconds; elapsed += .02f)
        {
            var previous = game.Zombies.ToDictionary(zombie => zombie.Id, zombie => zombie.Position);
            game.Update(.02f);
            foreach (var zombie in game.Zombies)
            {
                Assert.Equal(Balance.ZombieHp, zombie.Hp);
                Assert.InRange(Vector3.Distance(previous[zombie.Id], zombie.Position), 0, zombie.MovementSpeed * .02f + .00001f);
                Assert.Equal(Material.Air, game.World.GetMaterial(zombie.Cell));
            }
            inspect?.Invoke();
        }
    }

    private static bool At(Zombie zombie, Cell cell) => Vector3.DistanceSquared(zombie.Position, cell.Center) < .000001f;
    private static Func<Cell, bool> Dig(WorldGrid world) => typeof(WorldGrid)
        .GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<Cell, bool>>(world);
    private static Func<Zombie, Cell, Cell, bool> TryBuild(GameSession game) => typeof(GameSession)
        .GetMethod("TryBuildBridge", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<Zombie, Cell, Cell, bool>>(game);
    private delegate bool GetYieldStep(Zombie zombie, out Cell next);
    private static bool YieldStep(GameSession game, Zombie zombie, out Cell next) => typeof(GameSession)
        .GetMethod("TryGetBuilderYieldStep", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<GetYieldStep>(game)(zombie, out next);
}
