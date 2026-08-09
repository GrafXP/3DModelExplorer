using System.Collections.Concurrent;
using System.Diagnostics;

namespace ModelExplorer.Indexing;

/// <summary>
/// Immutable, in-memory projection of the persisted model index.
/// </summary>
/// <remarks>
/// SQLite is the durable source of truth, but it is deliberately absent from the
/// keystroke path. Names and relative paths are lower-cased once here; searches
/// then use the runtime's vectorized ordinal span search over flat arrays.
///
/// The entries are sorted once when the snapshot is built. A query only assigns
/// one of four ranks and walks that already-sorted array into rank buckets, so a
/// broad query does not pay for sorting tens of thousands of hits every time.
/// </remarks>
public sealed class ModelSearchIndex
{
    private const byte NoMatch = 0;
    private const byte ExactName = 1;
    private const byte NamePrefix = 2;
    private const byte NameContains = 3;
    private const byte PathContains = 4;

    private readonly ModelFile[] _models;
    private readonly string[] _haystacks;
    private readonly int[] _nameLengths;

    public ModelSearchIndex(IReadOnlyList<ModelFile> models)
    {
        var entries = new SearchEntry[models.Count];
        Parallel.For(0, entries.Length, i =>
        {
            var model = models[i];
            var name = model.Name.ToLowerInvariant();
            entries[i] = new SearchEntry(
                model,
                string.Concat(name, "\0", model.RelativePath.ToLowerInvariant()),
                name.Length);
        });

        // The haystack begins with name + NUL, so its ordinal order is exactly
        // the stable name/path order needed inside each rank. Precomputing it
        // before sorting also avoids Path.GetFileName/Path.Join allocations in
        // the comparer — millions of them for a 100k-file snapshot.
        Array.Sort(entries, SearchEntryComparer.Instance);

        _models = new ModelFile[entries.Length];
        _haystacks = new string[entries.Length];
        _nameLengths = new int[entries.Length];

        for (var i = 0; i < entries.Length; i++)
        {
            _models[i] = entries[i].Model;
            _haystacks[i] = entries[i].Haystack;
            _nameLengths[i] = entries[i].NameLength;
        }
    }

    public int Count => _models.Length;

    /// <summary>All indexed models in stable name/path order.</summary>
    public IReadOnlyList<ModelFile> AllModels => _models;

    /// <summary>
    /// Applies text and metadata filters, returning exact name matches first,
    /// then name prefixes, name contains, and finally path-only matches.
    /// </summary>
    public ModelSearchResult Search(ModelSearchQuery query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();

        var prepared = PreparedQuery.Create(query);
        if (prepared.IsEmpty)
        {
            return new ModelSearchResult(_models, stopwatch.Elapsed);
        }

        var ranks = new byte[_models.Length];

        if (_models.Length < 2_048)
        {
            SearchRange(0, _models.Length, prepared, ranks, cancellationToken);
        }
        else
        {
            var options = new ParallelOptions { CancellationToken = cancellationToken };
            Parallel.ForEach(
                Partitioner.Create(0, _models.Length),
                options,
                range => SearchRange(range.Item1, range.Item2, prepared, ranks, options.CancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();

        Span<int> counts = stackalloc int[PathContains + 1];
        counts.Clear();
        for (var i = 0; i < ranks.Length; i++)
        {
            counts[ranks[i]]++;
        }

        var matchCount = ranks.Length - counts[NoMatch];
        if (matchCount == 0)
        {
            return new ModelSearchResult([], stopwatch.Elapsed);
        }

        var results = new ModelFile[matchCount];
        Span<int> offsets = stackalloc int[PathContains + 1];
        offsets.Clear();
        offsets[NamePrefix] = counts[ExactName];
        offsets[NameContains] = offsets[NamePrefix] + counts[NamePrefix];
        offsets[PathContains] = offsets[NameContains] + counts[NameContains];

        for (var i = 0; i < ranks.Length; i++)
        {
            var rank = ranks[i];
            if (rank != NoMatch)
            {
                results[offsets[rank]++] = _models[i];
            }
        }

        return new ModelSearchResult(results, stopwatch.Elapsed);
    }

    private void SearchRange(
        int start,
        int end,
        PreparedQuery query,
        byte[] ranks,
        CancellationToken cancellationToken)
    {
        for (var i = start; i < end; i++)
        {
            // Checking in small batches keeps cancellation prompt without putting
            // a volatile read in the hottest part of every file comparison.
            if ((i & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var model = _models[i];
            if (!MatchesMetadata(model, query))
            {
                continue;
            }

            ranks[i] = Rank(_haystacks[i], _nameLengths[i], query);
        }
    }

    private static bool MatchesMetadata(ModelFile model, PreparedQuery query)
    {
        if (query.Extension is not null &&
            !model.RelativePath.EndsWith(query.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.MinimumSize is { } minimum && model.Size < minimum)
        {
            return false;
        }

        if (query.MaximumSizeExclusive is { } maximum && model.Size >= maximum)
        {
            return false;
        }

        if (query.RootId is { } rootId && model.RootId != rootId)
        {
            return false;
        }

        return query.FolderRelativePath is null ||
               IsInsideFolder(model.RelativePath, query.FolderRelativePath);
    }

    private static bool IsInsideFolder(string relativePath, string folder)
    {
        if (folder.Length == 0)
        {
            return true;
        }

        if (!relativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ||
            relativePath.Length <= folder.Length)
        {
            return false;
        }

        var boundary = relativePath[folder.Length];
        return boundary == Path.DirectorySeparatorChar ||
               boundary == Path.AltDirectorySeparatorChar;
    }

    private static byte Rank(string haystack, int nameLength, PreparedQuery query)
    {
        if (query.Terms.Length == 0)
        {
            return PathContains;
        }

        var name = haystack.AsSpan(0, nameLength);
        var path = haystack.AsSpan(nameLength + 1);
        var everyTermInName = true;

        foreach (var term in query.Terms)
        {
            if (name.IndexOf(term, StringComparison.Ordinal) >= 0)
            {
                continue;
            }

            everyTermInName = false;
            if (path.IndexOf(term, StringComparison.Ordinal) < 0)
            {
                return NoMatch;
            }
        }

        if (!everyTermInName)
        {
            return PathContains;
        }

        var text = query.NormalizedText.AsSpan();
        if (name.SequenceEqual(text) || NameStemEquals(name, text))
        {
            return ExactName;
        }

        return name.StartsWith(text, StringComparison.Ordinal)
            ? NamePrefix
            : NameContains;
    }

    /// <summary>
    /// Treats a query without an extension as an exact name. Users type
    /// "benchy", not "benchy.stl", and both should receive the top rank.
    /// </summary>
    private static bool NameStemEquals(ReadOnlySpan<char> name, ReadOnlySpan<char> text)
    {
        if (!name.StartsWith(text, StringComparison.Ordinal) || name.Length <= text.Length + 1)
        {
            return false;
        }

        return name[text.Length] == '.' && name[(text.Length + 1)..].IndexOf('.') < 0;
    }

    private readonly record struct SearchEntry(ModelFile Model, string Haystack, int NameLength);

    private sealed class SearchEntryComparer : IComparer<SearchEntry>
    {
        public static SearchEntryComparer Instance { get; } = new();

        public int Compare(SearchEntry x, SearchEntry y)
        {
            var byHaystack = StringComparer.Ordinal.Compare(x.Haystack, y.Haystack);
            return byHaystack != 0 ? byHaystack : x.Model.Id.CompareTo(y.Model.Id);
        }
    }

    private sealed record PreparedQuery(
        string NormalizedText,
        string[] Terms,
        string? Extension,
        long? MinimumSize,
        long? MaximumSizeExclusive,
        long? RootId,
        string? FolderRelativePath)
    {
        public bool IsEmpty =>
            Terms.Length == 0 &&
            Extension is null &&
            MinimumSize is null &&
            MaximumSizeExclusive is null &&
            RootId is null &&
            FolderRelativePath is null;

        public static PreparedQuery Create(ModelSearchQuery query)
        {
            var terms = (query.Text ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(term => term.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var extension = string.IsNullOrWhiteSpace(query.Extension)
                ? null
                : query.Extension.StartsWith('.') ? query.Extension : $".{query.Extension}";

            var folder = string.IsNullOrWhiteSpace(query.FolderRelativePath)
                ? null
                : Path.TrimEndingDirectorySeparator(query.FolderRelativePath.Trim());

            return new PreparedQuery(
                string.Join(' ', terms),
                terms,
                extension,
                query.MinimumSize,
                query.MaximumSizeExclusive,
                query.RootId,
                folder);
        }
    }
}

/// <param name="MaximumSizeExclusive">
/// Exclusive upper bound, so adjacent UI ranges meet without overlapping.
/// </param>
public readonly record struct ModelSearchQuery(
    string? Text = null,
    string? Extension = null,
    long? MinimumSize = null,
    long? MaximumSizeExclusive = null,
    long? RootId = null,
    string? FolderRelativePath = null);

public readonly record struct ModelSearchResult(
    IReadOnlyList<ModelFile> Models,
    TimeSpan Elapsed);
