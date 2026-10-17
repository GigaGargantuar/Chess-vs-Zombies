using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class BuildHeightTests
{
    [Fact]
    public void ClearedVoidFloorSupportsPiecesBlocksAndBottomObsidianWithoutNeighbors()
    {
        var game = new GameSession();
        ClearColumns(game, 6, 12, 6, 12);
        var turret = new Cell(7, 0, 8);
        var block = new Cell(9, 0, 8);
        var crust = new Cell(11, 0, 8);
        Assert.True(game.HasTowerSupport(turret));
        Assert.True(game.Deploy(PieceKind.Pawn, turret), game.Message);
        Assert.True(game.Inventory.Add(Material.Stone));
        Assert.True(game.BuildBarrier(block, Material.Stone), game.Message);
        Assert.True(game.Inventory.Add(Material.Obsidian));
        Assert.False(game.CanBuildObsidian(crust, CrustFace.West));
        Assert.True(game.BuildObsidian(crust, CrustFace.Bottom), game.Message);
        Assert.True(game.SaveFormation());
        Assert.True(game.Remove(turret));
        Assert.True(game.StartAutoRestoreFormation());
        game.Update(.4f);
        Assert.Equal(turret, Assert.Single(game.Turrets).Cell);
        Assert.Equal(block, Assert.Single(game.World.Barriers).Cell);
        Assert.Equal(crust, Assert.Single(game.World.ObsidianCells).Cell);
        WithSavePath(path =>
        {
            Assert.True(SaveStore.TrySave(game, path, out var error), error);
            Assert.True(SaveStore.TryLoad(path, out var loaded, out error), error);
            Assert.Equal(turret, Assert.Single(loaded!.Turrets).Cell);
            Assert.Equal(block, Assert.Single(loaded.World.Barriers).Cell);
            Assert.Equal(crust, Assert.Single(loaded.World.ObsidianCells).Cell);
        });
    }

    [Fact]
    public void TopLayerPiecesBlocksAndFormationSurviveSavingAndRestoration()
    {
        var game = new GameSession();
        Assert.True(game.Inventory.Add(Material.Stone, 31));
        for (int y = 16; y <= 30; y++)
        {
            Assert.True(game.BuildBarrier(new Cell(8, y, 8), Material.Stone), game.Message);
            Assert.True(game.BuildBarrier(new Cell(9, y, 8), Material.Stone), game.Message);
        }
        var top = new Cell(8, 31, 8);
        Assert.True(game.Deploy(PieceKind.Knight, top), game.Message);
        Assert.True(game.BuildBarrier(new Cell(9, 31, 8), Material.Stone), game.Message);
        Assert.True(game.SaveFormation());
        WithSavePath(path =>
        {
            Assert.True(SaveStore.TrySave(game, path, out var error), error);
            Assert.True(SaveStore.TryLoad(path, out var loaded, out error), error);
            Assert.Equal(top, Assert.Single(loaded!.Turrets).Cell);
            Assert.Equal(Material.Barrier, loaded.World.GetMaterial(new Cell(9, 31, 8)));
            Assert.Equal(top, Assert.Single(loaded.SavedFormation!.Towers).Cell);
            Assert.True(loaded.Remove(top));
            Assert.True(loaded.StartAutoRestoreFormation());
            loaded.Update(.4f);
            Assert.Equal(top, Assert.Single(loaded.Turrets).Cell);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void BothVerticalEdgesAreBuildableButHorizontalEdgesStayReserved(int y)
    {
        Assert.True(WorldGrid.IsTurretZone(new Cell(1, y, 30)));
        Assert.True(WorldGrid.IsFortificationZone(new Cell(30, y, 1)));
        foreach (var cell in new[] { new Cell(0, y, 8), new Cell(31, y, 8), new Cell(8, y, 0), new Cell(8, y, 31) })
        {
            Assert.False(WorldGrid.IsTurretZone(cell));
            Assert.False(WorldGrid.IsFortificationZone(cell));
        }
    }

    [Fact]
    public void CrosshairCanPlaceOnExcavatedVoidWithoutCreatingOcclusionGeometry()
    {
        var game = new GameSession();
        ClearColumns(game, 8, 8, 8, 8);
        var origin = new Vector3(8.5f, 18, 8.5f);
        Assert.Null(WorldPicker.Pick(game, origin, -Vector3.UnitY));
        Assert.Null(WorldPicker.Pick(game, origin, -Vector3.UnitY, 17.9f, includeVoidFloor: true));
        var aim = WorldPicker.Pick(game, origin, -Vector3.UnitY, 18, includeVoidFloor: true)!.Value;
        Assert.Equal(new Cell(8, 0, 8), aim.Placement);
        Assert.Equal(aim.Placement, aim.Highlight);
        Assert.True(game.Deploy(PieceKind.Pawn, aim.Placement!.Value), game.Message);
        Assert.Equal(new Cell(8, 0, 8), WorldPicker.Pick(game, origin, -Vector3.UnitY)!.Value.Hit);
        Assert.Null(WorldPicker.Pick(game, new Vector3(8, 20, 8), Vector3.UnitY, includeVoidFloor: true));
        Assert.Null(WorldPicker.Pick(game, new Vector3(-2, 2, 8), -Vector3.UnitY, includeVoidFloor: true));
    }

    private static void ClearColumns(GameSession game, int minX, int maxX, int minZ, int maxZ)
    {
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int x = minX; x <= maxX; x++)
            for (int z = minZ; z <= maxZ; z++)
                for (int y = 0; y < 16; y++) Assert.True((bool)dig.Invoke(game.World, [new Cell(x, y, z)])!);
    }

    private static void WithSavePath(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cvz-build-height-{Guid.NewGuid():N}.json");
        try { action(path); }
        finally { File.Delete(path); }
    }
}
