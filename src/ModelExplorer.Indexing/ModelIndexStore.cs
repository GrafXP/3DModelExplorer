using Microsoft.Data.Sqlite;

namespace ModelExplorer.Indexing;

/// <summary>
/// SQLite persistence for the file index.
/// </summary>
/// <remarks>
/// Deliberately synchronous throughout. Microsoft.Data.Sqlite's async methods are
/// wrappers over blocking calls, so awaiting them buys a state machine and
/// nothing else; callers run the store on a background task instead.
///
/// Every operation takes the same lock. A <see cref="SqliteConnection"/> is not
/// thread-safe, and SQLite serialises writers regardless, so one connection under
/// one lock costs nothing and removes a whole class of bug.
/// </remarks>
public sealed class ModelIndexStore : IDisposable
{
    /// <summary>
    /// Rows per transaction. Big enough that commit overhead disappears against
    /// the inserts, small enough that a cancelled scan discards very little.
    /// </summary>
    public const int BatchSize = 10_000;

    private readonly Lock _gate = new();
    private readonly SqliteConnection _connection;

    public ModelIndexStore(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();
        Configure();
        CreateSchema();
    }

    /// <summary>Alongside the thumbnail cache that arrives in step 6.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModelExplorer",
        "index.db");

    public IReadOnlyList<LibraryRoot> GetRoots()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT id, path, is_network, added_utc, last_scan_utc FROM roots ORDER BY path;";

            var roots = new List<LibraryRoot>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                roots.Add(ReadRoot(reader));
            }

            return roots;
        }
    }

    /// <summary>
    /// Adds a root, or returns the existing one if the folder is already in the
    /// library. Re-adding a folder is a no-op rather than an error — the user's
    /// intent ("I want this indexed") is already satisfied.
    /// </summary>
    public LibraryRoot AddRoot(string path, bool isNetwork)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO roots (path, is_network, added_utc)
                VALUES ($path, $network, $added)
                ON CONFLICT(path) DO UPDATE SET is_network = excluded.is_network
                RETURNING id, path, is_network, added_utc, last_scan_utc;
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$network", isNetwork ? 1 : 0);
            command.Parameters.AddWithValue("$added", DateTime.UtcNow.Ticks);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Could not add library root '{path}'.");
            }

            return ReadRoot(reader);
        }
    }

    public void RemoveRoot(long rootId)
    {
        lock (_gate)
        {
            // Files are deleted explicitly rather than by cascade, so removal does
            // not depend on the foreign_keys pragma being in force.
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM files WHERE root_id = $id;
                DELETE FROM roots WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", rootId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Drops every file row for a root, ahead of a full rescan.</summary>
    public void ClearRoot(long rootId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM files WHERE root_id = $id;";
            command.Parameters.AddWithValue("$id", rootId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Records that a root has been scanned end to end.</summary>
    public void MarkScanned(long rootId, DateTime utc)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE roots SET last_scan_utc = $t WHERE id = $id;";
            command.Parameters.AddWithValue("$t", utc.Ticks);
            command.Parameters.AddWithValue("$id", rootId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Writes one batch in a single transaction against one prepared statement.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Without the transaction every insert is its own commit;
    /// without the prepared statement every insert re-plans the same SQL. Together
    /// they are the difference between a scan bounded by the disk and one bounded
    /// by SQLite.
    /// </remarks>
    public void WriteBatch(IReadOnlyList<ScannedFile> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO files (root_id, relative_path, size, mtime_ticks)
                VALUES ($root, $path, $size, $mtime)
                ON CONFLICT(root_id, relative_path) DO UPDATE
                    SET size = excluded.size, mtime_ticks = excluded.mtime_ticks;
                """;

            var root = command.Parameters.Add("$root", SqliteType.Integer);
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var size = command.Parameters.Add("$size", SqliteType.Integer);
            var mtime = command.Parameters.Add("$mtime", SqliteType.Integer);
            command.Prepare();

            foreach (var file in batch)
            {
                root.Value = file.RootId;
                path.Value = file.RelativePath;
                size.Value = file.Size;
                mtime.Value = file.ModifiedTicks;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Reads the whole index. Roots are read first so every file under a root can
    /// share that root's path string instead of carrying its own copy.
    /// </summary>
    public IReadOnlyList<ModelFile> LoadFiles()
    {
        lock (_gate)
        {
            var rootPaths = new Dictionary<long, string>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT id, path FROM roots;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rootPaths[reader.GetInt64(0)] = reader.GetString(1);
                }
            }

            if (rootPaths.Count == 0)
            {
                return [];
            }

            // Unordered on purpose. Insertion order is directory-walk order, which
            // already groups a folder's files together, and sorting 100k rows in
            // SQLite would cost more than the read itself. Step 4 orders by rank.
            var files = new List<ModelFile>(4096);
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT id, root_id, relative_path, size, mtime_ticks FROM files;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var rootId = reader.GetInt64(1);
                    if (!rootPaths.TryGetValue(rootId, out var rootPath))
                    {
                        continue;
                    }

                    files.Add(new ModelFile
                    {
                        Id = reader.GetInt64(0),
                        RootId = rootId,
                        RootPath = rootPath,
                        RelativePath = reader.GetString(2),
                        Size = reader.GetInt64(3),
                        ModifiedTicks = reader.GetInt64(4),
                    });
                }
            }

            return files;
        }
    }

    public void Dispose() => _connection.Dispose();

    private static LibraryRoot ReadRoot(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetInt64(2) != 0,
        new DateTime(reader.GetInt64(3), DateTimeKind.Utc),
        reader.IsDBNull(4) ? null : new DateTime(reader.GetInt64(4), DateTimeKind.Utc));

    private void Configure()
    {
        // WAL keeps the UI's reads from queueing behind the scan's writes.
        // synchronous=NORMAL means a commit does not wait on an fsync, which is
        // what makes 10k-row transactions cheap; the exposure is losing the tail
        // of a scan to a power cut, and a rescan rebuilds it.
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA temp_store=MEMORY;");
    }

    private void CreateSchema()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS roots (
                id            INTEGER PRIMARY KEY,
                path          TEXT    NOT NULL COLLATE NOCASE,
                is_network    INTEGER NOT NULL,
                added_utc     INTEGER NOT NULL,
                last_scan_utc INTEGER
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_roots_path ON roots(path);

            CREATE TABLE IF NOT EXISTS files (
                id            INTEGER PRIMARY KEY,
                root_id       INTEGER NOT NULL,
                relative_path TEXT    NOT NULL,
                size          INTEGER NOT NULL,
                mtime_ticks   INTEGER NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_files_root_path ON files(root_id, relative_path);
            """);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
