using ModelExplorer.Indexing;

namespace ModelExplorer.Tests;

public class ModelSearchIndexTests
{
    [Fact]
    public void RanksExactThenPrefixThenNameThenPathMatches()
    {
        var search = new ModelSearchIndex(
        [
            File(1, 1, "gear.stl"),
            File(2, 1, "gearbox.3mf"),
            File(3, 1, "landing-gear.stl"),
            File(4, 1, Path.Combine("gear", "clip.stl")),
            File(5, 1, "unrelated.stl"),
        ]);

        var result = search.Search(new ModelSearchQuery("GEAR"));

        Assert.Equal(
            ["gear.stl", "gearbox.3mf", "landing-gear.stl", "clip.stl"],
            result.Models.Select(model => model.Name));
    }

    [Fact]
    public void SearchTermsUseAndSemanticsAcrossNameAndPath()
    {
        var search = new ModelSearchIndex(
        [
            File(1, 1, "red-wheel.stl"),
            File(2, 1, Path.Combine("red", "wheel.3mf")),
            File(3, 1, "red-bracket.stl"),
            File(4, 1, "wheel.stl"),
        ]);

        var result = search.Search(new ModelSearchQuery("  red   wheel "));

        Assert.Equal(["red-wheel.stl", "wheel.3mf"], result.Models.Select(model => model.Name));
    }

    [Fact]
    public void ExactStemAndExactFileNameReceiveTheSameTopRank()
    {
        var search = new ModelSearchIndex(
        [
            File(1, 1, "benchy-large.stl"),
            File(2, 1, "benchy.stl"),
        ]);

        Assert.Equal("benchy.stl", search.Search(new ModelSearchQuery("benchy")).Models[0].Name);
        Assert.Equal("benchy.stl", search.Search(new ModelSearchQuery("benchy.stl")).Models[0].Name);
    }

    [Fact]
    public void CombinesExtensionSizeAndFolderSubtreeFilters()
    {
        var search = new ModelSearchIndex(
        [
            File(1, 10, Path.Combine("parts", "small.stl"), 500),
            File(2, 10, Path.Combine("parts", "large.stl"), 2_000),
            File(3, 10, Path.Combine("parts", "small.3mf"), 500),
            File(4, 10, Path.Combine("parts-old", "small.stl"), 500),
            File(5, 20, Path.Combine("parts", "small.stl"), 500),
        ]);

        var result = search.Search(new ModelSearchQuery(
            Extension: "stl",
            MaximumSizeExclusive: 1_000,
            RootId: 10,
            FolderRelativePath: "parts"));

        var model = Assert.Single(result.Models);
        Assert.Equal(1, model.Id);
    }

    [Fact]
    public void EmptyQueryReturnsEveryModelInStableNameOrder()
    {
        var search = new ModelSearchIndex(
        [
            File(1, 1, "Zulu.stl"),
            File(2, 1, "alpha.stl"),
            File(3, 1, Path.Combine("nested", "Alpha.stl")),
        ]);

        var first = search.Search(new ModelSearchQuery()).Models;
        var second = search.Search(new ModelSearchQuery()).Models;

        Assert.Equal([2L, 3L, 1L], first.Select(model => model.Id));
        Assert.Equal(first.Select(model => model.Id), second.Select(model => model.Id));
    }

    [Fact]
    public void HonorsCancellationBeforeSearching()
    {
        var search = new ModelSearchIndex([File(1, 1, "part.stl")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            search.Search(new ModelSearchQuery("part"), cancellation.Token));
    }

    [Fact]
    public void SearchesAHundredThousandEntrySnapshotWithoutDatabaseAccess()
    {
        var files = Enumerable.Range(0, 100_000)
            .Select(i => File(i + 1, 1, Path.Combine($"folder-{i % 100}", $"part-{i:D6}.stl")))
            .ToArray();
        files[54_321] = File(54_322, 1, Path.Combine("special", "needle.stl"));
        var search = new ModelSearchIndex(files);

        var result = search.Search(new ModelSearchQuery("needle"));

        Assert.Equal("needle.stl", Assert.Single(result.Models).Name);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(1), $"Search took {result.Elapsed.TotalMilliseconds:N1} ms");
    }

    private static ModelFile File(long id, long rootId, string relativePath, long size = 100) => new()
    {
        Id = id,
        RootId = rootId,
        RootPath = rootId == 10 ? @"C:\models" : rootId == 20 ? @"D:\models" : @"C:\library",
        RelativePath = relativePath,
        Size = size,
        ModifiedTicks = 1,
    };
}
