using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class FallAnimationTests
{
    [Fact]
    public void DistanceCurveAcceleratesThroughOneBlockThenContinuesAtFiveBlocksPerSecond()
    {
        Assert.Equal(0, FallAnimations.DistanceAfter(0));
        Assert.Equal(.03125f, FallAnimations.DistanceAfter(.5f), 5);
        Assert.Equal(1, FallAnimations.DistanceAfter(1));
        Assert.Equal(6, FallAnimations.DistanceAfter(2));
        Assert.Equal(11, FallAnimations.DistanceAfter(3));
        var beforeSpeed = (FallAnimations.DistanceAfter(1) - FallAnimations.DistanceAfter(.999f)) / .001f;
        var afterSpeed = (FallAnimations.DistanceAfter(1.001f) - FallAnimations.DistanceAfter(1)) / .001f;
        Assert.InRange(beforeSpeed, 4.98f, 5.01f);
        Assert.InRange(afterSpeed, 4.99f, 5.01f);
    }

    [Fact]
    public void TowerGridSettlesImmediatelyWhileItsWholeSixBlockDropUsesOneContinuousTrack()
    {
        var game = Construction();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
        var turret = Assert.Single(game.Turrets);
        for (int y = 15; y >= 10; y--) Assert.True(game.Dig(new Cell(8, y, 8)));

        Assert.Equal(new Cell(8, 10, 8), turret.Cell);
        Assert.Equal(new Cell(8, 16, 8).Center, game.Falls.GetTurretPosition(turret));
        game.Update(.5f);
        Assert.Equal(16.46875f, game.Falls.GetTurretPosition(turret).Y, 4);
        game.Update(.5f);
        Assert.Equal(15.5f, game.Falls.GetTurretPosition(turret).Y, 4);
        game.Update(1);
        Assert.Equal(turret.Cell.Center, game.Falls.GetTurretPosition(turret));
        Assert.Equal(new Cell(8, 10, 8), Assert.Single(game.ExportState().Turrets).Cell);
    }

    [Fact]
    public void FallingTowerStacksKeepTheirSpacingAndFinishOneBlockInOneSecond()
    {
        var game = new GameSession();
        for (int y = 16; y <= 18; y++) Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, y, 8)));
        var upper = game.Turrets.OrderBy(t => t.Cell.Y).Skip(1).ToArray();
        Assert.True(game.Remove(new Cell(8, 16, 8)));
        Assert.Equal(new Cell(8, 16, 8), upper[0].Cell);
        Assert.Equal(new Cell(8, 17, 8), upper[1].Cell);
        game.Update(.5f);
        Assert.Equal(17.46875f, game.Falls.GetTurretPosition(upper[0]).Y, 4);
        Assert.Equal(1, game.Falls.GetTurretPosition(upper[1]).Y - game.Falls.GetTurretPosition(upper[0]).Y, 4);
        game.Update(.5f);
        Assert.All(upper, turret => Assert.Equal(turret.Cell.Center, game.Falls.GetTurretPosition(turret)));
    }

    [Fact]
    public void RemovingMoreSupportRetargetsFromTheCurrentVisualPositionWithoutRestartingAcceleration()
    {
        var game = Construction();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
        var turret = Assert.Single(game.Turrets);
        Assert.True(game.Dig(new Cell(8, 15, 8)));
        game.Update(.5f);
        var before = game.Falls.GetTurretPosition(turret);

        Assert.True(game.Dig(new Cell(8, 14, 8)));

        Assert.Equal(before, game.Falls.GetTurretPosition(turret));
        Assert.Equal(new Cell(8, 14, 8), turret.Cell);
        game.Update(.5f);
        Assert.Equal(15.5f, game.Falls.GetTurretPosition(turret).Y, 4);
        game.Update(.2f);
        Assert.Equal(turret.Cell.Center, game.Falls.GetTurretPosition(turret));
    }

    [Fact]
    public void DetachedNaturalBlocksExposeTheirFinalCellAndAnimatedMaterialWithoutChangingInventory()
    {
        var game = Construction();
        var source = new Cell(10, 15, 10);
        var destination = new Cell(10, 9, 10);
        for (int y = 9; y < 15; y++) Assert.True(game.Dig(new Cell(10, y, 10)));
        for (int y = 10; y <= 15; y++)
            foreach (var side in new[] { new Cell(1, 0, 0), new Cell(-1, 0, 0), new Cell(0, 0, 1), new Cell(0, 0, -1) })
                Assert.True(game.Dig(new Cell(10, y, 10) + side));
        var fall = Assert.Single(game.Falls.Blocks);
        int dirt = game.Inventory.DirtCount, stone = game.Inventory.StoneCount;
        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        Assert.Equal(Material.Dirt, game.World.GetMaterial(destination));
        Assert.Equal(destination, fall.Destination);
        Assert.Equal(source.Center, fall.Position);
        Assert.Equal(Material.Dirt, fall.Material);
        Assert.Null(fall.Content);
        Assert.Null(fall.Crust);
        int revision = game.Falls.Revision;
        game.Update(1);
        Assert.Equal(14.5f, Assert.Single(game.Falls.Blocks).Position.Y, 4);
        game.Update(1);
        Assert.Empty(game.Falls.Blocks);
        Assert.True(game.Falls.Revision > revision);
        Assert.Equal(dirt, game.Inventory.DirtCount);
        Assert.Equal(stone, game.Inventory.StoneCount);
    }

    [Fact]
    public void BarrierAndObsidianTracksRetainTheirRenderingMetadata()
    {
        var barrierGame = Construction();
        Assert.True(barrierGame.Inventory.Add(Material.Stone));
        Assert.True(barrierGame.BuildBarrier(new Cell(12, 16, 12), Material.Stone));
        Assert.True(barrierGame.Dig(new Cell(12, 15, 12)));
        var barrier = Assert.Single(barrierGame.Falls.Blocks);
        Assert.Equal(Material.Barrier, barrier.Material);
        Assert.Equal(Material.Stone, barrier.Content);
        Assert.Equal(new Cell(12, 15, 12), barrier.Destination);

        var crustGame = new GameSession();
        crustGame.Liquids.RestoreState(new LiquidState { Obsidian = [new(new Cell(8, 20, 8), 2, CrustFace.West, 10)] });
        crustGame.Update(.001f);
        var crust = Assert.Single(crustGame.Falls.Blocks);
        Assert.Equal(Material.Obsidian, crust.Material);
        Assert.Equal(new Cell(8, 16, 8), crust.Destination);
        Assert.Equal(new Cell(8, 20, 8).Center, crust.Position);
        Assert.Equal(2, crust.Crust!.Units);
        Assert.Equal(CrustFace.West, crust.Crust.Face);
        Assert.Equal(crust.Destination, crust.Crust.Cell);
    }

    [Fact]
    public void PhysicalZombieFallUsesTheSameCurveAndStopsOnTheActualThinCrustSurface()
    {
        var game = Battle();
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(new Cell(8, 16, 8), 2, CrustFace.Bottom, 10)] });
        var zombie = BeginAt(game, new Vector3(8.5f, 19.7f, 8.5f));
        zombie.FaceToward(-Vector3.UnitZ);
        game.Update(.5f);
        Assert.InRange(Math.Abs(zombie.Position.Y - 19.66875f), 0, .00001f);
        game.Update(.5f);
        Assert.Equal(18.7f, zombie.Position.Y, 4);
        Assert.Equal(-Vector3.UnitZ, zombie.Facing);
        game.Update(.4f);
        Assert.Equal(16.7f, zombie.Position.Y, 4);
        Assert.False(game.World.IsSolidAt(zombie.Position - Vector3.UnitY * .49f));
        Assert.True(game.World.IsSolidAt(zombie.Position - Vector3.UnitY * .51f));
    }

    [Fact]
    public void PhysicalZombieFallRetargetsWhenItsLandingSupportIsDestroyed()
    {
        var game = Battle();
        var support = new Cell(8, 16, 8);
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(support, 10, CrustFace.Bottom, 50)] });
        var zombie = BeginAt(game, new Vector3(8.5f, 23.5f, 8.5f));
        game.Update(.5f);
        var before = zombie.Position;
        Assert.True(game.World.DamageObsidian(support, 50));
        Assert.Equal(before, zombie.Position);
        game.Update(.5f);
        Assert.Equal(22.5f, zombie.Position.Y, 4);
        game.Update(1);
        Assert.Equal(17.5f, zombie.Position.Y, 4);
        game.Update(.2f);
        Assert.Equal(16.5f, zombie.Position.Y, 4);
    }

    [Fact]
    public void ASmallDropFinishesEarlyAndResetClearsPresentationTracks()
    {
        var game = Battle();
        var zombie = BeginAt(game, new Vector3(8.5f, 17, 8.5f));
        game.Update(.8f);
        Assert.True(zombie.Position.Y > 16.5f);
        game.Update(.08f);
        Assert.Equal(16.5f, zombie.Position.Y, 4);

        var construction = Construction();
        Assert.True(construction.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
        var turret = Assert.Single(construction.Turrets);
        Assert.True(construction.Dig(new Cell(8, 15, 8)));
        Assert.NotEqual(turret.Cell.Center, construction.Falls.GetTurretPosition(turret));
        construction.Reset();
        Assert.Empty(construction.Falls.Blocks);
        Assert.Equal(turret.Cell.Center, construction.Falls.GetTurretPosition(turret));
    }

    [Fact]
    public void InterruptedJumpLandsOnTheBankUnderItsBodyEdgeAndWalksBackOntoTheRoute()
    {
        var game = Battle();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        for (int z = 0; z < WorldGrid.Size; z++) Assert.True(game.Dig(new Cell(8, 15, z)));
        var landingSupport = new Cell(9, 15, 15);
        Assert.True(game.Dig(landingSupport));
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(landingSupport, 10, CrustFace.Bottom, 50)] });
        var start = new Cell(7, 16, 15);
        var zombie = BeginAt(game, start.Center);
        zombie.Lane = 0;
        game.Update(.01f);
        Assert.Equal(new Cell(9, 16, 15), zombie.NextCell);
        game.Update(.7f / 1.5f); // Same jump progress after the unarmed movement boost.
        Assert.InRange(zombie.Position.X, 8.09f, 8.1f);

        Assert.True(game.World.DamageObsidian(landingSupport, 50));
        game.Update(.01f);
        Assert.Null(zombie.NextCell);
        bool reachedBankCenter = false;
        for (int step = 0; step < 250; step++)
        {
            var before = zombie.Position;
            game.Update(.01f);
            Assert.True(zombie.Hp > 0);
            Assert.InRange(Math.Abs(zombie.Position.X - before.X), 0, zombie.MovementSpeed * .01f + .00001f);
            Assert.True(zombie.Position.Y >= 16.4999f);
            foreach (float x in new[] { -.2f, .2f })
                foreach (float z in new[] { -.2f, .2f })
                    Assert.False(game.World.IsSolidAt(zombie.Position + new Vector3(x, -.39f, z)));
            if (Vector3.DistanceSquared(zombie.Position, start.Center) > .000001f) continue;
            reachedBankCenter = true;
            break;
        }
        Assert.True(reachedBankCenter, $"Interrupted jumper stalled at {zombie.Position}.");
        game.Update(.01f);
        Assert.NotNull(zombie.NextCell);
    }

    private static GameSession Construction()
    {
        var game = new GameSession();
        game.Economy.Credit(200);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.BuyTool(ToolKind.Pickaxe));
        return game;
    }

    private static GameSession Battle()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Kind = ZombieKind.Basic }] }] });
        return new GameSession(12345, levels);
    }

    private static Zombie BeginAt(GameSession game, Vector3 position)
    {
        Assert.True(game.StartLevel());
        game.Update(.001f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = position;
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        zombie.Cooldown = 10000;
        return zombie;
    }
}
