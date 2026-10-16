using ChessVsZombies.Core;
using System.Reflection;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class LiquidPlacementTests
{
    private static readonly Cell WetCell = new(8, 16, 8);

    [Theory]
    [InlineData(LiquidKind.Water)]
    [InlineData(LiquidKind.Mercury)]
    [InlineData(LiquidKind.Lava)]
    public void FullBlockDisplacesLiquidAndPreviewLeavesStateUntouched(LiquidKind kind)
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Stone));
        Assert.True(game.Liquids.TryAdd(WetCell, kind, 10));
        long revision = game.Liquids.Revision;

        Assert.True(game.CanBuildBarrier(WetCell, Material.Stone));
        Assert.Equal(revision, game.Liquids.Revision);
        Assert.Equal(10, game.Liquids.GetUnits(WetCell, kind));
        Assert.True(game.BuildBarrier(WetCell, Material.Stone), game.Message);

        Assert.Equal(0, game.Liquids.GetTotalUnits(WetCell));
        Assert.Equal(Material.Stone, game.World.GetBarrier(WetCell)!.Content);
        Assert.Equal(10, Total(game, kind));
        Assert.Equal(0, game.Inventory.StoneCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void DeployingIntoLiquidRetainsAtMostFiveUnitsAndDisplacesOnlyTheOverflow(int units)
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(WetCell, LiquidKind.Water, units));
        Assert.True(game.CanDeploy(PieceKind.Pawn, WetCell));
        Assert.Equal(units, game.Liquids.GetTotalUnits(WetCell));

        Assert.True(game.Deploy(PieceKind.Pawn, WetCell), game.Message);

        Assert.Equal(Math.Min(5, units), game.Liquids.GetTotalUnits(WetCell));
        Assert.Equal(units, Total(game, LiquidKind.Water));
        Assert.Equal(WetCell, Assert.Single(game.Turrets).Cell);
        Assert.False(game.Liquids.TryAdd(WetCell, LiquidKind.Water, 6 - Math.Min(5, units)));
    }

    [Fact]
    public void TurretsCanReceivePoursUpToTheirSharedCapacity()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Pawn, WetCell));
        Assert.True(game.BuyLiquid(LiquidKind.Water));
        Assert.True(game.CanPourLiquid(WetCell, LiquidKind.Water, 5));
        Assert.True(game.PourLiquid(WetCell, LiquidKind.Water, 5));
        Assert.False(game.CanPourLiquid(WetCell, LiquidKind.Water, 1));
        Assert.False(game.PourLiquid(WetCell, LiquidKind.Water, 1));
        Assert.Equal(5, game.Liquids.GetTotalUnits(WetCell));
        Assert.Equal(5, game.LiquidInventory[LiquidKind.Water]);
    }

    [Fact]
    public void FluidFlowCanEnterTurretsButCannotOverfillTheirCells()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Pawn, WetCell));
        Assert.True(game.Liquids.TryAdd(WetCell + new Cell(0, 1, 0), LiquidKind.Water, 10));

        game.Update(.1f);

        Assert.InRange(game.Liquids.GetTotalUnits(WetCell), 1, 5);
        Assert.Equal(10, Total(game, LiquidKind.Water));
    }

    [Fact]
    public void PlacingObsidianInLiquidDisplacesItWithoutLosingMaterial()
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Obsidian));
        Assert.True(game.Liquids.TryAdd(WetCell, LiquidKind.Water, 10));

        Assert.True(game.CanBuildObsidian(WetCell, CrustFace.Bottom));
        Assert.True(game.BuildObsidian(WetCell, CrustFace.Bottom), game.Message);

        Assert.Equal(1, game.World.GetObsidian(WetCell)!.Units);
        Assert.Equal(10, Total(game, LiquidKind.Water));
        Assert.Equal(0, game.Inventory.ObsidianLayers);
    }

    [Fact]
    public void EnclosedLiquidPreventsPlacementAtomicallyUntilAnOutletIsOpened()
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Stone, 7));
        foreach (var offset in new Cell[] { new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1) })
            Assert.True(game.BuildBarrier(WetCell + offset, Material.Stone));
        Assert.True(game.BuildBarrier(WetCell + new Cell(1, 1, 0), Material.Stone));
        Assert.True(game.BuildBarrier(WetCell + new Cell(0, 1, 0), Material.Stone));
        Assert.True(game.Liquids.TryAdd(WetCell, LiquidKind.Water, 10));
        long revision = game.Liquids.Revision;
        int currency = game.Economy.Currency;

        Assert.False(game.CanBuildBarrier(WetCell, Material.Stone));
        Assert.False(game.BuildBarrier(WetCell, Material.Stone));
        Assert.False(game.CanDeploy(PieceKind.Pawn, WetCell));
        Assert.False(game.Deploy(PieceKind.Pawn, WetCell));
        Assert.Equal(revision, game.Liquids.Revision);
        Assert.Equal(currency, game.Economy.Currency);
        Assert.Equal(1, game.Inventory.StoneCount);
        Assert.Equal(10, game.Liquids.GetTotalUnits(WetCell));
        Assert.Empty(game.Turrets);
        Assert.True(game.Remove(WetCell + new Cell(0, 1, 0)));
        Assert.True(game.BuildBarrier(WetCell, Material.Stone), game.Message);
        Assert.Equal(10, Total(game, LiquidKind.Water));
    }

    [Fact]
    public void BuilderPlacesItsRealCarriedBlockIntoAWetFloor()
    {
        var game = new GameSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        var floor = new Cell(9, 15, 8);
        Assert.True(game.Dig(floor));
        Assert.True(game.Liquids.TryAdd(floor, LiquidKind.Water, 10));
        var from = new Cell(8, 16, 8);
        var zombie = new Zombie { Id = 101, WaveIndex = -1 };
        zombie.Position = zombie.StandAt(from);
        game.Zombies.Add(zombie);

        bool placed = (bool)Invoke(game, "TryBuildBridge", zombie, from, floor + new Cell(0, 1, 0))!;

        Assert.True(placed);
        Assert.Equal(0, zombie.CarriedBlocks);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(floor));
        Assert.Equal(0, game.Liquids.GetTotalUnits(floor));
        Assert.Equal(10, Total(game, LiquidKind.Water));
    }

    [Fact]
    public void PendingDeathDropDisplacesLiquidInsteadOfWaitingAboveTheWetCell()
    {
        var game = new GameSession();
        Assert.True(game.Liquids.TryAdd(WetCell, LiquidKind.Water, 10));
        var drops = (List<BlockDrop>)typeof(GameSession).GetField("_pendingBlockDrops", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        drops.Add(new BlockDrop(WetCell, Material.Stone, 123, true));

        Invoke(game, "ResolveBlockDrops");

        Assert.Empty(game.PendingBlockDrops);
        var block = Assert.Single(game.World.Barriers);
        Assert.Equal(WetCell, block.Cell);
        Assert.Equal(Material.Stone, block.Content);
        Assert.Equal(123, block.Hp);
        Assert.Equal(0, game.Liquids.GetTotalUnits(WetCell));
        Assert.Equal(10, Total(game, LiquidKind.Water));
    }

    private static object? Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static long Total(GameSession game, LiquidKind kind) =>
        game.Liquids.Cells.Sum(cell => (long)game.Liquids.GetUnits(cell.Cell, kind)) + game.Liquids.GetEscapedUnits(kind);
}
