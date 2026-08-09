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

    [Theory]
    [InlineData(ModelSortField.Size, false, new[] { 3L, 1L, 2L })]
    [InlineData(ModelSortField.Size, true, new[] { 2L, 1L, 3L })]
    [InlineData(ModelSortField.DateModified, false, new[] { 2L, 3L, 1L })]
    [InlineData(ModelSortField.DateModified, true, new[] { 1L, 3L, 2L })]
    [InlineData(ModelSortField.Name, false, new[] { 1L, 2L, 3L })]
    [InlineData(ModelSortField.Name, true, new[] { 3L, 2L, 1L })]
    public void OrdersEveryModelByTheRequestedField(
        ModelSortField field,
        bool descending,
        long[] expected)
    {
        var search = new ModelSearchIndex(
        [
            File(1, 1, "alpha.stl", size: 200, modifiedTicks: 300),
            File(2, 1, "bravo.stl", size: 300, modifiedTicks: 100),
            File(3, 1, "charlie.stl", size: 100, modifiedTicks: 200),
        ]);

        var result = search.Search(new ModelSearchQuery(Sort: field, Descending: descending));

        Assert.Equal(expected, result.Models.Select(model => model.Id));
    }

    [Fact]
    public void SortingAppliesToFilteredResultsRatherThanTheWholeIndex()
    {
        var search = new ModelSearchIndex(
        [
            File(1, 1, "gear-small.stl", size: 100),
            File(2, 1, "unrelated.stl", size: 900),
            File(3, 1, "gear-large.stl", size: 500),
            File(4, 1, "gear-medium.stl", size: 300),
        ]);

        var result = search.Search(new ModelSearchQuery(
            "gear",
            Sort: ModelSortField.Size,
            Descending: true));

        Assert.Equal([3L, 4L, 1L], result.Models.Select(model => model.Id));
    }

    [Fact]
    public void GroupsByFormatAndByFolderWithinTheirRoot()
    {
        var search = new ModelSearchIndex(
        [
            File(1, 10, Path.Combine("b-parts", "cog.stl")),
            File(2, 10, Path.Combine("a-parts", "hinge.3mf")),
            File(3, 20, Path.Combine("a-parts", "clip.stl")),
            File(4, 10, Path.Combine("a-parts", "bolt.stl")),
        ]);

        Assert.Equal(
            [2L, 4L, 3L, 1L],
            search.Search(new ModelSearchQuery(Sort: ModelSortField.Format)).Models.Select(m => m.Id));

        // C:\models before D:\models, then a-parts before b-parts inside the first.
        Assert.Equal(
            [4L, 2L, 1L, 3L],
            search.Search(new ModelSearchQuery(Sort: ModelSortField.Folder)).Models.Select(m => m.Id));
    }

    [Fact]
    public void BreaksTiesByNameSoAnOrderIsNeverArbitrary()
    {
        var search = new ModelSearchIndex(
        [
            File(1, 1, "zulu.stl", size: 100),
            File(2, 1, "alpha.stl", size: 100),
            File(3, 1, "mike.stl", size: 100),
        ]);

        var result = search.Search(new ModelSearchQuery(Sort: ModelSortField.Size));

        Assert.Equal([2L, 3L, 1L], result.Models.Select(model => model.Id));
    }

    [Fact]
    public void ReusesOneOrderingAcrossRepeatedQueries()
    {
        var files = Enumerable.Range(0, 100_000)
            .Select(i => File(i + 1, 1, $"part-{i:D6}.stl", size: (i * 7919) % 100_000))
            .ToArray();
        var search = new ModelSearchIndex(files);
        var sort = new ModelSearchQuery(Sort: ModelSortField.Size, Descending: true);

        // The first query builds the permutation; every later one only walks it,
        // which is what keeps re-sorting off the keystroke path.
        var first = search.Search(sort);
        var second = search.Search(sort);

        Assert.Equal(first.Models.Select(model => model.Id), second.Models.Select(model => model.Id));
        Assert.True(
            second.Elapsed < TimeSpan.FromMilliseconds(200),
            $"Repeat sorted search took {second.Elapsed.TotalMilliseconds:N1} ms");
        Assert.True(second.Models[0].Size >= second.Models[^1].Size);
    }

    private static ModelFile File(
        long id,
        long rootId,
        string relativePath,
        long size = 100,
        long modifiedTicks = 1) => new()
    {
        Id = id,
        RootId = rootId,
        RootPath = rootId == 10 ? @"C:\models" : rootId == 20 ? @"D:\models" : @"C:\library",
        RelativePath = relativePath,
        Size = size,
        ModifiedTicks = modifiedTicks,
    };
}
