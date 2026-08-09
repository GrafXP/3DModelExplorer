using ModelExplorer.Indexing;

namespace ModelExplorer.Tests;

public class FileScannerTests
{
    [Fact]
    public void FindsModelsRecursivelyAndIgnoresEverythingElse()
    {
        using var dir = new TempDirectory();
        IndexingFixtures.CreateSampleTree(dir.Path);

        var found = FileScanner.Enumerate(dir.Path, IndexingFixtures.Extensions).ToList();

        Assert.Equal(IndexingFixtures.SampleModelCount, found.Count);
        Assert.Contains(found, f => f.RelativePath == "cube.stl");
        Assert.Contains(found, f => f.RelativePath == "plate.3MF");
        Assert.Contains(found, f => f.RelativePath == Path.Combine("parts", "bracket.stl"));
        Assert.Contains(found, f => f.RelativePath == Path.Combine("parts", "deep", "pin.stl"));
        Assert.DoesNotContain(found, f => f.RelativePath.EndsWith(".txt", StringComparison.Ordinal));
    }

    /// <summary>
    /// The relative path is built from span arithmetic against the root, which is
    /// exactly the kind of thing that silently shears off a leading character.
    /// </summary>
    [Fact]
    public void RelativePathsAreRootedCorrectlyWhateverTheRootLooksLike()
    {
        using var dir = new TempDirectory();
        IndexingFixtures.CreateFile(dir.Path, Path.Combine("a", "b", "part.stl"));

        var expected = Path.Combine("a", "b", "part.stl");

        foreach (var root in (string[])[dir.Path, dir.Path + Path.DirectorySeparatorChar])
        {
            var found = FileScanner.Enumerate(root, IndexingFixtures.Extensions).Single();
            Assert.Equal(expected, found.RelativePath);
        }
    }

    [Fact]
    public void ExtensionMatchingIgnoresCase()
    {
        using var dir = new TempDirectory();
        IndexingFixtures.CreateFile(dir.Path, "UPPER.STL");
        IndexingFixtures.CreateFile(dir.Path, "mixed.3Mf");

        Assert.Equal(2, FileScanner.Enumerate(dir.Path, IndexingFixtures.Extensions).Count());
    }

    /// <summary>A dotfile named for the extension is not a model.</summary>
    [Fact]
    public void SkipsFilesThatAreNothingButAnExtension()
    {
        using var dir = new TempDirectory();
        IndexingFixtures.CreateFile(dir.Path, ".stl");

        Assert.Empty(FileScanner.Enumerate(dir.Path, IndexingFixtures.Extensions));
    }

    /// <summary>
    /// Size and last-write time come off the directory entry the OS already
    /// returned rather than from a second stat, so they are worth checking.
    /// </summary>
    [Fact]
    public void ReadsSizeAndModifiedTimeFromTheWalk()
    {
        using var dir = new TempDirectory();
        IndexingFixtures.CreateFile(dir.Path, "sized.stl", 1234);

        var before = DateTime.UtcNow.AddMinutes(-1);
        var found = FileScanner.Enumerate(dir.Path, IndexingFixtures.Extensions).Single();

        Assert.Equal(1234, found.Size);

        var modified = new DateTime(found.ModifiedTicks, DateTimeKind.Utc);
        Assert.InRange(modified, before, DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void AnEmptyTreeYieldsNothing()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "empty", "deeper"));

        Assert.Empty(FileScanner.Enumerate(dir.Path, IndexingFixtures.Extensions));
    }
}
