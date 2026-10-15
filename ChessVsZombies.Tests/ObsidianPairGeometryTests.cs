using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ObsidianPairGeometryTests
{
    private static readonly Cell CrustCell = new(8, 16, 8);

    [Theory]
    [InlineData(8.1f, CrustFace.West)]
    [InlineData(8.9f, CrustFace.East)]
    public void CrosshairFindsTheActualPartOnEitherSideOfTheOpenChannel(float x, CrustFace face)
    {
        var game = Pair();

        var aim = WorldPicker.Pick(game, new Vector3(x, 16.5f, 7.5f), Vector3.UnitZ, 2, false);

        Assert.True(aim.HasValue);
        Assert.Equal(CrustCell, aim.Value.Hit);
        Assert.Equal(face, aim.Value.ObsidianFace);
        Assert.False(aim.Value.IsLiquid);
        Assert.Null(WorldPicker.Pick(game, new Vector3(8.5f, 16.5f, 7.5f), Vector3.UnitZ, 2, false));
    }

    [Theory]
    [InlineData(8.1f, CrustFace.West)]
    [InlineData(8.9f, CrustFace.East)]
    public void PelletsDamageOnlyThePartTheyActuallyIntersect(float x, CrustFace face)
    {
        var game = Pair();
        game.Projectiles.Add(Shot(new Vector3(x, 16.5f, 7.5f), 3));

        game.Update(.1f);

        Assert.Empty(game.Projectiles);
        var crust = Assert.IsType<ObsidianCell>(game.World.GetObsidian(CrustCell));
        Assert.Equal(17, crust.Hp);
        Assert.Equal(7, crust.Parts.Single(part => part.Face == face).Hp);
        Assert.Equal(10, crust.Parts.Single(part => part.Face != face).Hp);
    }

    [Fact]
    public void PelletsPassThroughTheGapBetweenOppositeCrusts()
    {
        var game = Pair();
        game.Projectiles.Add(Shot(new Vector3(8.5f, 16.5f, 7.5f), 3));

        game.Update(.1f);

        Assert.Single(game.Projectiles);
        Assert.Equal(20, game.World.GetObsidian(CrustCell)!.Hp);
    }

    [Fact]
    public void DestroyingTheSecondaryCrustDoesNotDamageOrDeleteItsNeighbor()
    {
        var game = Pair();
        game.Projectiles.Add(Shot(new Vector3(8.9f, 16.5f, 7.5f), 10));

        game.Update(.1f);

        var remaining = Assert.IsType<ObsidianCell>(game.World.GetObsidian(CrustCell));
        Assert.Equal(CrustFace.West, remaining.Face);
        Assert.Equal(2, remaining.Units);
        Assert.Equal(10, remaining.Hp);
        Assert.Single(remaining.Parts);
        Assert.Equal(0, game.Inventory.ObsidianLayers);
        Assert.Empty(game.Projectiles);
    }

    [Fact]
    public void ZombieBodyAndFootingUseBothSlabsWithoutFillingTheGap()
    {
        var game = Pair();
        var clear = Method<Func<Vector3, Zombie?, bool>>(game, "IsBodyClear");
        var floor = Method<FindFloor>(game, "TrySolidFloor");

        Assert.True(clear(CrustCell.Center, null));
        Assert.False(clear(new Vector3(8.75f, 16.5f, 8.5f), null));
        Assert.True(floor(new Vector3(8.9f, 17.5f, 8.5f), out var eastHeight, null));
        Assert.Equal(17.5f, eastHeight);
        Assert.True(floor(new Vector3(8.5f, 17.5f, 8.5f), out var gapHeight, null));
        Assert.Equal(16.5f, gapHeight);
    }

    [Fact]
    public void JumpSweepDetectsTheOppositeSlabInsteadOfPassingThroughIt()
    {
        var game = Pair(primaryUnits: 1, oppositeUnits: 4);
        var clear = Method<Func<Cell, Cell, float, float, float, Zombie?, bool>>(game, "IsJumpArcClear");

        Assert.False(clear(new Cell(8, 16, 7), new Cell(8, 16, 9), .1f, 0, 1, null));
    }

    [Fact]
    public void ASlabFallTrackPreservesTheStationaryOppositeSlabAtItsDestination()
    {
        var game = Pair();
        var source = CrustCell + new Cell(0, 3, 0);
        var moving = game.World.GetObsidian(CrustCell)!.Parts.Single(part => part.Face == CrustFace.East);
        Method<Action<Cell, Cell, ObsidianCell>>(game.Falls, "RecordObsidianDrop")(source, CrustCell, moving);

        game.Update(.5f);

        var fall = Assert.Single(game.Falls.Blocks);
        Assert.Equal(CrustFace.East, fall.Crust!.Face);
        Assert.Equal(2, fall.Crust.Units);
        Assert.Equal(19.46875f, fall.Position.Y, 4);
        Assert.Equal(2, game.World.GetObsidian(CrustCell)!.Parts.Count());
        Assert.DoesNotContain(game.Falls.Blocks, block => block.Crust?.Face == CrustFace.West);
    }

    [Fact]
    public void MovingAnEntirePairedBlockAnimatesBothPreservedFaces()
    {
        var game = Pair();
        var source = CrustCell + new Cell(0, 3, 0);
        Method<Action<Cell, Cell, WorldGrid>>(game.Falls, "RecordBlockDrop")(source, CrustCell, game.World);

        game.Update(.5f);

        Assert.Equal(2, game.Falls.Blocks.Count);
        Assert.Equal(new[] { CrustFace.West, CrustFace.East }, game.Falls.Blocks.Select(block => block.Crust!.Face).Order());
        Assert.All(game.Falls.Blocks, block => Assert.Equal(19.46875f, block.Position.Y, 4));
    }

    private static GameSession Pair(int primaryUnits = 2, int oppositeUnits = 2)
    {
        var game = new GameSession();
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(CrustCell, primaryUnits + oppositeUnits, CrustFace.West,
                (primaryUnits + oppositeUnits) * LiquidSystem.ObsidianHpPerUnit,
                OppositeUnits: oppositeUnits, OppositeHp: oppositeUnits * LiquidSystem.ObsidianHpPerUnit)]
        });
        return game;
    }

    private static Projectile Shot(Vector3 from, float damage) => new()
    {
        Kind = ShotKind.Pellet, Origin = from, Target = from + Vector3.UnitZ * 2,
        Duration = .1f, Damage = damage, TargetId = -1
    };

    private delegate bool FindFloor(Vector3 position, out float height, Zombie? zombie);
    private static T Method<T>(object target, string name) where T : Delegate =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<T>(target);
}
