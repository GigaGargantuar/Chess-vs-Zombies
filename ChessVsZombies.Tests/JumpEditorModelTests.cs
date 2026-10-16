using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class JumpEditorModelTests
{
    [Fact]
    public void PlanePickingUsesTheActiveHeightAndRejectsOriginAndOutOfRangeCells()
    {
        var editor = new JumpEditorModel();
        editor.SetLayer(1, 5);
        Assert.Equal(new Cell(2, 1, -1), editor.PickPlacement(new Vector3(2, 6, -1), -Vector3.UnitY, 5));
        Assert.Null(editor.PickPlacement(new Vector3(0, 6, 0), -Vector3.UnitY, 5));
        Assert.Null(editor.PickPlacement(new Vector3(6, 6, 0), -Vector3.UnitY, 5));
        Assert.Null(editor.PickPlacement(new Vector3(2, 6, 0), Vector3.UnitY, 5));
    }

    [Fact]
    public void PickingSelectsTheNearestVisibleDestinationAndSectionHidesOtherHeights()
    {
        var editor = new JumpEditorModel();
        Cell[] pattern = [new(2, 0, 0), new(2, 1, 0)];
        Assert.Equal(new Cell(2, 1, 0), editor.PickExisting(pattern, new Vector3(2, 5, 0), -Vector3.UnitY));
        editor.ToggleCrossSection();
        Assert.Equal(new Cell(2, 0, 0), editor.PickExisting(pattern, new Vector3(2, 5, 0), -Vector3.UnitY));
        editor.SetLayer(-1, 3);
        Assert.Null(editor.PickExisting(pattern, new Vector3(2, 5, 0), -Vector3.UnitY));
    }

    [Fact]
    public void RepeatedAddRejectsDuplicatesNearOffsetsAndTheWrongLayer()
    {
        var editor = new JumpEditorModel();
        var pattern = new List<Cell>();
        Assert.True(editor.TryAdd(pattern, new Cell(2, 0, 1), 3));
        Assert.False(editor.TryAdd(pattern, new Cell(2, 0, 1), 3));
        Assert.False(editor.TryAdd(pattern, new Cell(0, 0, 0), 3));
        Assert.False(editor.TryAdd(pattern, new Cell(1, 0, 1), 3));
        Assert.False(editor.TryAdd(pattern, new Cell(4, 0, 0), 3));
        Assert.False(editor.TryAdd(pattern, new Cell(int.MinValue, 0, 0), 3));
        Assert.False(editor.TryAdd(pattern, new Cell(2, 1, 0), 3));
        Assert.Single(pattern);
    }

    [Fact]
    public void ZoomRotationAndLayerStayWithinEditorLimits()
    {
        var editor = new JumpEditorModel();
        editor.ZoomBy(200);
        Assert.Equal(JumpEditorModel.MinimumZoom, editor.Zoom);
        editor.ZoomBy(-200);
        Assert.Equal(JumpEditorModel.MaximumZoom, editor.Zoom);
        editor.Rotate(1000, 1000);
        Assert.InRange(editor.Pitch, -.95f, .95f);
        editor.SetLayer(99, 4);
        Assert.Equal(4, editor.Layer);
        editor.SetLayer(-99, 4);
        Assert.Equal(-4, editor.Layer);
    }
}
