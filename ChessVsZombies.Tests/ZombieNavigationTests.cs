using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class ZombieNavigationTests
{
    [Fact]
    public void CombatDestructionOpensTheTowerCellAndInvalidatesAnAlreadyClimbingZombieImmediately()
    {
        var game = NewBattle(2);
        // Keep a natural roof above the climb's headroom so climbing cannot escape
        // onto the surface. The far end has a clear stair directly under the core.
        Assert.True(game.BuyTool(ToolKind.Shovel));
        for (int x = 10; x <= 14; x++) Assert.True(game.Dig(new Cell(x, 13, 15)));
        Assert.True(game.Dig(new Cell(10, 14, 15)));
        Assert.True(game.Dig(new Cell(11, 14, 15)));
        Assert.True(game.Dig(new Cell(14, 14, 15)));
        Assert.True(game.Dig(new Cell(15, 14, 15)));
        var towerCell = new Cell(11, 13, 15);
        Assert.True(game.Deploy(PieceKind.Pawn, towerCell));
        var turret = Assert.Single(game.Turrets);
        turret.Hp = 1; // Breaching this damaged piece is shorter than walking around it.
        turret.Cooldown = 10000;
        Begin(game, 2);
        var attacker = game.Zombies[0];
        var climber = game.Zombies[1];
        Place(attacker, new Cell(10, 13, 15).Center);
        Place(climber, new Vector3(10.5f, 13.8f, 15.5f));
        climber.NextCell = towerCell + new Cell(0, 1, 0);
        climber.MoveProgress = .5f;

        game.Update(.01f);

        Assert.True(turret.Hp <= 0);
        Assert.Empty(game.Turrets);
        Assert.Null(game.TrySelectTurret(towerCell));
        Assert.Equal(Material.Air, game.World.GetMaterial(towerCell));
        Assert.Null(climber.NextCell);
        Assert.Equal(0, climber.MoveProgress);
        Assert.InRange(climber.Position.Y, 13.5f, 13.8f);
        Assert.Equal(Vector3.UnitX, attacker.Facing);
        game.Update(.25f); // The accelerated fall is initially smaller than float precision.
        Assert.True(climber.Position.Y < 13.8f);

        var entered = new HashSet<int>();
        for (int step = 0; step < 175; step++)
        {
            game.Update(.02f);
            foreach (var zombie in game.Zombies)
                if (zombie.Cell == towerCell) entered.Add(zombie.Id);
        }
        Assert.Contains(attacker.Id, entered);
        Assert.Contains(climber.Id, entered);
        Assert.True(attacker.Position.X > towerCell.X + 1);
        Assert.True(climber.Position.X > towerCell.X + 1);
        Assert.All(game.Zombies, zombie => Assert.True(zombie.Hp > 0));
    }

    [Fact]
    public void ADeferredTowerDeathCannotRemainInTheCachedOccupancyOrSupportGraph()
    {
        var game = NewBattle();
        var towerCell = new Cell(11, 16, 15);
        Assert.True(game.Deploy(PieceKind.Pawn, towerCell));
        var turret = Assert.Single(game.Turrets);
        turret.Cooldown = 10000;
        Begin(game);
        var zombie = Assert.Single(game.Zombies);
        Place(zombie, new Vector3(10.5f, 16.8f, 15.5f));
        zombie.NextCell = towerCell + new Cell(0, 1, 0);
        // The public HP state can be changed by another damage system without
        // calling the particular obstacle-attack branch that bumps topology.
        turret.Hp = 0;

        game.Update(.01f);

        Assert.Empty(game.Turrets);
        Assert.Null(zombie.NextCell);
        Assert.InRange(zombie.Position.Y, 16.5f, 16.8f);
        game.Update(.25f);
        Assert.True(zombie.Position.Y < 16.8f);
        bool entered = false;
        for (int step = 0; step < 175; step++)
        {
            game.Update(.02f);
            entered |= zombie.Cell == towerCell;
        }
        Assert.True(entered, "A retired tower must not leave an impassable cached cell.");
        Assert.True(zombie.Position.X > towerCell.X + 1);
    }

    [Fact]
    public void DryWalkingFacesThePlannedStepAndThenTheActualHorizontalMotion()
    {
        var game = NewBattle();
        Begin(game);
        var zombie = Assert.Single(game.Zombies);
        Place(zombie, new Cell(10, 16, 15).Center);
        zombie.FaceToward(-Vector3.UnitZ);

        game.Update(.01f);

        Assert.Equal(new Cell(11, 16, 15), zombie.NextCell);
        Assert.Equal(Vector3.UnitX, zombie.Facing);
        var before = zombie.Position;
        game.Update(.1f);
        Assert.True(zombie.Position.X > before.X);
        AssertHorizontalFacing(zombie, zombie.Position - before);
    }

    [Fact]
    public void StationaryCoreAttacksFaceTheObjectiveCell()
    {
        var game = NewBattle();
        Begin(game);
        var zombie = Assert.Single(game.Zombies);
        Place(zombie, new Cell(14, 16, 15).Center);
        zombie.FaceToward(-Vector3.UnitZ);

        game.Update(.01f);

        Assert.Equal(Vector3.UnitX, zombie.Facing);
        Assert.Equal(Balance.CoreHp - zombie.Attack, game.CoreHp);
        Assert.Equal(new Cell(14, 16, 15).Center, zombie.Position);
    }

    [Fact]
    public void PureVerticalFallingAndBuoyantRisingPreserveThePreviousHeading()
    {
        var falling = NewBattle();
        Begin(falling);
        var fallingZombie = Assert.Single(falling.Zombies);
        Place(fallingZombie, new Vector3(10.5f, 17.2f, 15.5f));
        fallingZombie.FaceToward(-Vector3.UnitZ);
        falling.Update(.25f);
        Assert.True(fallingZombie.Position.Y < 17.2f);
        Assert.Equal(-Vector3.UnitZ, fallingZombie.Facing);

        var floating = NewBattle(kind: ZombieKind.Basic);
        FillMercuryWell(floating);
        Begin(floating);
        var floatingZombie = Assert.Single(floating.Zombies);
        Place(floatingZombie, new Cell(8, 13, 8).Center);
        floatingZombie.FaceToward(-Vector3.UnitX);
        floating.Update(.05f);
        Assert.True(floatingZombie.Position.Y > 13.5f);
        Assert.Equal(-Vector3.UnitX, floatingZombie.Facing);
    }

    [Fact]
    public void BuoyantHorizontalTransitionsFaceTheirResolvedMovement()
    {
        var game = NewBattle(kind: ZombieKind.Basic);
        FillMercuryWell(game);
        Begin(game);
        var zombie = Assert.Single(game.Zombies);
        Place(zombie, new Cell(7, 16, 8).Center);
        zombie.FaceToward(-Vector3.UnitZ);
        bool movedHorizontally = false;
        for (int step = 0; step < 20; step++)
        {
            var before = zombie.Position;
            game.Update(.05f);
            var delta = zombie.Position - before;
            if (new Vector2(delta.X, delta.Z).LengthSquared() <= .000001f) continue;
            movedHorizontally = true;
            AssertHorizontalFacing(zombie, delta);
        }
        Assert.True(movedHorizontally);
    }

    private static void AssertHorizontalFacing(Zombie zombie, Vector3 direction)
    {
        var expected = Vector3.Normalize(new Vector3(direction.X, 0, direction.Z));
        Assert.InRange(Vector3.Distance(expected, zombie.Facing), 0, .0001f);
    }

    private static GameSession NewBattle(int count = 1, ZombieKind kind = ZombieKind.Armed)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { SpawnInterval = .01f, Groups = [new() { Kind = kind, Tier = GearTier.Osmium, Count = count }] }] });
        return new GameSession(12345, levels);
    }

    private static void Begin(GameSession game, int count = 1)
    {
        Assert.True(game.StartLevel());
        game.Update(count == 1 ? .001f : .021f);
        Assert.Equal(count, game.Zombies.Count);
    }

    private static void Place(Zombie zombie, Vector3 position)
    {
        zombie.Position = position;
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        zombie.Cooldown = 0;
        zombie.Lane = 0;
    }

    private static void FillMercuryWell(GameSession game)
    {
        Assert.True(game.BuyTool(ToolKind.Shovel));
        for (int y = 13; y <= 15; y++)
        {
            var cell = new Cell(8, y, 8);
            Assert.True(game.Dig(cell));
            Assert.True(game.Liquids.TryAdd(cell, LiquidKind.Mercury, 10));
        }
    }
}
