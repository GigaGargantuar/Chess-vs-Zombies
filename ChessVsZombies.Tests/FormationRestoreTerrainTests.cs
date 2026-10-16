using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class FormationRestoreTerrainTests
{
    private static readonly Cell GroundTower = new(8, 16, 8);
    private static readonly Cell ElevatedTower = new(8, 19, 8);

    [Theory]
    [InlineData(Material.Dirt, ToolKind.Shovel)]
    [InlineData(Material.Stone, ToolKind.Pickaxe)]
    public void BuriedSavedCellIsExcavatedWithTheOwnedToolAndItsMaterialIsRecovered(Material material, ToolKind tool)
    {
        var game = SavedGroundTower();
        Assert.True(game.BuyTool(tool));
        PlaceTerrain(game.World, GroundTower, material);
        int currency = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Equal(GroundTower, Assert.Single(game.Turrets).Cell);
        Assert.Equal(Material.Air, game.World.GetMaterial(GroundTower));
        Assert.Equal(1, game.Inventory.GetCount(material));
        Assert.Equal(currency - Balance.PieceCost(PieceKind.Pawn), game.Economy.Currency);
        Assert.False(game.IsAutoRestoringFormation);
    }

    [Theory]
    [InlineData(Material.Wood)]
    [InlineData(Material.Osmium)]
    public void APlacedBlockInTheSavedCellIsRecoveredWithoutAnExcavationTool(Material material)
    {
        var game = SavedGroundTower();
        Assert.True(game.Inventory.Add(material));
        Assert.True(game.BuildBarrier(GroundTower, material));

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Equal(GroundTower, Assert.Single(game.Turrets).Cell);
        Assert.Null(game.World.GetBarrier(GroundTower));
        Assert.Equal(1, game.Inventory.GetCount(material));
        Assert.False(game.Inventory.HasShovel);
        Assert.False(game.Inventory.HasPickaxe);
    }

    [Fact]
    public void ObsidianInTheSavedCellIsMinedAndRecoveredAsLayers()
    {
        var game = SavedGroundTower();
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        Assert.True(game.Inventory.Add(Material.Obsidian, 3));
        for (int i = 0; i < 3; i++) Assert.True(game.BuildObsidian(GroundTower, CrustFace.Bottom));

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Equal(GroundTower, Assert.Single(game.Turrets).Cell);
        Assert.Null(game.World.GetObsidian(GroundTower));
        Assert.Equal(3, game.Inventory.ObsidianLayers);
    }

    [Fact]
    public void MissingPedestalIsRebuiltBottomUpWithStoredDirtBeforeStone()
    {
        var game = SavedElevatedTower();
        Assert.True(game.Inventory.Add(Material.Stone, 2));

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        AssertPedestal(game);
        Assert.Equal(ElevatedTower, Assert.Single(game.Turrets).Cell);
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Equal(2, game.Inventory.StoneCount);
        game.Update(.1f);
        Assert.Equal(ElevatedTower, Assert.Single(game.Turrets).Cell);
    }

    [Fact]
    public void MissingExcavationToolWaitsWithoutBlockingAnotherSavedTower()
    {
        var game = NewGame();
        var other = GroundTower + new Cell(1, 0, 0);
        Assert.True(game.Deploy(PieceKind.Pawn, GroundTower));
        Assert.True(game.Deploy(PieceKind.Pawn, other));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(GroundTower));
        Assert.True(game.Remove(other));
        PlaceTerrain(game.World, GroundTower, Material.Dirt);
        int currency = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Equal(other, Assert.Single(game.Turrets).Cell);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(GroundTower));
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Equal(currency - Balance.PieceCost(PieceKind.Pawn), game.Economy.Currency);
        Assert.True(game.IsAutoRestoringFormation);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        game.Update(.36f);
        Assert.NotNull(game.TrySelectTurret(GroundTower));
        Assert.False(game.IsAutoRestoringFormation);
    }

    [Fact]
    public void AShortageOfStoredSupportBlocksWaitsWithoutSpendingAndResumesWhenBlocksArrive()
    {
        var game = SavedElevatedTower();
        Assert.True(game.Inventory.TrySpend(Material.Dirt, 3));
        int currency = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Empty(game.Turrets);
        Assert.Empty(game.World.Barriers);
        Assert.Equal(currency, game.Economy.Currency);
        Assert.True(game.IsAutoRestoringFormation);
        Assert.True(game.Inventory.Add(Material.Dirt, 3));
        game.Update(.36f);
        AssertPedestal(game);
        Assert.Equal(ElevatedTower, Assert.Single(game.Turrets).Cell);
    }

    [Fact]
    public void InsufficientTowerCurrencyDoesNotExcavateItsSavedCell()
    {
        var game = SavedGroundTower();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        PlaceTerrain(game.World, GroundTower, Material.Dirt);
        Assert.True(game.Economy.TrySpend(game.Economy.Currency));

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Empty(game.Turrets);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(GroundTower));
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Equal(0, game.Economy.Currency);
    }

    [Fact]
    public void InsufficientSavedComponentsDoNotExcavateOrSpendForThatTower()
    {
        var game = NewGame();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Deploy(PieceKind.Pawn, GroundTower));
        Assert.Single(game.Turrets).UpgradePoints[UpgradeKind.HP] = 2;
        game.Economy.GetProgress(UpgradeKind.HP).PullCount = 21;
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(GroundTower));
        PlaceTerrain(game.World, GroundTower, Material.Dirt);
        game.Economy.GetProgress(UpgradeKind.HP).Available = 1;
        int currency = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Empty(game.Turrets);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(GroundTower));
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Equal(currency, game.Economy.Currency);
        Assert.Equal(1, game.Economy.GetProgress(UpgradeKind.HP).Available);
        game.Economy.GetProgress(UpgradeKind.HP).Available = 2;
        game.Update(.36f);
        Assert.Equal(2, Assert.Single(game.Turrets).UpgradePoints[UpgradeKind.HP]);
        Assert.Equal(1, game.Inventory.DirtCount);
    }

    [Fact]
    public void CameraClearanceCoversEverySupportBlockBeforeAnyTerrainIsChanged()
    {
        var game = SavedElevatedTower();
        var camera = new FlyCamera(new Cell(8, 17, 8).Center);
        int currency = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation(cell => !camera.Intersects(cell)));
        game.Update(.36f);

        Assert.Empty(game.Turrets);
        Assert.Empty(game.World.Barriers);
        Assert.Equal(3, game.Inventory.DirtCount);
        Assert.Equal(currency, game.Economy.Currency);
        camera.Move(System.Numerics.Vector3.UnitX, 2, _ => false);
        game.Update(.36f);
        AssertPedestal(game);
        Assert.Equal(ElevatedTower, Assert.Single(game.Turrets).Cell);
    }

    [Fact]
    public void ABodyInASupportCellDefersTheWholeTowerWithoutTrappingTheZombie()
    {
        var game = SavedElevatedTower();
        var zombie = new Zombie { Id = 90000, WaveIndex = -1, Position = new Cell(8, 17, 8).Center };
        game.Zombies.Add(zombie);
        int currency = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Empty(game.Turrets);
        Assert.Empty(game.World.Barriers);
        Assert.Equal(3, game.Inventory.DirtCount);
        Assert.Equal(currency, game.Economy.Currency);
        Assert.Equal(zombie.MaxBodyHp, zombie.BodyHp);
        game.Zombies.Clear();
        game.Update(.36f);
        AssertPedestal(game);
        Assert.Equal(ElevatedTower, Assert.Single(game.Turrets).Cell);
    }

    [Fact]
    public void RestorationCanRebuildThePedestalDuringCombat()
    {
        var game = SavedElevatedTower();
        Assert.True(game.StartLevel());

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Equal(GamePhase.Combat, game.Phase);
        AssertPedestal(game);
        Assert.Equal(ElevatedTower, Assert.Single(game.Turrets).Cell);
        Assert.Equal(0, game.Inventory.DirtCount);
    }

    [Fact]
    public void RestorationCanExcavateTheSavedCellDuringCombat()
    {
        var game = SavedGroundTower();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        PlaceTerrain(game.World, GroundTower, Material.Dirt);
        Assert.True(game.StartLevel());

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.Equal(Material.Air, game.World.GetMaterial(GroundTower));
        Assert.Equal(GroundTower, Assert.Single(game.Turrets).Cell);
        Assert.Equal(1, game.Inventory.DirtCount);
    }

    [Fact]
    public void SupportDisplacementCannotLeaveHalfBuiltTerrainWhenTheTowerWouldTrapLiquid()
    {
        var game = NewGame();
        var tower = GroundTower + new Cell(0, 1, 0);
        Assert.True(game.Inventory.Add(Material.Dirt));
        Assert.True(game.BuildBarrier(GroundTower));
        Assert.True(game.Deploy(PieceKind.Pawn, tower));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(tower));
        Assert.True(game.Remove(GroundTower));
        Assert.True(game.Inventory.Add(Material.Stone, 10));
        foreach (var offset in new Cell[] { new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1) })
            for (int y = 16; y <= 17; y++)
                Assert.True(game.BuildBarrier(new Cell(8, y, 8) + offset, Material.Stone));
        Assert.True(game.BuildBarrier(new Cell(7, 18, 8), Material.Stone));
        Assert.True(game.BuildBarrier(new Cell(8, 18, 8), Material.Stone));
        Assert.True(game.Liquids.TryAdd(GroundTower, LiquidKind.Water, 10));
        int currency = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Empty(game.Turrets);
        Assert.Null(game.World.GetBarrier(GroundTower));
        Assert.Equal(1, game.Inventory.DirtCount);
        Assert.Equal(currency, game.Economy.Currency);
        Assert.Equal(10, game.Liquids.GetUnits(GroundTower, LiquidKind.Water));
        Assert.Equal(0, game.Liquids.GetTotalUnits(tower));
        Assert.True(game.Remove(new Cell(8, 18, 8)));
        game.Update(.36f);
        Assert.Equal(tower, Assert.Single(game.Turrets).Cell);
        Assert.Equal(Material.Dirt, game.World.GetBarrier(GroundTower)!.Content);
        Assert.Equal(10, game.Liquids.Cells.Sum(cell => cell.Water) + game.Liquids.GetEscapedUnits(LiquidKind.Water));
    }

    [Fact]
    public void FailedTowerDisplacementRestoresDamagedPairedObsidianAndEveryRecoveredLayer()
    {
        var game = NewGame();
        var tower = GroundTower + new Cell(0, 1, 0);
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        Assert.True(game.Inventory.Add(Material.Dirt));
        Assert.True(game.BuildBarrier(GroundTower));
        Assert.True(game.Deploy(PieceKind.Pawn, tower));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(tower));
        Assert.True(game.Remove(GroundTower));
        Assert.True(game.Inventory.Add(Material.Stone, 10));
        foreach (var offset in new Cell[] { new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1) })
            for (int y = 16; y <= 17; y++)
                Assert.True(game.BuildBarrier(new Cell(8, y, 8) + offset, Material.Stone));
        Assert.True(game.BuildBarrier(new Cell(7, 18, 8), Material.Stone));
        Assert.True(game.BuildBarrier(new Cell(8, 18, 8), Material.Stone));
        Assert.True(game.Liquids.TryAdd(GroundTower, LiquidKind.Water, 10));
        var crust = new ObsidianCell(tower, 3, CrustFace.West, 11, OppositeUnits: 1, OppositeHp: 3);
        var liquidState = game.Liquids.ExportState();
        liquidState.Obsidian.Add(crust);
        game.Liquids.RestoreState(liquidState);
        Assert.True(game.Inventory.Add(Material.Obsidian, 4));
        var inventory = Enum.GetValues<Material>().Select(game.Inventory.GetCount).ToArray();
        var liquids = game.Liquids.Cells.ToArray();
        int currency = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Empty(game.Turrets);
        Assert.True(game.IsAutoRestoringFormation);
        Assert.Contains("displaced liquid", game.FormationRestoreStatus);
        Assert.Equal(crust, game.World.GetObsidian(tower));
        Assert.Equal(crust.Parts, game.World.GetObsidian(tower)!.Parts);
        Assert.Null(game.World.GetBarrier(GroundTower));
        Assert.Equal(inventory, Enum.GetValues<Material>().Select(game.Inventory.GetCount).ToArray());
        Assert.Equal(liquids, game.Liquids.Cells.ToArray());
        Assert.Equal(0, game.Liquids.GetEscapedUnits(LiquidKind.Water));
        Assert.Equal(currency, game.Economy.Currency);
        Assert.True(game.Inventory.HasPickaxe);
    }

    [Fact]
    public void FailureAtTheThirdSupportRollsBackTheFirstTwoAndTheirLiquidDisplacement()
    {
        var game = SavedElevatedTower();
        Assert.True(game.Inventory.Add(Material.Stone, 89));
        // A three-cell-wide sealed chamber leaves every pillar cell's side
        // neighbors empty, so all three supports really must be constructed.
        for (int y = 16; y <= 19; y++)
            for (int x = 6; x <= 10; x++)
                for (int z = 6; z <= 10; z++)
                    if (x is 6 or 10 || z is 6 or 10)
                        Assert.True(game.BuildBarrier(new Cell(x, y, z), Material.Stone));
        for (int x = 6; x <= 10; x++)
            for (int z = 6; z <= 10; z++)
                Assert.True(game.BuildBarrier(new Cell(x, 20, z), Material.Stone));
        for (int y = 16; y <= 19; y++)
            for (int x = 7; x <= 9; x++)
                for (int z = 7; z <= 9; z++)
                {
                    var cell = new Cell(x, y, z);
                    if (cell != GroundTower && cell != ElevatedTower)
                        Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Water, 10));
                }
        int currency = game.Economy.Currency;

        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.36f);

        Assert.Empty(game.Turrets);
        Assert.True(game.IsAutoRestoringFormation);
        Assert.Contains($"support at {new Cell(8, 18, 8)}", game.FormationRestoreStatus);
        for (int y = 16; y <= 18; y++) Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(8, y, 8)));
        Assert.Equal(3, game.Inventory.DirtCount);
        Assert.Equal(0, game.Inventory.StoneCount);
        Assert.Equal(89, game.World.Barriers.Count());
        Assert.Equal(340, game.Liquids.Cells.Sum(cell => cell.Water));
        Assert.Equal(0, game.Liquids.GetEscapedUnits(LiquidKind.Water));
        Assert.Equal(currency, game.Economy.Currency);
    }

    private static GameSession NewGame()
    {
        var game = new GameSession();
        game.Economy.Credit(3000);
        return game;
    }

    private static GameSession SavedGroundTower()
    {
        var game = NewGame();
        Assert.True(game.Deploy(PieceKind.Pawn, GroundTower));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(GroundTower));
        return game;
    }

    private static GameSession SavedElevatedTower()
    {
        var game = NewGame();
        Assert.True(game.Inventory.Add(Material.Dirt, 3));
        for (int y = 16; y < ElevatedTower.Y; y++) Assert.True(game.BuildBarrier(new Cell(8, y, 8)));
        Assert.True(game.Deploy(PieceKind.Pawn, ElevatedTower));
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(ElevatedTower));
        for (int y = ElevatedTower.Y - 1; y >= 16; y--) Assert.True(game.Remove(new Cell(8, y, 8)));
        return game;
    }

    private static void AssertPedestal(GameSession game)
    {
        for (int y = 16; y < ElevatedTower.Y; y++)
            Assert.Equal(Material.Dirt, game.World.GetBarrier(new Cell(8, y, 8))!.Content);
    }

    private static void PlaceTerrain(WorldGrid world, Cell cell, Material material) => Assert.True((bool)typeof(WorldGrid)
        .GetMethod("RestoreTerrainPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(world, [cell, material])!);
}
