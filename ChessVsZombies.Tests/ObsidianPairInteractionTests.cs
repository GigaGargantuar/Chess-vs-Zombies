using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ObsidianPairInteractionTests
{
    [Fact]
    public void AZombieBetweenPairedWallsAttacksTheWallItsMovementActuallyTouches()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(cell, 6, CrustFace.West, 30, OppositeUnits: 3, OppositeHp: 15)]
        });
        Assert.True(game.StartLevel());
        var zombie = new Zombie { Id = -10, WaveIndex = -1, Position = cell.Center,
            NextCell = cell + new Cell(1, 0, 0), Attack = 1 };
        game.Zombies.Add(zombie);

        game.Update(.02f);

        var parts = game.World.GetObsidian(cell)!.Parts.ToArray();
        Assert.Equal(15, parts.Single(part => part.Face == CrustFace.West).Hp);
        Assert.Equal(14, parts.Single(part => part.Face == CrustFace.East).Hp);
        Assert.Equal(cell.Center, zombie.Position);
    }

    [Fact]
    public void GrowingTheSelectedOppositeFacePreservesBothDurabilitiesAndMiningRecoversAllLayers()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(cell, 4, CrustFace.West, 12, OppositeUnits: 2, OppositeHp: 4)]
        });
        Assert.True(game.Inventory.Add(Material.Obsidian));

        Assert.True(game.BuildObsidian(cell, CrustFace.East), game.Message);

        var parts = game.World.GetObsidian(cell)!.Parts.ToArray();
        Assert.Equal(new ObsidianCell(cell, 2, CrustFace.West, 8), parts[0]);
        Assert.Equal(new ObsidianCell(cell, 3, CrustFace.East, 9), parts[1]);
        Assert.Equal(0, game.Inventory.ObsidianLayers);
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        Assert.True(game.Dig(cell));
        Assert.Equal(5, game.Inventory.ObsidianLayers);
        Assert.Null(game.World.GetObsidian(cell));
        Assert.Empty(game.Feedback.Hits);
    }

    [Fact]
    public void WetFallingSlabMergesWithoutOverfillingOrDeletingEitherLiquid()
    {
        var game = new GameSession();
        var lower = new Cell(8, 16, 8);
        var upper = lower + new Cell(0, 1, 0);
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(lower, 2, CrustFace.West, 8), new(upper, 2, CrustFace.East, 7)],
            Cells = [new(lower, 4, 4, 0, 2, CrustFace.West), new(upper, 6, 2, 0, 2, CrustFace.East)]
        });

        game.Update(.01f); // Settlement only, before the first fluid tick.

        Assert.Equal(4, game.World.GetObsidian(lower)!.Units);
        Assert.Null(game.World.GetObsidian(upper));
        Assert.Equal(10, game.Liquids.Cells.Sum(cell => cell.Water));
        Assert.Equal(6, game.Liquids.Cells.Sum(cell => cell.Mercury));
        Assert.All(game.Liquids.Cells, cell => Assert.InRange(cell.Water + cell.Mercury + cell.Lava + cell.ObsidianUnits, 0, 10));
        var restored = new LiquidSystem(new WorldGrid());
        restored.RestoreState(game.Liquids.ExportState());
        Assert.Equal(game.Liquids.Cells, restored.Cells);
    }
}
