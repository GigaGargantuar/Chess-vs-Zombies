using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class ZombieBridgeTests
{
    [Fact]
    public void ZombiesContributeOneBlockEachToASharedBridgeAndAnUnspentFollowerCrossesIt()
    {
        var game = NewSession(4);
        ExcavateMoat(game, 9, 12);
        var zombies = StartAt(game, new Cell(8, 16, 15));

        AdvanceUntil(game, 30, () => zombies.All(zombie => zombie.Position.X >= 13.499f) && game.CoreHp < Balance.CoreHp,
            () => Assert.All(zombies, zombie =>
            {
                Assert.True(zombie.Hp > 0);
                Assert.InRange(zombie.CarriedBlocks, 0, 1);
                Assert.Equal(Material.Air, game.World.GetMaterial(zombie.Cell));
            }));

        Assert.All(zombies, zombie => Assert.True(zombie.Position.X >= 13.499f, $"Zombie remained at {zombie.Position}."));
        Assert.True(game.CoreHp < Balance.CoreHp);
        var bridge = BridgeCells(game, 9, 12);
        Assert.Equal(3, bridge.Length);
        Assert.Equal(bridge.Length, zombies.Sum(zombie => 1 - zombie.CarriedBlocks));
        Assert.Contains(zombies, zombie => zombie.CarriedBlocks == 1);
        Assert.All(bridge, cell => Assert.Equal(100, game.World.GetTerrainHp(cell)));
        Assert.Equal(0, game.Inventory.DirtCount);
    }

    [Fact]
    public void OneZombieCannotRegenerateBlocksToCrossAWideMoat()
    {
        var game = NewSession();
        ExcavateMoat(game, 9, 12);
        var zombie = Assert.Single(StartAt(game, new Cell(8, 16, 15)));
        AdvanceUntil(game, 10, () => zombie.CarriedBlocks == 0);
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Single(BridgeCells(game, 9, 12));

        game.Update(20);

        Assert.True(zombie.Hp > 0);
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Single(BridgeCells(game, 9, 12));
        Assert.True(zombie.Position.X < 13, $"A lone builder crossed the wide moat at {zombie.Position}.");
        Assert.Equal(Balance.CoreHp, game.CoreHp);
    }

    [Fact]
    public void ExistingOneCellJumpDoesNotSpendTheCarriedBlock()
    {
        var game = NewSession();
        ExcavateMoat(game, 13, 13);
        var zombie = Assert.Single(StartAt(game, new Cell(12, 16, 15)));
        var peak = zombie.Position.Y;

        AdvanceUntil(game, 8, () => zombie.Position.X >= 14.499f, () =>
        {
            peak = Math.Max(peak, zombie.Position.Y);
            Assert.True(zombie.Hp > 0);
            Assert.Equal(1, zombie.CarriedBlocks);
            Assert.Equal(Material.Air, game.World.GetMaterial(zombie.Cell));
        });

        Assert.True(zombie.Position.X >= 14.499f);
        Assert.True(peak > 17, $"Expected a physical jump arc, but its peak was {peak}.");
        Assert.Equal(1, zombie.CarriedBlocks);
        Assert.Empty(BridgeCells(game, 13, 13));
    }

    [Fact]
    public void ADisconnectedCoreIslandDoesNotPreventTheWaveFromStartingOrSpawning()
    {
        var game = NewSession(3);
        // A three-cell-wide, full-depth ring disconnects every shell spawn from
        // the core island and is too wide for the existing one-cell gap jump.
        Excavate(game, cell => cell.Y < 16 && cell.X is >= 10 and <= 21 && cell.Z is >= 10 and <= 21 &&
            (cell.X is < 13 or > 18 || cell.Z is < 13 or > 18));

        Assert.True(game.StartLevel(), game.Message);
        game.Update(.05f);

        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.Equal(3, Assert.Single(game.Waves).Spawned);
        Assert.Equal(3, game.Zombies.Count);
        Assert.All(game.Zombies, zombie =>
        {
            Assert.True(zombie.Hp > 0);
            Assert.Equal(1, zombie.CarriedBlocks);
            Assert.True(zombie.Cell.X < 10 || zombie.Cell.X > 21 || zombie.Cell.Z < 10 || zombie.Cell.Z > 21);
        });
    }

    [Fact]
    public void AnEmptySpawnShellWaitsWithoutUsingItsQuotaAndRefreshesWhenSupportAppears()
    {
        var game = NewSession();
        Excavate(game, cell => cell.Y < 16 && (cell.X is 0 or 31 || cell.Z is 0 or 31));

        Assert.True(game.StartLevel(), game.Message);
        game.Update(.5f);

        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.Empty(game.Zombies);
        Assert.Equal(0, Assert.Single(game.Waves).Spawned);
        Assert.Equal(1, game.LevelNumber);

        var support = new Cell(0, 15, 8);
        Assert.True(PlaceNatural(game.World)(support, Material.Dirt));
        game.Update(.2f);

        Assert.Equal(1, Assert.Single(game.Waves).Spawned);
        var spawned = Assert.Single(game.Zombies);
        Assert.True(spawned.Hp > 0);
        Assert.Equal(new Cell(0, 16, 8), spawned.Cell);
        Assert.Equal(1, spawned.CarriedBlocks);
    }

    [Theory]
    [InlineData(ZombieKind.Basic, GearTier.Osmium, Material.Dirt)]
    [InlineData(ZombieKind.Armed, GearTier.Wood, Material.Wood)]
    [InlineData(ZombieKind.Armored, GearTier.Iron, Material.Iron)]
    [InlineData(ZombieKind.ArmedArmored, GearTier.Osmium, Material.Osmium)]
    public void AnUnusedCarriedBlockDropsWithItsMaterialAndIsNotDuplicatedAfterWaveCleanup(
        ZombieKind kind, GearTier tier, Material expectedMaterial)
    {
        var game = NewSession(kind: kind, tier: tier);
        var deathCell = new Cell(8, 16, 8);
        var zombie = Assert.Single(StartAt(game, deathCell));
        var before = SnapshotTerrain(game.World);

        game.DamageZombie(zombie, 100_000, DamageKind.Chemical);
        game.Update(.01f);

        Assert.Empty(game.Zombies);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Equal(expectedMaterial, game.World.GetNaturalMaterial(deathCell));
        Assert.Equal(Balance.BlockHp(expectedMaterial), game.World.GetTerrainHp(deathCell));
        Assert.Equal(deathCell, Assert.Single(ChangedTerrainCells(game.World, before)));

        game.Update(2);

        Assert.Equal(expectedMaterial, game.World.GetNaturalMaterial(deathCell));
        Assert.Equal(deathCell, Assert.Single(ChangedTerrainCells(game.World, before)));
        Assert.Equal(0, zombie.CarriedBlocks);
    }

    [Fact]
    public void AnUnsupportedDeathDropFallsToTheGroundWithoutCreatingExtraBlocks()
    {
        var game = NewSession();
        var deathCell = new Cell(8, 20, 8);
        var landedCell = new Cell(8, 16, 8);
        var zombie = Assert.Single(StartAt(game, deathCell));
        var before = SnapshotTerrain(game.World);

        game.DamageZombie(zombie, 100, DamageKind.Chemical);
        game.Update(2);

        Assert.Empty(game.Zombies);
        Assert.Equal(Material.Air, game.World.GetNaturalMaterial(deathCell));
        Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(landedCell));
        Assert.Equal(100, game.World.GetTerrainHp(landedCell));
        Assert.Equal(landedCell, Assert.Single(ChangedTerrainCells(game.World, before)));
        Assert.Equal(0, zombie.CarriedBlocks);
    }

    [Fact]
    public void ABuilderThatSpentItsBlockDoesNotDropAnotherOnDeath()
    {
        var game = NewSession();
        ExcavateMoat(game, 9, 12);
        var zombie = Assert.Single(StartAt(game, new Cell(8, 16, 15)));
        AdvanceUntil(game, 10, () => zombie.CarriedBlocks == 0);
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Single(BridgeCells(game, 9, 12));
        var before = SnapshotTerrain(game.World);

        game.DamageZombie(zombie, 100, DamageKind.Chemical);
        game.Update(2);

        Assert.Empty(game.Zombies);
        Assert.Empty(ChangedTerrainCells(game.World, before));
        Assert.Equal(0, zombie.CarriedBlocks);
    }

    [Fact]
    public void ADeathDropDisplacesWaterWithoutLosingUnitsOrLeavingLiquidInsideTheSolid()
    {
        var game = NewSession();
        var deathCell = new Cell(8, 16, 8);
        var zombie = Assert.Single(StartAt(game, deathCell));
        Assert.True(game.Liquids.TryAdd(deathCell, LiquidKind.Water, LiquidSystem.UnitsPerBlock));

        game.DamageZombie(zombie, 100, DamageKind.Chemical);
        game.Update(.01f);

        Assert.Empty(game.Zombies);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(deathCell));
        Assert.Equal(0, game.Liquids.GetTotalUnits(deathCell));
        Assert.Equal(LiquidSystem.UnitsPerBlock, game.Liquids.GetUnits(deathCell + new Cell(0, 1, 0), LiquidKind.Water));

        // Let subsequent flow settle too: displacement must preserve the volume
        // beyond the single frame when the dropped block first enters the water.
        game.Update(1);

        var liquidCells = game.Liquids.Cells.Where(cell => cell.Water + cell.Mercury + cell.Lava > 0).ToArray();
        Assert.Equal(LiquidSystem.UnitsPerBlock, liquidCells.Sum(cell => cell.Water));
        Assert.All(liquidCells, cell => Assert.Equal(Material.Air, game.World.GetMaterial(cell.Cell)));
        Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(deathCell));
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void ABridgeFloorOccupiedByALivingZombieDoesNotSpendTheBlockAndCanBeRetried()
    {
        var game = NewSession();
        ExcavateMoat(game, 9, 12);
        var start = new Cell(8, 16, 15);
        var next = new Cell(9, 16, 15);
        var floor = next + new Cell(0, -1, 0);
        var builder = Assert.Single(StartAt(game, start));
        var occupant = new Zombie
        {
            Id = -1,
            Kind = ZombieKind.Basic,
            Tier = GearTier.Wood,
            Position = floor.Center,
            Hp = Balance.ZombieHp,
            MaxHp = Balance.ZombieHp,
            Attack = Balance.BaseAttack
        };
        game.Zombies.Add(occupant);
        // Isolate placement from the occupant's later falling/movement update.
        var tryBuild = typeof(GameSession).GetMethod("TryBuildBridge", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Zombie, Cell, Cell, bool>>(game);
        int revisionBefore = game.World.Revision;

        Assert.False(tryBuild(builder, start, next));

        Assert.Equal(1, builder.CarriedBlocks);
        Assert.Equal(0, builder.Cooldown);
        Assert.Equal(revisionBefore, game.World.Revision);
        Assert.Equal(Material.Air, game.World.GetNaturalMaterial(floor));
        Assert.Equal(Balance.ZombieHp, occupant.Hp);

        game.Zombies.Remove(occupant);

        Assert.True(tryBuild(builder, start, next));
        Assert.Equal(0, builder.CarriedBlocks);
        Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(floor));
        Assert.Equal(100, game.World.GetTerrainHp(floor));
    }

    private static GameSession NewSession(int count = 1, ZombieKind kind = ZombieKind.Basic, GearTier tier = GearTier.Wood)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Name = "Cooperative bridge traversal",
            Waves = [new() { SpawnInterval = .01f, Groups = [new() { Count = count, Kind = kind, Tier = tier }] }]
        });
        return new GameSession(12345, levels);
    }

    private static Zombie[] StartAt(GameSession game, Cell start)
    {
        Assert.True(game.StartLevel(), game.Message);
        int count = game.Level.Waves.Single().Groups.Sum(group => group.Count);
        game.Update(count * .01f + .01f);
        Assert.Equal(count, game.Zombies.Count);
        var zombies = game.Zombies.ToArray();
        foreach (var zombie in zombies)
        {
            zombie.Position = start.Center;
            zombie.NextCell = null;
            zombie.MoveProgress = 0;
            zombie.Cooldown = 0;
            zombie.Lane = 0;
        }
        return zombies;
    }

    private static void AdvanceUntil(GameSession game, float timeout, Func<bool> complete, Action? inspect = null)
    {
        for (float elapsed = 0; elapsed < timeout && !complete(); elapsed += .05f)
        {
            game.Update(.05f);
            inspect?.Invoke();
        }
    }

    private static void ExcavateMoat(GameSession game, int firstX, int lastX) =>
        Excavate(game, cell => cell.X >= firstX && cell.X <= lastX && cell.Y < 16);

    private static void Excavate(GameSession game, Func<Cell, bool> include)
    {
        // Fast map construction only: movement, wave quotas, construction, and
        // deaths below still run through the public simulation. Applying each
        // excavation through the player tool would settle thousands of times.
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        foreach (var cell in AllCells())
            if (include(cell) && Balance.IsTerrainMaterial(game.World.GetNaturalMaterial(cell))) Assert.True(dig(cell));
    }

    private static Func<Cell, Material, bool> PlaceNatural(WorldGrid world) =>
        typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, Material, bool>>(world);

    private static Cell[] BridgeCells(GameSession game, int firstX, int lastX) =>
        AllCells().Where(cell => cell.X >= firstX && cell.X <= lastX && cell.Y == 15 &&
            game.World.GetNaturalMaterial(cell) == Material.Dirt).ToArray();

    private static Material[] SnapshotTerrain(WorldGrid world) => AllCells().Select(world.GetNaturalMaterial).ToArray();

    private static Cell[] ChangedTerrainCells(WorldGrid world, Material[] before) =>
        AllCells().Where((cell, index) => world.GetNaturalMaterial(cell) != before[index]).ToArray();

    private static IEnumerable<Cell> AllCells()
    {
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 0; y < WorldGrid.Size; y++)
                for (int z = 0; z < WorldGrid.Size; z++) yield return new Cell(x, y, z);
    }
}
