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
///
/// Every other sort order works the same way: a permutation of the entries is
/// built once per field, then a query walks it and keeps the matches. Sorting is
/// therefore never on the keystroke path — only the first query that asks for a
/// given order pays for it, and it pays once per snapshot.
/// </remarks>
public sealed class ModelSearchIndex
{
    private const byte NoMatch = 0;
    private const byte ExactName = 1;
    private const byte NamePrefix = 2;
    private const byte NameContains = 3;
    private const byte PathContains = 4;

    private static readonly int SortFieldCount = Enum.GetValues<ModelSortField>().Length;

    private readonly ModelFile[] _models;
    private readonly string[] _haystacks;
    private readonly int[] _nameLengths;

    /// <summary>
    /// One lazily built permutation per <see cref="ModelSortField"/>, indexed by
    /// the enum value. Null means "not built yet"; the array itself never grows,
    /// so publishing a slot with a single interlocked write is enough to make it
    /// safe for the concurrent searches a fast typist produces.
    /// </summary>
    private readonly int[]?[] _orders = new int[]?[SortFieldCount];

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
    /// Applies text and metadata filters and returns the survivors in the order
    /// the query asked for. Under <see cref="ModelSortField.Relevance"/> that is
    /// exact name matches first, then name prefixes, name contains, and finally
    /// path-only matches.
    /// </summary>
    public ModelSearchResult Search(ModelSearchQuery query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();

        var prepared = PreparedQuery.Create(query);

        // Relevance needs something to be relevant to. With no search text every
        // entry would rank the same, so it degrades to the name order the
        // snapshot is already stored in.
        var byRank = prepared.Sort == ModelSortField.Relevance && prepared.Terms.Length > 0;
        var order = byRank ? null : OrderFor(prepared.Sort);

        if (prepared.IsEmpty)
        {
            return new ModelSearchResult(Arrange(order, prepared.Descending), stopwatch.Elapsed);
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

        if (byRank)
        {
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
        }
        else
        {
            Gather(order, ranks, prepared.Descending, results);
        }

        return new ModelSearchResult(results, stopwatch.Elapsed);
    }

    /// <summary>
    /// Copies the matched entries out in the order of <paramref name="order"/>,
    /// which is null when the entries are already in the requested order.
    /// </summary>
    private void Gather(int[]? order, byte[] ranks, bool descending, ModelFile[] results)
    {
        var written = 0;

        for (var k = 0; k < ranks.Length; k++)
        {
            // A descending sort walks the permutation backwards rather than
            // reversing the results afterwards.
            var slot = descending ? ranks.Length - 1 - k : k;
            var entry = order is null ? slot : order[slot];

            if (ranks[entry] != NoMatch)
            {
                results[written++] = _models[entry];
            }
        }
    }

    /// <summary>
    /// The whole snapshot in the requested order, for a query with nothing to
    /// filter on. Ascending name order is how the entries are stored, so the
    /// common case of an empty search box hands back the shared array untouched.
    /// </summary>
    private IReadOnlyList<ModelFile> Arrange(int[]? order, bool descending)
    {
        if (order is null && !descending)
        {
            return _models;
        }

        var arranged = new ModelFile[_models.Length];
        for (var k = 0; k < arranged.Length; k++)
        {
            var slot = descending ? arranged.Length - 1 - k : k;
            arranged[k] = _models[order is null ? slot : order[slot]];
        }

        return arranged;
    }

    /// <summary>
    /// The permutation putting the entries in <paramref name="field"/> order, or
    /// null when they are already in it.
    /// </summary>
    /// <remarks>
    /// Built on the first query that asks for the field and kept for the life of
    /// the snapshot. Two searches racing to build the same order both succeed and
    /// produce identical arrays, so the loser simply discards its own.
    /// </remarks>
    private int[]? OrderFor(ModelSortField field)
    {
        // The entries are stored in name order, so name — and relevance with
        // nothing to rank — need no permutation at all.
        if (field is ModelSortField.Relevance or ModelSortField.Name)
        {
            return null;
        }

        var slot = (int)field;
        if (Volatile.Read(ref _orders[slot]) is { } cached)
        {
            return cached;
        }

        var built = BuildOrder(field);
        return Interlocked.CompareExchange(ref _orders[slot], built, null) ?? built;
    }

    private int[] BuildOrder(ModelSortField field)
    {
        var order = new int[_models.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // The entry index is the tiebreak, and the entries are already in name
        // order, so equal keys stay alphabetical without needing a stable sort.
        Array.Sort(order, (a, b) =>
        {
            var byKey = CompareKeys(field, a, b);
            return byKey != 0 ? byKey : a.CompareTo(b);
        });

        return order;
    }

    private int CompareKeys(ModelSortField field, int a, int b) => field switch
    {
        ModelSortField.DateModified => _models[a].ModifiedTicks.CompareTo(_models[b].ModifiedTicks),
        ModelSortField.Size => _models[a].Size.CompareTo(_models[b].Size),
        ModelSortField.Format => ExtensionOf(a).CompareTo(ExtensionOf(b), StringComparison.Ordinal),
        ModelSortField.Folder => CompareFolders(a, b),
        _ => 0,
    };

    /// <summary>
    /// Roots first, then the folder below them, so the files of one project land
    /// together even when two roots contain a folder of the same name.
    /// </summary>
    private int CompareFolders(int a, int b)
    {
        var byRoot = string.Compare(
            _models[a].RootPath,
            _models[b].RootPath,
            StringComparison.OrdinalIgnoreCase);

        return byRoot != 0
            ? byRoot
            : FolderOf(a).CompareTo(FolderOf(b), StringComparison.Ordinal);
    }

    /// <summary>
    /// The extension, read straight off the lowercased haystack. Sort keys are
    /// compared O(n log n) times, so none of them may allocate.
    /// </summary>
    private ReadOnlySpan<char> ExtensionOf(int index)
    {
        var name = _haystacks[index].AsSpan(0, _nameLengths[index]);
        var dot = name.LastIndexOf('.');
        return dot < 0 ? default : name[dot..];
    }

    /// <summary>The folder part of the relative path, likewise allocation-free.</summary>
    private ReadOnlySpan<char> FolderOf(int index)
    {
        var path = _haystacks[index].AsSpan(_nameLengths[index] + 1);
        var separator = path.LastIndexOfAny('\\', '/');
        return separator < 0 ? default : path[..separator];
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
        string? FolderRelativePath,
        ModelSortField Sort,
        bool Descending)
    {
        /// <summary>
        /// Nothing to filter on, so every entry survives. Sort is deliberately
        /// not part of this: an order still has to be applied to all of them.
        /// </summary>
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
                folder,
                query.Sort,
                query.Descending);
        }
    }
}

/// <summary>
/// What the results are ordered by.
/// </summary>
/// <remarks>
/// Every field here is answerable from the index alone. Genuinely
/// geometry-derived orders — triangle count, printed volume, whether a model
/// fits a given bed — need the file parsed, which the index deliberately never
/// does during a scan.
/// </remarks>
public enum ModelSortField
{
    /// <summary>Search rank, falling back to name when there is no search text.</summary>
    Relevance,
    Name,
    DateModified,
    Size,

    /// <summary>File extension, so one format is grouped together.</summary>
    Format,

    /// <summary>Containing folder, so the parts of one project stay adjacent.</summary>
    Folder,
}

/// <param name="MaximumSizeExclusive">
/// Exclusive upper bound, so adjacent UI ranges meet without overlapping.
/// </param>
/// <param name="Descending">
/// Reverses <paramref name="Sort" />. Ignored under
/// <see cref="ModelSortField.Relevance" /> with search text, where the ordering
/// is by rank and "least relevant first" is not a thing anyone wants.
/// </param>
public readonly record struct ModelSearchQuery(
    string? Text = null,
    string? Extension = null,
    long? MinimumSize = null,
    long? MaximumSizeExclusive = null,
    long? RootId = null,
    string? FolderRelativePath = null,
    ModelSortField Sort = ModelSortField.Relevance,
    bool Descending = false);

public readonly record struct ModelSearchResult(
    IReadOnlyList<ModelFile> Models,
    TimeSpan Elapsed);
