using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class JumpTraversalTests
{
    [Fact]
    public void ZombieJumpsAcrossAOneCellGapWithoutFallingIntoIt()
    {
        var game = NewSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        for (int z = 0; z < WorldGrid.Size; z++) Assert.True(game.Dig(new Cell(13, 15, z)));
        var start = new Cell(12, 16, 15);
        var landing = new Cell(14, 16, 15);
        var zombie = StartAt(game, start);

        game.Update(.01f);
        Assert.Equal(landing, zombie.NextCell);
        float peak = FollowJump(game, zombie, start, landing);

        Assert.InRange(peak, 17.49f, 17.51f);
        Assert.Equal(landing.Center, zombie.Position);
        Assert.Empty(game.World.DamagedTerrain);
        Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(13, 15, 15)));
    }

    [Fact]
    public void ZombieJumpsOverAnIntactTowerWithItsWholeBodyClear()
    {
        var game = NewSession();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 16, 15)));
        var tower = Assert.Single(game.Turrets);
        tower.Cooldown = 100;
        var start = new Cell(10, 16, 15);
        var landing = new Cell(12, 16, 15);
        var zombie = StartAt(game, start);
        float towerHp = tower.Hp;

        game.Update(.01f);
        Assert.Equal(landing, zombie.NextCell);
        float peak = FollowJump(game, zombie, start, landing);

        Assert.InRange(peak, 18.49f, 18.51f);
        Assert.Equal(landing.Center, zombie.Position);
        Assert.Equal(towerHp, tower.Hp);
    }

    [Fact]
    public void AStackedTowerBlocksTheOneTowerJumpArc()
    {
        var game = NewSession();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 16, 15)));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 17, 15)));
        foreach (var tower in game.Turrets) tower.Cooldown = 100;
        var zombie = StartAt(game, new Cell(10, 16, 15));

        game.Update(.01f);

        Assert.NotEqual(new Cell(12, 16, 15), zombie.NextCell);
        for (int step = 0; step < 50; step++)
        {
            game.Update(.02f);
            AssertBodyClear(game, zombie);
        }
    }

    [Fact]
    public void ThinObsidianInTheHeadroomBlocksTheTowerJump()
    {
        var game = NewSession();
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian =
            [
                new(new Cell(10, 16, 14), 10, CrustFace.Bottom, 50),
                new(new Cell(10, 17, 14), 10, CrustFace.Bottom, 50),
                new(new Cell(10, 17, 15), 1, CrustFace.Top, 5)
            ]
        });
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 16, 15)));
        Assert.Single(game.Turrets).Cooldown = 100;
        var zombie = StartAt(game, new Cell(10, 16, 15));

        game.Update(.01f);

        Assert.NotEqual(new Cell(12, 16, 15), zombie.NextCell);
        AssertBodyClear(game, zombie);
        Assert.Equal(1, game.World.GetObsidian(new Cell(10, 17, 15))!.Units);
    }

    [Fact]
    public void TwoCellGapCannotBeJumpedWithoutASupportedLanding()
    {
        var game = NewSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        for (int x = 12; x <= 13; x++)
            for (int z = 0; z < WorldGrid.Size; z++) Assert.True(game.Dig(new Cell(x, 15, z)));
        var zombie = StartAt(game, new Cell(11, 16, 15));

        game.Update(.01f);

        Assert.NotEqual(new Cell(13, 16, 15), zombie.NextCell);
        Assert.NotEqual(new Cell(14, 16, 15), zombie.NextCell);
        Assert.True(zombie.NextCell.HasValue);
        Assert.InRange((zombie.NextCell.Value - new Cell(11, 16, 15)).Manhattan, 1, 2);
    }

    [Fact]
    public void LosingTheLandingDuringAJumpStartsFallingAtTheCurrentPosition()
    {
        var game = NewSession();
        foreach (int x in new[] { 8, 10, 11 }) Assert.True(game.Deploy(PieceKind.Pawn, new Cell(x, 16, 15)));
        foreach (var tower in game.Turrets) tower.Cooldown = 100;
        Assert.True(game.Inventory.Add(Material.Dirt, 2));
        for (int x = 12; x <= 13; x++) Assert.True(game.BuildBarrier(new Cell(x, 16, 15)));
        var landing = new Cell(10, 17, 15);
        var support = game.TrySelectTurret(landing + new Cell(0, -1, 0))!;
        var zombie = StartAt(game, new Cell(8, 17, 15));
        game.Update(.01f);
        Assert.Equal(landing, zombie.NextCell);
        game.Update(.5f);
        var airborne = zombie.Position;
        Assert.True(airborne.Y > 17.5f);

        support.Hp = 0;
        game.Update(.01f);

        Assert.Null(zombie.NextCell);
        Assert.True(zombie.Hp > 0);
        Assert.Equal(airborne.X, zombie.Position.X);
        Assert.Equal(airborne.Z, zombie.Position.Z);
        Assert.InRange(zombie.Position.Y, airborne.Y - .01f, airborne.Y);
        Assert.NotEqual(landing.Center, zombie.Position);
        AssertBodyClear(game, zombie);
    }

    private static GameSession NewSession()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Name = "Jump traversal",
            Waves = [new() { SpawnInterval = .01f, Groups = [new() { Count = 1, Kind = ZombieKind.Basic, Tier = GearTier.Wood }] }]
        });
        return new GameSession(12345, levels);
    }

    private static Zombie StartAt(GameSession game, Cell start)
    {
        Assert.True(game.StartLevel(), game.Message);
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = start.Center;
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        zombie.Cooldown = 0;
        zombie.Lane = 0;
        return zombie;
    }

    private static float FollowJump(GameSession game, Zombie zombie, Cell start, Cell landing)
    {
        float peak = zombie.Position.Y;
        for (int step = 0; step < 150 && zombie.NextCell.HasValue; step++)
        {
            var before = zombie.Position;
            game.Update(.02f);
            Assert.True(zombie.Hp > 0);
            Assert.InRange(zombie.Position.Y, start.Center.Y - .0001f, start.Center.Y + 2.001f);
            Assert.InRange(zombie.Position.X, before.X - .0001f, landing.Center.X + .0001f);
            Assert.InRange(Vector3.Distance(before, zombie.Position), 0, .08f);
            AssertBodyClear(game, zombie);
            peak = Math.Max(peak, zombie.Position.Y);
        }
        Assert.Null(zombie.NextCell);
        return peak;
    }

    private static void AssertBodyClear(GameSession game, Zombie zombie)
    {
        foreach (float x in new[] { -.2f, .2f })
            foreach (float y in new[] { -.39f, .39f })
                foreach (float z in new[] { -.2f, .2f })
                {
                    var point = zombie.Position + new Vector3(x, y, z);
                    Assert.False(game.World.IsSolidAt(point), $"Body intersects terrain at {point}.");
                    var cell = new Cell((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z));
                    Assert.Null(game.TrySelectTurret(cell));
                }
    }
}
