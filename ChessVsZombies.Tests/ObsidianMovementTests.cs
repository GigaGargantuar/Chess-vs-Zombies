using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class ObsidianMovementTests
{
    [Fact]
    public void AReactionGrowingThroughTheTorsoIsAttackedWithoutInstantlyKillingTheOccupant()
    {
        var game = Battle(ZombieKind.Armored);
        var cell = new Cell(8, 16, 8);
        var zombie = BeginAt(game, cell.Center);
        game.Liquids.RestoreState(new LiquidState { Cells = [new(cell, 0, 4, 6)] });

        game.Update(.1f);

        var crust = Assert.IsType<ObsidianCell>(game.World.GetObsidian(cell));
        Assert.Equal(2, crust.Units);
        Assert.Equal(4, crust.OffsetUnits);
        Assert.Equal(9, crust.Hp);
        Assert.True(zombie.BodyHp > 0);
        Assert.Equal(cell.Center, zombie.Position);
        Assert.Null(zombie.NextCell);
    }

    [Fact]
    public void AnInternalSlabMissedByEveryCornerIsBrokenAtTheNormalAttackCadence()
    {
        var game = Battle(ZombieKind.Armored);
        var cell = new Cell(8, 16, 8);
        var zombie = BeginAt(game, cell.Center);
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(cell, 2, CrustFace.Bottom, 3, 4)] });
        foreach (float x in new[] { -.2f, .2f })
            foreach (float y in new[] { -.39f, .39f })
                foreach (float z in new[] { -.2f, .2f })
                    Assert.False(game.World.IsSolidAt(zombie.Position + new Vector3(x, y, z)));
        Assert.True(game.World.IsSolidAt(zombie.Position));

        game.Update(.01f);
        Assert.Equal(2, game.World.GetObsidian(cell)!.Hp);
        Assert.Equal(cell.Center, zombie.Position);
        Assert.Equal(10, zombie.BodyHp);
        game.Update(.5f);
        Assert.Equal(2, game.World.GetObsidian(cell)!.Hp);
        game.Update(.51f);
        Assert.Equal(1, game.World.GetObsidian(cell)!.Hp);
        game.Update(1.01f);
        Assert.Null(game.World.GetObsidian(cell));
        game.Update(.2f);
        Assert.NotEqual(cell.Center, zombie.Position);
        Assert.Equal(10, zombie.BodyHp);
    }

    [Fact]
    public void AZombieOnAFractionalSlabStepsDownToTheGroundAndContinuesItsRoute()
    {
        var game = Battle();
        var cell = new Cell(8, 16, 8);
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(cell, 6, CrustFace.Bottom, 30)] });
        var start = new Vector3(8.5f, 17.1f, 8.5f);
        var zombie = BeginAt(game, start);

        for (int step = 0; step < 50; step++)
        {
            var before = zombie.Position;
            game.Update(.05f);
            Assert.InRange(Vector3.Distance(before, zombie.Position), 0, .043f);
            Assert.True(zombie.Hp > 0);
            AssertBodyDoesNotOverlapCrust(game, zombie);
        }

        Assert.True(Vector3.Distance(start, zombie.Position) > 1);
        Assert.Equal(16.5f, zombie.Position.Y, 4);
        Assert.Equal(30, game.World.GetObsidian(cell)!.Hp);
    }

    [Fact]
    public void ABuoyantZombieBreaksTheCrustBlockingItsRiseInsteadOfReturningForever()
    {
        var game = Battle(ZombieKind.ArmedArmored, GearTier.Diamond);
        Assert.True(game.BuyTool(ToolKind.Shovel));
        for (int y = 14; y <= 15; y++) Assert.True(game.Dig(new Cell(8, y, 8)));
        var crustCell = new Cell(8, 15, 8);
        game.Liquids.RestoreState(new LiquidState
        {
            Cells = [new(new Cell(8, 14, 8), 0, 10, 0), new(crustCell, 0, 4, 0, 2, CrustFace.Bottom)],
            Obsidian = [new(crustCell, 2, CrustFace.Bottom, 10, 4)]
        });
        var zombie = BeginAt(game, new Vector3(8.5f, 14.9f, 8.5f));
        Assert.False(zombie.SinksIn(LiquidKind.Mercury));

        game.Update(.1f);

        Assert.Null(game.World.GetObsidian(crustCell));
        Assert.True(zombie.Hp > 0);
        Assert.True(zombie.Position.Y < 15.4f); // Breaking it does not teleport the body above it.
        game.Update(.4f);
        Assert.True(zombie.Position.Y > 15.5f);
        Assert.True(zombie.Hp > 0);
    }

    private static void AssertBodyDoesNotOverlapCrust(GameSession game, Zombie zombie)
    {
        var half = new Vector3(.2f, .39f, .2f);
        var min = zombie.Position - half;
        var max = zombie.Position + half;
        foreach (var crust in game.World.ObsidianCells)
            Assert.False(min.X < crust.Max.X && max.X > crust.Min.X && min.Y < crust.Max.Y && max.Y > crust.Min.Y &&
                min.Z < crust.Max.Z && max.Z > crust.Min.Z, $"Body overlaps crust at {crust.Cell}: {zombie.Position}.");
    }

    private static GameSession Battle(ZombieKind kind = ZombieKind.Armed, GearTier tier = GearTier.Osmium)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Kind = kind, Tier = tier }] }] });
        return new GameSession(levels: levels);
    }

    private static Zombie BeginAt(GameSession game, Vector3 position)
    {
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = position;
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        zombie.Cooldown = 0;
        zombie.Lane = 0;
        return zombie;
    }
}
