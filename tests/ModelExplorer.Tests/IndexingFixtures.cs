using ModelExplorer.Indexing;

namespace ModelExplorer.Tests;

/// <summary>Builds throwaway folder trees and index databases.</summary>
internal static class IndexingFixtures
{
    public static readonly string[] Extensions = [".stl", ".3mf"];

    /// <summary>
    /// Creates a file under <paramref name="root"/>, making its folders as
    /// needed, and returns the path relative to the root.
    /// </summary>
    public static string CreateFile(string root, string relativePath, int sizeBytes = 16)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[sizeBytes]);
        return relativePath;
    }

    /// <summary>
    /// The tree the scanner tests share: two models at the top, two nested, and
    /// two files that must not be indexed.
    /// </summary>
    public static void CreateSampleTree(string root)
    {
        CreateFile(root, "cube.stl", 100);
        CreateFile(root, "plate.3MF", 200);
        CreateFile(root, Path.Combine("parts", "bracket.stl"), 300);
        CreateFile(root, Path.Combine("parts", "deep", "pin.stl"), 400);
        CreateFile(root, "readme.txt");
        CreateFile(root, Path.Combine("parts", "notes.md"));
    }

    public const int SampleModelCount = 4;
}

/// <summary>A store over a temp database that deletes itself.</summary>
internal sealed class TempIndex : IDisposable
{
    private readonly TempDirectory _directory = new();

    public TempIndex()
    {
        Store = new ModelIndexStore(Path.Combine(_directory.Path, "index.db"));
        Service = new IndexService(Store, IndexingFixtures.Extensions);
    }

    public ModelIndexStore Store { get; }

    public IndexService Service { get; }

    public void Dispose()
    {
        Service.Dispose();
        _directory.Dispose();
    }
}
