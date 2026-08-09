using ModelExplorer.Indexing;

namespace ModelExplorer.Tests;

public sealed class FolderTreeBuilderTests
{
    [Fact]
    public void A_root_with_no_files_is_still_a_node()
    {
        var tree = FolderTreeBuilder.Build([Root(1, @"C:\Models")], []);

        var root = Assert.Single(tree);
        Assert.Equal("Models", root.Name);
        Assert.Equal(1, root.RootId);
        Assert.Equal(string.Empty, root.RelativePath);
        Assert.Equal(@"C:\Models", root.FullPath);
        Assert.Equal(0, root.FileCount);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Files_at_the_top_of_a_root_create_no_child_folders()
    {
        var tree = FolderTreeBuilder.Build(
            [Root(1, @"C:\Models")],
            [File(1, @"C:\Models", "cube.stl"), File(1, @"C:\Models", "plate.3mf")]);

        var root = Assert.Single(tree);
        Assert.Equal(2, root.FileCount);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Nested_folders_become_nested_nodes()
    {
        var tree = FolderTreeBuilder.Build(
            [Root(1, @"C:\Models")],
            [File(1, @"C:\Models", @"parts\deep\pin.stl")]);

        var parts = Assert.Single(Assert.Single(tree).Children);
        Assert.Equal("parts", parts.Name);
        Assert.Equal("parts", parts.RelativePath);
        Assert.Equal(@"C:\Models\parts", parts.FullPath);

        var deep = Assert.Single(parts.Children);
        Assert.Equal("deep", deep.Name);
        Assert.Equal(@"parts\deep", deep.RelativePath);
        Assert.Equal(@"C:\Models\parts\deep", deep.FullPath);
        Assert.Empty(deep.Children);
    }

    /// <summary>
    /// A node's count is its whole subtree, because that is exactly the set of
    /// results selecting it produces.
    /// </summary>
    [Fact]
    public void Counts_roll_up_through_every_ancestor()
    {
        var tree = FolderTreeBuilder.Build(
            [Root(1, @"C:\Models")],
            [
                File(1, @"C:\Models", "loose.stl"),
                File(1, @"C:\Models", @"parts\bracket.stl"),
                File(1, @"C:\Models", @"parts\deep\pin.stl"),
                File(1, @"C:\Models", @"parts\deep\rod.stl"),
            ]);

        var root = Assert.Single(tree);
        Assert.Equal(4, root.FileCount);

        var parts = Assert.Single(root.Children);
        Assert.Equal(3, parts.FileCount);

        var deep = Assert.Single(parts.Children);
        Assert.Equal(2, deep.FileCount);
    }

    [Fact]
    public void Folders_differing_only_in_case_are_one_node()
    {
        var tree = FolderTreeBuilder.Build(
            [Root(1, @"C:\Models")],
            [File(1, @"C:\Models", @"Parts\a.stl"), File(1, @"C:\Models", @"parts\b.stl")]);

        var parts = Assert.Single(Assert.Single(tree).Children);
        Assert.Equal(2, parts.FileCount);
    }

    [Fact]
    public void Forward_slashes_split_the_same_as_backslashes()
    {
        var tree = FolderTreeBuilder.Build(
            [Root(1, @"C:\Models")],
            [File(1, @"C:\Models", "parts/deep/pin.stl")]);

        var deep = Assert.Single(Assert.Single(Assert.Single(tree).Children).Children);
        Assert.Equal("deep", deep.Name);
        Assert.Equal(1, deep.FileCount);
    }

    [Fact]
    public void Roots_are_ordered_by_path_and_children_by_name()
    {
        var tree = FolderTreeBuilder.Build(
            [Root(1, @"C:\Zulu"), Root(2, @"C:\Alpha")],
            [
                File(2, @"C:\Alpha", @"zeta\a.stl"),
                File(2, @"C:\Alpha", @"beta\b.stl"),
                File(2, @"C:\Alpha", @"Gamma\c.stl"),
            ]);

        Assert.Equal(["Alpha", "Zulu"], tree.Select(node => node.Name));
        Assert.Equal(["beta", "Gamma", "zeta"], tree[0].Children.Select(node => node.Name));
    }

    [Fact]
    public void Each_root_keeps_its_own_subtree()
    {
        var tree = FolderTreeBuilder.Build(
            [Root(1, @"C:\Alpha"), Root(2, @"D:\Beta")],
            [
                File(1, @"C:\Alpha", @"shared\a.stl"),
                File(2, @"D:\Beta", @"shared\b.stl"),
                File(2, @"D:\Beta", @"shared\c.stl"),
            ]);

        Assert.Equal(1, tree[0].FileCount);
        Assert.Equal(1, Assert.Single(tree[0].Children).FileCount);
        Assert.Equal(1, tree[0].Children[0].RootId);

        Assert.Equal(2, tree[1].FileCount);
        Assert.Equal(2, Assert.Single(tree[1].Children).FileCount);
        Assert.Equal(2, tree[1].Children[0].RootId);
    }

    /// <summary>
    /// The roots define the library's shape. A file left behind by a removed root
    /// must not conjure a subtree of its own.
    /// </summary>
    [Fact]
    public void A_file_whose_root_is_gone_is_ignored()
    {
        var tree = FolderTreeBuilder.Build(
            [Root(1, @"C:\Models")],
            [File(1, @"C:\Models", "kept.stl"), File(99, @"C:\Removed", @"orphan\gone.stl")]);

        var root = Assert.Single(tree);
        Assert.Equal(1, root.FileCount);
        Assert.Empty(root.Children);
    }

    /// <summary>
    /// The relative path is handed straight to the search index as the subtree
    /// filter, so it has to match how the index stores paths.
    /// </summary>
    [Fact]
    public void Node_paths_match_what_the_search_index_filters_on()
    {
        var root = Root(1, @"C:\Models");
        var files = new[]
        {
            File(1, root.Path, @"parts\bracket.stl"),
            File(1, root.Path, @"parts\deep\pin.stl"),
            File(1, root.Path, @"other\thing.stl"),
        };

        var deep = FolderTreeBuilder.Build([root], files)[0].Children[1].Children[0];
        Assert.Equal(@"parts\deep", deep.RelativePath);

        var index = new ModelSearchIndex(files);
        var matches = index.Search(new ModelSearchQuery(
            RootId: deep.RootId,
            FolderRelativePath: deep.RelativePath));

        Assert.Equal(["pin.stl"], matches.Models.Select(model => model.Name));
    }

    private static LibraryRoot Root(long id, string path) =>
        new(id, path, IsNetwork: false, DateTime.UtcNow, DateTime.UtcNow);

    private static ModelFile File(long rootId, string rootPath, string relativePath) => new()
    {
        Id = relativePath.GetHashCode(),
        RootId = rootId,
        RootPath = rootPath,
        RelativePath = relativePath,
        Size = 1024,
        ModifiedTicks = DateTime.UtcNow.Ticks,
    };
}
