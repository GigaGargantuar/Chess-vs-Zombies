using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class WorldGridTests
{
    [Fact]
    public void BuildableAreaUsesAllLayersWhileSpawnsUseOnlyTheOutermostHorizontalRing()
    {
        var cells = AllCells().ToArray();
        Assert.Equal(32 * 32 * 32, cells.Length);
        Assert.Equal(8, cells.Count(WorldGrid.IsCore));
        Assert.Equal(512, cells.Count(WorldGrid.IsBuildZone));
        Assert.Equal(30 * 32 * 30 - 512, cells.Count(WorldGrid.IsTurretZone));
        Assert.Equal(30 * 32 * 30 - 8, cells.Count(WorldGrid.IsFortificationZone));
        Assert.Equal((32 * 32 - 30 * 30) * 32, cells.Count(WorldGrid.IsSpawnZone));
        Assert.DoesNotContain(cells, cell => WorldGrid.IsTurretZone(cell) && WorldGrid.IsSpawnZone(cell));
        Assert.All(cells, cell => Assert.True(WorldGrid.IsTurretZone(cell) || WorldGrid.IsSpawnZone(cell) || WorldGrid.IsBuildZone(cell)));
        foreach (int y in new[] { 0, 31 })
        {
            Assert.Equal(32 * 32 - 30 * 30, cells.Count(cell => cell.Y == y && WorldGrid.IsSpawnZone(cell)));
            Assert.False(WorldGrid.IsSpawnZone(new Cell(8, y, 8)));
            Assert.True(WorldGrid.IsSpawnZone(new Cell(0, y, 8)));
            Assert.True(WorldGrid.IsSpawnZone(new Cell(8, y, 31)));
        }
        Assert.All(cells.Where(WorldGrid.IsCore), cell =>
        {
            Assert.InRange(cell.X, 15, 16);
            Assert.InRange(cell.Y, 15, 16);
            Assert.InRange(cell.Z, 15, 16);
        });
    }

    [Theory]
    [InlineData(-1, 16, 16)]
    [InlineData(32, 16, 16)]
    [InlineData(16, -1, 16)]
    [InlineData(16, 32, 16)]
    [InlineData(16, 16, -1)]
    [InlineData(16, 16, 32)]
    public void NoPlayableZoneExtendsOutsideTheGrid(int x, int y, int z)
    {
        var cell = new Cell(x, y, z);
        Assert.False(WorldGrid.IsInside(cell));
        Assert.False(WorldGrid.IsCore(cell));
        Assert.False(WorldGrid.IsBuildZone(cell));
        Assert.False(WorldGrid.IsTurretZone(cell));
        Assert.False(WorldGrid.IsSpawnZone(cell));
    }

    [Fact]
    public void InitialTerrainHasFourDirtLayersOverStoneAndPreservesTheWholeCore()
    {
        var world = new WorldGrid();
        var materials = AllCells().Select(world.GetMaterial).ToArray();
        Assert.Equal(12288, materials.Count(material => material == Material.Stone));
        Assert.Equal(4092, materials.Count(material => material == Material.Dirt));
        Assert.Equal(8, materials.Count(material => material == Material.Core));
        Assert.Equal(16380, materials.Count(material => material == Material.Air));

        Assert.All(AllCells(), cell =>
        {
            var expected = WorldGrid.IsCore(cell) ? Material.Core
                : cell.Y < 12 ? Material.Stone
                : cell.Y < 16 ? Material.Dirt : Material.Air;
            Assert.Equal(expected, world.GetMaterial(cell));
        });
    }

    private static IEnumerable<Cell> AllCells()
    {
        for (int x = 0; x < 32; x++)
            for (int y = 0; y < 32; y++)
                for (int z = 0; z < 32; z++)
                    yield return new Cell(x, y, z);
    }
}
