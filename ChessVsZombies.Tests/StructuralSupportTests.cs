using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class StructuralSupportTests
{
    [Fact]
    public void TowersStackAndRemovingMiddleThenBottomSettlesTheSurvivorsWithoutChangingTheirStats()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(8, 17, 8)));
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(8, 18, 8)));
        var top = game.Turrets[2];
        top.Hp = 19;
        game.Economy.GetProgress(UpgradeKind.ATK).PullCount = 1;
        game.Economy.GetProgress(UpgradeKind.ATK).Available = 1;
        Assert.True(game.ApplyUpgradeTemplate(top.Cell, new UpgradeTemplate { Points = new() { [UpgradeKind.ATK] = 1 } }));
        int wallet = game.Economy.Currency;

        Assert.True(game.Remove(new Cell(8, 17, 8)));
        Assert.Equal(new Cell(8, 17, 8), top.Cell);
        Assert.Equal(2, game.Turrets.Count);
        Assert.True(game.Remove(new Cell(8, 16, 8)));
        Assert.Same(top, Assert.Single(game.Turrets));
        Assert.Equal(new Cell(8, 16, 8), top.Cell);
        Assert.Equal(19, top.Hp);
        Assert.Equal(2, game.Economy.GetAttack(top));
        Assert.Equal(wallet + 3, game.Economy.Currency);
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.ATK).Available);
    }

    [Fact]
    public void AZombieDestroyingTheBottomTowerCollapsesTheStackInTheSameUpdate()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Number = 1, Name = "Tower collapse", Waves =
            [new() { Groups = [new() { Count = 1, Kind = ZombieKind.Armed, Tier = GearTier.Osmium }] }] });
        var game = new GameSession(levels: levels);
        // Confine the approach so this regression exercises destruction rather
        // than the newly available safer detours and tower jumps.
        Assert.True(game.BuyTool(ToolKind.Shovel));
        for (int x = 10; x <= 15; x++) Assert.True(game.Dig(new Cell(x, 14, 15)));
        Assert.True(game.Dig(new Cell(11, 15, 15)));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 14, 15)));
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(11, 15, 15)));
        Assert.True(game.Deploy(PieceKind.Bishop, new Cell(11, 16, 15)));
        var bottom = game.Turrets[0];
        bottom.Hp = 1; // Make the direct breach cheaper than an open detour.
        var middle = game.Turrets[1];
        var top = game.Turrets[2];
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = new Cell(10, 14, 15).Center;
        zombie.NextCell = null;
        zombie.Cooldown = 0;
        zombie.Lane = 0;
        int currency = game.Economy.Currency;
        game.Update(.01f);
        Assert.DoesNotContain(bottom, game.Turrets);
        Assert.Equal(new Cell(11, 14, 15), middle.Cell);
        Assert.Equal(new Cell(11, 15, 15), top.Cell);
        Assert.Equal(currency, game.Economy.Currency);
    }

    [Fact]
    public void SideConnectedBlocksKeepAHoleAndAnIsolatedSurvivorFalls()
    {
        var game = Blocks();
        Assert.True(game.BuildBarrier(new Cell(12, 16, 12)));
        Assert.True(game.BuildBarrier(new Cell(12, 17, 12)));
        Assert.True(game.BuildBarrier(new Cell(13, 17, 12)));
        game.World.GetBarrier(new Cell(12, 17, 12))!.Hp = 37;
        Assert.True(game.Remove(new Cell(12, 16, 12)));
        Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(12, 16, 12)));
        Assert.NotNull(game.World.GetBarrier(new Cell(12, 17, 12)));
        Assert.NotNull(game.World.GetBarrier(new Cell(13, 17, 12)));
        Assert.True(game.Remove(new Cell(13, 17, 12)));
        var survivor = Assert.Single(game.World.Barriers);
        Assert.Equal(new Cell(12, 16, 12), survivor.Cell);
        Assert.Equal(37, survivor.Hp);
    }

    [Fact]
    public void ABlockAttachedOnlyAboveRemainsSuspendedUntilThatNeighborIsRemoved()
    {
        var game = Blocks();
        for (int y = 16; y <= 18; y++) Assert.True(game.BuildBarrier(new Cell(12, y, 12)));
        Assert.True(game.Remove(new Cell(12, 16, 12)));
        Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(12, 16, 12)));
        Assert.NotNull(game.World.GetBarrier(new Cell(12, 17, 12)));
        Assert.True(game.Remove(new Cell(12, 18, 12)));
        Assert.Equal(new Cell(12, 16, 12), Assert.Single(game.World.Barriers).Cell);
    }

    [Fact]
    public void DiagonalBlocksAndSidewaysTowersDoNotCountAsPlacementSupport()
    {
        var game = Blocks();
        Assert.True(game.BuildBarrier(new Cell(12, 16, 12)));
        Assert.False(game.BuildBarrier(new Cell(13, 17, 13)));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 17, 8)));
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(9, 17, 8)));
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(12, 17, 12)));
    }

    [Fact]
    public void TerrainOverhangKeepsItsHoleUntilItsLastFaceNeighborIsRemoved()
    {
        var game = new GameSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        var target = new Cell(8, 14, 8);
        Assert.True(game.Dig(new Cell(8, 13, 8)));
        Assert.Equal(Material.Dirt, game.World.GetMaterial(target));
        foreach (var offset in new[] { new Cell(0, 1, 0), new Cell(1, 0, 0), new Cell(-1, 0, 0), new Cell(0, 0, 1) })
        {
            Assert.True(game.Dig(target + offset));
            Assert.Equal(Material.Dirt, game.World.GetMaterial(target));
            Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(8, 13, 8)));
        }
        Assert.True(game.Dig(target + new Cell(0, 0, -1)));
        Assert.Equal(Material.Air, game.World.GetMaterial(target));
        Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(new Cell(8, 13, 8)));
        Assert.Equal(6, game.Inventory.DirtCount);
        Assert.Null(game.World.GetBarrier(new Cell(8, 13, 8)));
    }

    [Fact]
    public void FallingTowerDisplacesLiquidWithoutLosingUnitsOrSavingAnOverlap()
    {
        var game = new GameSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Dig(new Cell(8, 14, 8)));
        Assert.True(game.BuyLiquid(LiquidKind.Water));
        Assert.True(game.PourLiquid(new Cell(8, 14, 8), LiquidKind.Water));
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(8, 16, 8)));
        Assert.True(game.Dig(new Cell(8, 15, 8)));
        Assert.Equal(new Cell(8, 14, 8), Assert.Single(game.Turrets).Cell);
        Assert.Equal(0, game.Liquids.GetTotalUnits(new Cell(8, 14, 8)));
        Assert.Equal(10, game.Liquids.GetTotalUnits(new Cell(8, 15, 8)));
        Assert.Equal(10, game.ExportState().Liquids!.Cells.Sum(c => c.Water));
    }

    [Fact]
    public void SettlingDoesNotCrushAZombieInTheEmptyPortionOfAnObsidianCell()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        var zombie = new Zombie { Id = 1, Position = cell.Center, WaveIndex = -1 };
        game.Zombies.Add(zombie);
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(cell, 2, CrustFace.West, 10)] });
        game.Update(.01f);
        Assert.Equal(10, zombie.Hp);
        Assert.Equal(cell.Center, zombie.Position);
    }

    [Fact]
    public void ObsidianCellsSupportGridPiecesWhileTheirEmptySpaceRetainsPreciseBodyCollision()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(cell, 2, CrustFace.Bottom, 10)] });
        Assert.False(game.World.IsSolidAt(cell.Center));
        Assert.True(game.HasTowerSupport(cell + new Cell(0, 1, 0)));
        Assert.True(game.Deploy(PieceKind.Pawn, cell + new Cell(0, 1, 0)));
        game.Update(.01f);
        Assert.Equal(cell + new Cell(0, 1, 0), Assert.Single(game.Turrets).Cell);
    }

    private static GameSession Blocks()
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Dirt, 10));
        return game;
    }
}
