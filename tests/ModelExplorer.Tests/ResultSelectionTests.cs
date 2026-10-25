using ModelExplorer.Core.Library;
using ModelExplorer.Indexing;

namespace ModelExplorer.Tests;

public class ResultSelectionTests
{
    private static ModelFile[] Files(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => new ModelFile
        {
            Id = i,
            RootId = 1,
            RootPath = @"C:\models",
            RelativePath = $"model{i}.stl",
            Size = 100,
            ModifiedTicks = 0,
        }),
    ];

    private static ResultSelection Over(ModelFile[] files)
    {
        var selection = new ResultSelection();
        selection.Reset(files);
        return selection;
    }

    [Fact]
    public void APlainClickSelectsOnlyThatModel()
    {
        var files = Files(5);
        var selection = Over(files);

        selection.Click(1, control: false, shift: false);
        selection.Click(3, control: false, shift: false);

        Assert.Equal([files[3]], selection.Selected);
        Assert.Equal(3, selection.Focus);
    }

    [Fact]
    public void CtrlClickAddsAndRemovesAndKeepsResultOrder()
    {
        var files = Files(5);
        var selection = Over(files);

        selection.Click(3, control: false, shift: false);
        selection.Click(0, control: true, shift: false);
        selection.Click(4, control: true, shift: false);
        Assert.Equal([files[0], files[3], files[4]], selection.Selected);

        selection.Click(3, control: true, shift: false);
        Assert.Equal([files[0], files[4]], selection.Selected);
    }

    [Fact]
    public void ShiftClickSelectsTheRangeFromTheAnchor()
    {
        var files = Files(8);
        var selection = Over(files);

        selection.Click(2, control: false, shift: false);
        selection.Click(5, control: false, shift: true);
        Assert.Equal(files[2..6], selection.Selected);

        // Ctrl+Shift adds a second range to what is there.
        selection.Click(7, control: true, shift: false);
        selection.Click(6, control: true, shift: true);
        Assert.Equal(files[2..8], selection.Selected);
    }

    [Fact]
    public void ArrowKeysMoveTheSelectionAndShiftExtendsIt()
    {
        var files = Files(6);
        var selection = Over(files);

        selection.Move(1, extend: false);
        Assert.Equal([files[0]], selection.Selected);
        selection.Move(2, extend: false);
        Assert.Equal([files[2]], selection.Selected);
        selection.Move(2, extend: true);
        Assert.Equal(files[2..5], selection.Selected);
        selection.Move(100, extend: false);
        Assert.Equal([files[5]], selection.Selected);
    }

    [Fact]
    public void ANewResultListKeepsTheFilesStillInIt()
    {
        var files = Files(6);
        var selection = Over(files);
        selection.Click(1, control: false, shift: false);
        selection.Click(4, control: true, shift: false);
        var version = selection.Version;

        // A search that still contains model 4, now at a different row.
        selection.Reset([files[4], files[5]]);

        Assert.Equal([files[4]], selection.Selected);
        Assert.Equal(0, selection.Focus);
        Assert.NotEqual(version, selection.Version);
    }

    [Fact]
    public void ARefinedListThatKeepsEverythingChangesNothing()
    {
        var files = Files(4);
        var selection = Over(files);
        selection.Click(2, control: false, shift: false);
        var version = selection.Version;

        selection.Reset(files.Reverse().ToArray());

        Assert.Equal(version, selection.Version);
        Assert.Equal(1, selection.Focus);
    }

    [Fact]
    public void SelectAllAndEnsure()
    {
        var files = Files(4);
        var selection = Over(files);

        selection.SelectAll();
        Assert.Equal(files, selection.Selected);

        // A right-click inside the selection keeps it; outside, it replaces it.
        selection.Ensure(2);
        Assert.Equal(4, selection.Count);
        selection.Clear();
        selection.Click(0, control: false, shift: false);
        selection.Ensure(3);
        Assert.Equal([files[3]], selection.Selected);
    }
}
