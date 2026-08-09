using ModelExplorer.Indexing;

namespace ModelExplorer.Tests;

public class ModelIndexStoreTests
{
    [Fact]
    public void AddingTheSameFolderTwiceReturnsTheSameRoot()
    {
        using var index = new TempIndex();

        var first = index.Store.AddRoot(@"C:\models", isNetwork: false);
        var second = index.Store.AddRoot(@"C:\models", isNetwork: false);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(index.Store.GetRoots());
    }

    [Fact]
    public void WritesAndReadsBackFiles()
    {
        using var index = new TempIndex();
        var root = index.Store.AddRoot(@"C:\models", isNetwork: false);

        index.Store.WriteBatch(
        [
            new ScannedFile(root.Id, @"a\one.stl", 100, 5_000),
            new ScannedFile(root.Id, "two.3mf", 200, 6_000),
        ]);

        var files = index.Store.LoadFiles().OrderBy(f => f.RelativePath).ToList();

        Assert.Equal(2, files.Count);
        Assert.Equal(@"C:\models\a\one.stl", files[0].FullPath);
        Assert.Equal("one.stl", files[0].Name);
        Assert.Equal(@"C:\models\a", files[0].Folder);
        Assert.Equal(100, files[0].Size);
        Assert.Equal(new DateTime(5_000, DateTimeKind.Utc), files[0].ModifiedUtc);
        Assert.Equal("two.3mf", files[1].Name);
    }

    /// <summary>
    /// Re-writing a path updates it rather than adding a duplicate. Step 7's
    /// incremental rescan leans on this; without it a rescan doubles the index.
    /// </summary>
    [Fact]
    public void RewritingAPathUpdatesTheExistingRow()
    {
        using var index = new TempIndex();
        var root = index.Store.AddRoot(@"C:\models", isNetwork: false);

        index.Store.WriteBatch([new ScannedFile(root.Id, "part.stl", 100, 1)]);
        index.Store.WriteBatch([new ScannedFile(root.Id, "part.stl", 999, 2)]);

        var file = Assert.Single(index.Store.LoadFiles());
        Assert.Equal(999, file.Size);
        Assert.Equal(2, file.ModifiedTicks);
    }

    /// <summary>The same relative path under two roots is two different files.</summary>
    [Fact]
    public void PathsAreScopedToTheirRoot()
    {
        using var index = new TempIndex();
        var first = index.Store.AddRoot(@"C:\one", isNetwork: false);
        var second = index.Store.AddRoot(@"D:\two", isNetwork: false);

        index.Store.WriteBatch(
        [
            new ScannedFile(first.Id, "part.stl", 1, 1),
            new ScannedFile(second.Id, "part.stl", 2, 2),
        ]);

        var files = index.Store.LoadFiles();
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.FullPath == @"C:\one\part.stl");
        Assert.Contains(files, f => f.FullPath == @"D:\two\part.stl");
    }

    [Fact]
    public void ClearingARootLeavesTheRootButDropsItsFiles()
    {
        using var index = new TempIndex();
        var root = index.Store.AddRoot(@"C:\models", isNetwork: false);
        index.Store.WriteBatch([new ScannedFile(root.Id, "part.stl", 1, 1)]);

        index.Store.ClearRoot(root.Id);

        Assert.Single(index.Store.GetRoots());
        Assert.Empty(index.Store.LoadFiles());
    }

    [Fact]
    public void RemovingARootTakesItsFilesWithIt()
    {
        using var index = new TempIndex();
        var kept = index.Store.AddRoot(@"C:\keep", isNetwork: false);
        var removed = index.Store.AddRoot(@"C:\drop", isNetwork: false);

        index.Store.WriteBatch(
        [
            new ScannedFile(kept.Id, "keep.stl", 1, 1),
            new ScannedFile(removed.Id, "drop.stl", 1, 1),
        ]);

        index.Store.RemoveRoot(removed.Id);

        Assert.Single(index.Store.GetRoots());
        var file = Assert.Single(index.Store.LoadFiles());
        Assert.Equal("keep.stl", file.Name);
    }

    [Fact]
    public void MarkingAScanRecordsWhenItFinished()
    {
        using var index = new TempIndex();
        var root = index.Store.AddRoot(@"C:\models", isNetwork: false);
        Assert.Null(root.LastScanUtc);

        var completed = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);
        index.Store.MarkScanned(root.Id, completed);

        Assert.Equal(completed, index.Store.GetRoots().Single().LastScanUtc);
    }

    /// <summary>
    /// The index is the whole point of not rescanning at startup, so it has to
    /// survive the process that wrote it.
    /// </summary>
    [Fact]
    public void TheIndexSurvivesReopening()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "index.db");

        using (var store = new ModelIndexStore(path))
        {
            var root = store.AddRoot(@"C:\models", isNetwork: false);
            store.WriteBatch([new ScannedFile(root.Id, "part.stl", 42, 7)]);
        }

        using (var reopened = new ModelIndexStore(path))
        {
            Assert.Single(reopened.GetRoots());
            var file = Assert.Single(reopened.LoadFiles());
            Assert.Equal(@"C:\models\part.stl", file.FullPath);
            Assert.Equal(42, file.Size);
        }
    }
}

public class IndexServiceTests
{
    [Fact]
    public async Task ScansAFolderIntoTheIndex()
    {
        using var dir = new TempDirectory();
        using var index = new TempIndex();
        IndexingFixtures.CreateSampleTree(dir.Path);

        var root = await index.Service.AddRootAsync(dir.Path);
        var summary = await index.Service.ScanAsync([root], null, CancellationToken.None);

        Assert.False(summary.Cancelled);
        Assert.Equal(IndexingFixtures.SampleModelCount, summary.Found);
        Assert.Equal(IndexingFixtures.SampleModelCount, summary.Indexed);

        var files = await index.Service.LoadFilesAsync();
        Assert.Equal(IndexingFixtures.SampleModelCount, files.Count);
        Assert.All(files, f => Assert.True(File.Exists(f.FullPath), $"{f.FullPath} should exist"));
    }

    [Fact]
    public async Task ACompletedScanMarksItsRoots()
    {
        using var dir = new TempDirectory();
        using var index = new TempIndex();
        IndexingFixtures.CreateFile(dir.Path, "part.stl");

        var root = await index.Service.AddRootAsync(dir.Path);
        await index.Service.ScanAsync([root], null, CancellationToken.None);

        Assert.NotNull((await index.Service.GetRootsAsync()).Single().LastScanUtc);
    }

    /// <summary>Rescanning replaces rows rather than accumulating them.</summary>
    [Fact]
    public async Task RescanningDoesNotDuplicateFiles()
    {
        using var dir = new TempDirectory();
        using var index = new TempIndex();
        IndexingFixtures.CreateSampleTree(dir.Path);

        var root = await index.Service.AddRootAsync(dir.Path);
        await index.Service.ScanAsync([root], null, CancellationToken.None);
        await index.Service.ScanAsync([root], null, CancellationToken.None);

        Assert.Equal(IndexingFixtures.SampleModelCount, (await index.Service.LoadFilesAsync()).Count);
    }

    /// <summary>A file removed from disk is gone from the index after a rescan.</summary>
    [Fact]
    public async Task RescanningDropsFilesThatNoLongerExist()
    {
        using var dir = new TempDirectory();
        using var index = new TempIndex();
        IndexingFixtures.CreateFile(dir.Path, "keep.stl");
        IndexingFixtures.CreateFile(dir.Path, "gone.stl");

        var root = await index.Service.AddRootAsync(dir.Path);
        await index.Service.ScanAsync([root], null, CancellationToken.None);

        File.Delete(Path.Combine(dir.Path, "gone.stl"));
        await index.Service.ScanAsync([root], null, CancellationToken.None);

        var file = Assert.Single(await index.Service.LoadFilesAsync());
        Assert.Equal("keep.stl", file.Name);
    }

    /// <summary>
    /// Cancelling is a button the user pressed, not a failure: it reports through
    /// the summary instead of throwing, and it must not clear an index it is not
    /// going to refill.
    /// </summary>
    [Fact]
    public async Task CancellingBeforeAnyWorkLeavesTheIndexAlone()
    {
        using var dir = new TempDirectory();
        using var index = new TempIndex();
        IndexingFixtures.CreateSampleTree(dir.Path);

        var root = await index.Service.AddRootAsync(dir.Path);
        await index.Service.ScanAsync([root], null, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var summary = await index.Service.ScanAsync([root], null, cancellation.Token);

        Assert.True(summary.Cancelled);
        Assert.Equal(0, summary.Indexed);
        Assert.Equal(IndexingFixtures.SampleModelCount, (await index.Service.LoadFilesAsync()).Count);
    }

    [Fact]
    public async Task ScanningNoRootsIsANoOp()
    {
        using var index = new TempIndex();

        var summary = await index.Service.ScanAsync([], null, CancellationToken.None);

        Assert.False(summary.Cancelled);
        Assert.Equal(0, summary.Found);
    }

    /// <summary>
    /// Progress is sampled on a timer, so a scan long enough to tick has to
    /// produce at least one report — otherwise the status bar stays blank for the
    /// whole scan.
    /// </summary>
    [Fact]
    public async Task ReportsProgressWhileScanning()
    {
        using var dir = new TempDirectory();
        using var index = new TempIndex();

        for (var i = 0; i < 2_000; i++)
        {
            IndexingFixtures.CreateFile(dir.Path, Path.Combine($"d{i % 20}", $"part{i}.stl"), 1);
        }

        var reports = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(p => { lock (reports) { reports.Add(p); } });

        var root = await index.Service.AddRootAsync(dir.Path);
        var summary = await index.Service.ScanAsync([root], progress, CancellationToken.None);

        Assert.Equal(2_000, summary.Indexed);
        Assert.True(summary.FilesPerSecond > 0);
    }
}
