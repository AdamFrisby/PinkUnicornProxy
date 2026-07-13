using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PinkUnicornProxy.Configuration;

namespace PinkUnicornProxy.Rewriting;

internal sealed partial class HistoryRewriteCache : BackgroundService
{
    private const int ApplicationId = 0x50555050;
    private const int CurrentSchemaVersion = 2;
    private const int CachePolicyVersion = 1;
    private const int HashLength = 32;
    private const int LockStripeCount = 64;
    private const int MaximumVacuumPagesPerSweep = 16_384;
    private const long EstimatedEntryOverhead = 256;
    private const long EstimatedItemOverhead = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 256,
        WriteIndented = false,
    };

    private readonly SemaphoreSlim[] creationLocks;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly HistoryRewriteCacheOptions options;
    private readonly TimeProvider timeProvider;
    private readonly string connectionString;
    private readonly byte[] cacheNamespace;
    private readonly ILogger<HistoryRewriteCache>? logger;
    private readonly bool enabled;
    private int healthy;
    private volatile bool initialized;

    public HistoryRewriteCache(
        IOptions<PinkUnicornOptions> options,
        IHostEnvironment environment,
        ILogger<HistoryRewriteCache> logger)
        : this(
            options.Value.HistoryRewrite.Cache,
            TimeProvider.System,
            environment.ContentRootPath,
            logger,
            options.Value.Mode == ProxyMode.Rewrite,
            options.Value.HistoryRewrite.TriggerTokens)
    {
    }

    internal HistoryRewriteCache(
        HistoryRewriteCacheOptions options,
        TimeProvider? timeProvider = null,
        string? contentRootPath = null,
        ILogger<HistoryRewriteCache>? logger = null,
        bool rewriteModeEnabled = true,
        IReadOnlyList<string>? triggerTokens = null)
    {
        this.options = options;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger;
        enabled = options.Enabled && rewriteModeEnabled;
        healthy = enabled ? 0 : 1;
        cacheNamespace = ComputeCacheNamespace(
            options.Generation,
            triggerTokens ?? ["!!NO!!"]);
        DatabasePath = ResolveDatabasePath(options.DatabasePath, contentRootPath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = Math.Max(1, checked((int)Math.Ceiling(options.BusyTimeout.TotalSeconds))),
        }.ToString();
        creationLocks = Enumerable.Range(0, LockStripeCount)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();
    }

    public bool Enabled => enabled;

    public bool IsHealthy => !Enabled || Volatile.Read(ref healthy) == 1;

    internal string DatabasePath { get; }

    public static HistoryCacheSnapshot CreateSnapshot(
        ProviderRequestKind provider,
        JsonNode history)
    {
        if (history is JsonArray array)
        {
            byte[][] itemHashes = new byte[array.Count][];
            for (int index = 0; index < array.Count; index++)
            {
                byte[] itemBytes = JsonSerializer.SerializeToUtf8Bytes(array[index], SerializerOptions);
                itemHashes[index] = SHA256.HashData(itemBytes);
            }

            return new HistoryCacheSnapshot(
                provider,
                true,
                ComputeArrayHistoryHash(itemHashes),
                itemHashes);
        }

        return new HistoryCacheSnapshot(
            provider,
            false,
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(history, SerializerOptions)),
            []);
    }

    public async Task<HistoryCacheMatch?> GetLongestPrefixAsync(
        HistoryCacheSnapshot snapshot,
        IReadOnlyList<int> triggerTurnIndices,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = snapshot.IsArray
                ? """
                        SELECT history_hash,
                               original_item_count,
                               rewritten_history,
                               edit_count,
                               opaque_block_count,
                               created_sequence
                        FROM history_rewrite_entries
                        WHERE cache_namespace = $cache_namespace
                          AND provider = $provider
                          AND is_array = 1
                          AND history_hash = $history_hash
                          AND revision_anchor_hash = $revision_anchor_hash
                          AND last_access_utc > $expiry_cutoff

                        UNION ALL

                        SELECT history_hash,
                               original_item_count,
                               rewritten_history,
                               edit_count,
                               opaque_block_count,
                               created_sequence
                        FROM history_rewrite_entries
                        WHERE cache_namespace = $cache_namespace
                          AND provider = $provider
                          AND is_array = 1
                          AND revision_anchor_hash = $revision_anchor_hash
                          AND original_item_count < $item_count
                          AND last_access_utc > $expiry_cutoff
                          AND original_item_hashes = substr(
                                $original_item_hashes,
                                1,
                                length(original_item_hashes))
                        ORDER BY original_item_count DESC
                        LIMIT 1;
                        """
                : """
                        SELECT history_hash,
                               original_item_count,
                               rewritten_history,
                               edit_count,
                               opaque_block_count,
                               created_sequence
                        FROM history_rewrite_entries
                        WHERE cache_namespace = $cache_namespace
                          AND provider = $provider
                          AND is_array = 0
                          AND history_hash = $history_hash
                          AND last_access_utc > $expiry_cutoff
                        LIMIT 1;
                        """;
            command.Parameters.Add("$cache_namespace", SqliteType.Blob).Value = cacheNamespace;
            command.Parameters.Add("$provider", SqliteType.Text).Value =
                GetProviderKey(snapshot.Provider);
            command.Parameters.Add("$expiry_cutoff", SqliteType.Integer).Value =
                GetExpiryCutoffMilliseconds();
            if (snapshot.IsArray)
            {
                command.Parameters.Add("$history_hash", SqliteType.Blob).Value =
                    snapshot.HistoryHash;
                command.Parameters.Add("$revision_anchor_hash", SqliteType.Blob).Value =
                    CreateRevisionAnchorHash(snapshot, triggerTurnIndices);
                command.Parameters.Add("$item_count", SqliteType.Integer).Value =
                    snapshot.OriginalItemHashes.Length;
                command.Parameters.Add("$original_item_hashes", SqliteType.Blob).Value =
                    FlattenItemHashes(snapshot.OriginalItemHashes);
            }
            else
            {
                command.Parameters.Add("$history_hash", SqliteType.Blob).Value =
                    snapshot.HistoryHash;
            }

            CacheCandidate? candidate = null;
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    int prefixItemCount = reader.GetInt32(1);
                    if (!snapshot.IsArray
                        || !triggerTurnIndices.Any(index => index >= prefixItemCount))
                    {
                        candidate = new CacheCandidate(
                            (byte[])reader[0],
                            reader.GetInt64(5),
                            new HistoryCacheMatch(
                                (byte[])reader[2],
                                reader.GetInt32(3),
                                reader.GetInt32(4),
                                prefixItemCount,
                                snapshot.OriginalItemHashes.Length == prefixItemCount));
                    }
                }
            }

            if (candidate is null)
            {
                return null;
            }

            bool touched = await TouchAsync(
                snapshot.Provider,
                snapshot.IsArray,
                candidate.HistoryHash,
                candidate.CreatedSequence,
                cancellationToken);
            MarkHealthy();
            return touched ? candidate.Match : null;
        }
        catch (HistoryRewriteCacheException)
        {
            MarkUnhealthy();
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            MarkUnhealthy();
            throw new HistoryRewriteCacheException("The durable history rewrite cache could not be read.", exception);
        }
    }

    public async ValueTask<IDisposable> AcquireCreationLockAsync(
        HistoryCacheSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return NoOpDisposable.Instance;
        }

        int stripe = (int)(BitConverter.ToUInt32(snapshot.HistoryHash, 0) % LockStripeCount);
        SemaphoreSlim semaphore = creationLocks[stripe];
        await semaphore.WaitAsync(cancellationToken);
        return new SemaphoreReleaser(semaphore);
    }

    public async Task<HistoryCacheStoreResult> StoreAsync(
        HistoryCacheSnapshot snapshot,
        IReadOnlyList<int> triggerTurnIndices,
        byte[] rewrittenHistory,
        int editCount,
        int opaqueBlockCount,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("Cannot store a revision when the cache is disabled.");
        }

        byte[] originalItemHashes = FlattenItemHashes(snapshot.OriginalItemHashes);
        byte[] revisionAnchorHash = CreateRevisionAnchorHash(snapshot, triggerTurnIndices);
        long size = checked(
            rewrittenHistory.LongLength
            + originalItemHashes.LongLength
            + cacheNamespace.LongLength
            + snapshot.HistoryHash.LongLength
            + revisionAnchorHash.LongLength
            + EstimatedEntryOverhead
            + (snapshot.OriginalItemHashes.LongLength * EstimatedItemOverhead));
        if (size > options.MaximumBytes)
        {
            throw new HistoryRewriteCacheException(
                "A single history revision exceeds the configured durable cache size limit.");
        }

        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await writeLock.WaitAsync(cancellationToken);
            try
            {
                await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
                await using SqliteTransaction transaction =
                    (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                await DeleteExpiredAsync(connection, transaction, cancellationToken);
                long accessSequence = await GetNextAccessSequenceAsync(
                    connection,
                    transaction,
                    cancellationToken);

                int inserted;
                await using (SqliteCommand insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT OR IGNORE INTO history_rewrite_entries (
                            cache_namespace,
                            generation,
                            provider,
                            is_array,
                            history_hash,
                            revision_anchor_hash,
                            original_item_count,
                            original_item_hashes,
                            rewritten_history,
                            edit_count,
                            opaque_block_count,
                            size_bytes,
                            created_utc,
                            last_access_utc,
                            created_sequence,
                            last_access_sequence)
                        VALUES (
                            $cache_namespace,
                            $generation,
                            $provider,
                            $is_array,
                            $history_hash,
                            $revision_anchor_hash,
                            $original_item_count,
                            $original_item_hashes,
                            $rewritten_history,
                            $edit_count,
                            $opaque_block_count,
                            $size_bytes,
                            $now,
                            $now,
                            $last_access_sequence,
                            $last_access_sequence);
                        """;
                    AddKeyParameters(
                        insert,
                        cacheNamespace,
                        snapshot.Provider,
                        snapshot.IsArray,
                        snapshot.HistoryHash);
                    insert.Parameters.Add("$generation", SqliteType.Integer).Value =
                        options.Generation;
                    insert.Parameters.Add("$revision_anchor_hash", SqliteType.Blob).Value =
                        revisionAnchorHash;
                    insert.Parameters.Add("$original_item_count", SqliteType.Integer).Value =
                        snapshot.OriginalItemHashes.Length;
                    insert.Parameters.Add("$original_item_hashes", SqliteType.Blob).Value =
                        originalItemHashes;
                    insert.Parameters.Add("$rewritten_history", SqliteType.Blob).Value =
                        rewrittenHistory;
                    insert.Parameters.Add("$edit_count", SqliteType.Integer).Value = editCount;
                    insert.Parameters.Add("$opaque_block_count", SqliteType.Integer).Value =
                        opaqueBlockCount;
                    insert.Parameters.Add("$size_bytes", SqliteType.Integer).Value = size;
                    insert.Parameters.Add("$now", SqliteType.Integer).Value = now;
                    insert.Parameters.Add("$last_access_sequence", SqliteType.Integer).Value =
                        accessSequence;
                    inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                await EnforceSizeLimitAsync(connection, transaction, cancellationToken);
                HistoryCacheMatch authoritative = await ReadExactAsync(
                    connection,
                    transaction,
                    cacheNamespace,
                    snapshot,
                    cancellationToken)
                    ?? throw new HistoryRewriteCacheException(
                        "The newly stored history revision was not retained by the durable cache.");
                await transaction.CommitAsync(cancellationToken);
                MarkHealthy();
                return new HistoryCacheStoreResult(authoritative, inserted == 1);
            }
            finally
            {
                writeLock.Release();
            }
        }
        catch (HistoryRewriteCacheException)
        {
            MarkUnhealthy();
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            MarkUnhealthy();
            throw new HistoryRewriteCacheException("The durable history rewrite cache could not be updated.", exception);
        }
    }

    internal async Task PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await writeLock.WaitAsync(cancellationToken);
            try
            {
                await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
                await using SqliteTransaction transaction =
                    (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await DeleteExpiredAsync(connection, transaction, cancellationToken);
                await EnforceSizeLimitAsync(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                await using SqliteCommand reclaim = connection.CreateCommand();
                reclaim.CommandText = "PRAGMA freelist_count;";
                long freePages = Convert.ToInt64(
                    await reclaim.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                int pagesToReclaim = checked((int)Math.Min(
                    freePages,
                    MaximumVacuumPagesPerSweep));
                if (pagesToReclaim > 0)
                {
                    reclaim.CommandText = $"PRAGMA incremental_vacuum({pagesToReclaim});";
                    await reclaim.ExecuteNonQueryAsync(cancellationToken);
                }

                reclaim.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                await reclaim.ExecuteNonQueryAsync(cancellationToken);
                MarkHealthy();
            }
            finally
            {
                writeLock.Release();
            }
        }
        catch (HistoryRewriteCacheException)
        {
            MarkUnhealthy();
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            MarkUnhealthy();
            throw new HistoryRewriteCacheException("The durable history rewrite cache could not be cleaned.", exception);
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Enabled)
        {
            await EnsureInitializedAsync(cancellationToken);
            await PurgeExpiredAsync(cancellationToken);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        TimeSpan sweepInterval = options.CleanupInterval <= options.TimeToLive
            ? options.CleanupInterval
            : options.TimeToLive;
        using PeriodicTimer timer = new(sweepInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PurgeExpiredAsync(stoppingToken);
                }
                catch (HistoryRewriteCacheException exception)
                {
                    if (logger is not null)
                    {
                        LogMaintenanceFailure(logger, exception);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized || !Enabled)
        {
            return;
        }

        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            string? directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                bool directoryAlreadyExisted = Directory.Exists(directory);
                Directory.CreateDirectory(directory);
                if (!directoryAlreadyExisted)
                {
                    SetOwnerOnlyPermissions(directory, isDirectory: true);
                }
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            DatabaseIdentity initialIdentity = await ReadDatabaseIdentityAsync(
                connection,
                transaction: null,
                cancellationToken);
            ValidateDatabaseIdentity(initialIdentity);
            if (initialIdentity.SchemaVersion == 0)
            {
                if (await CountSchemaObjectsAsync(connection, transaction: null, cancellationToken) != 0)
                {
                    throw new HistoryRewriteCacheException(
                        "The configured durable cache path contains an unrecognized schema.");
                }

                await using SqliteCommand autoVacuum = connection.CreateCommand();
                autoVacuum.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
                await autoVacuum.ExecuteNonQueryAsync(cancellationToken);
                autoVacuum.CommandText = "PRAGMA auto_vacuum;";
                long autoVacuumMode = Convert.ToInt64(
                    await autoVacuum.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (autoVacuumMode != 2)
                {
                    throw new HistoryRewriteCacheException(
                        "The durable cache could not enable incremental auto-vacuum.");
                }
            }

            await using SqliteTransaction migration = connection.BeginTransaction(deferred: false);
            DatabaseIdentity migrationIdentity = await ReadDatabaseIdentityAsync(
                connection,
                migration,
                cancellationToken);
            ValidateDatabaseIdentity(migrationIdentity);

            if (migrationIdentity.SchemaVersion == 0)
            {
                if (await CountSchemaObjectsAsync(connection, migration, cancellationToken) != 0)
                {
                    throw new HistoryRewriteCacheException(
                        "The configured durable cache path contains an unrecognized schema.");
                }

                await using SqliteCommand schema = connection.CreateCommand();
                schema.Transaction = migration;
                schema.CommandText = $"""
                    CREATE TABLE history_rewrite_entries (
                        cache_namespace BLOB NOT NULL CHECK (length(cache_namespace) = {HashLength}),
                        generation INTEGER NOT NULL CHECK (generation >= 1),
                        provider TEXT NOT NULL,
                        is_array INTEGER NOT NULL CHECK (is_array IN (0, 1)),
                        history_hash BLOB NOT NULL CHECK (length(history_hash) = {HashLength}),
                        revision_anchor_hash BLOB NOT NULL CHECK (length(revision_anchor_hash) = {HashLength}),
                        original_item_count INTEGER NOT NULL CHECK (original_item_count >= 0),
                        original_item_hashes BLOB NOT NULL,
                        rewritten_history BLOB NOT NULL,
                        edit_count INTEGER NOT NULL CHECK (edit_count >= 0),
                        opaque_block_count INTEGER NOT NULL CHECK (opaque_block_count >= 0),
                        size_bytes INTEGER NOT NULL CHECK (size_bytes > 0),
                        created_utc INTEGER NOT NULL,
                        last_access_utc INTEGER NOT NULL,
                        created_sequence INTEGER NOT NULL CHECK (created_sequence >= 1),
                        last_access_sequence INTEGER NOT NULL CHECK (last_access_sequence >= 1),
                        PRIMARY KEY (cache_namespace, provider, is_array, history_hash),
                        CHECK (length(original_item_hashes) = original_item_count * {HashLength})
                    ) WITHOUT ROWID;

                    CREATE TABLE history_rewrite_metadata (
                        singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                        access_sequence INTEGER NOT NULL CHECK (access_sequence >= 0),
                        total_size_bytes INTEGER NOT NULL CHECK (total_size_bytes >= 0)
                    );

                    INSERT INTO history_rewrite_metadata (
                        singleton,
                        access_sequence,
                        total_size_bytes)
                    VALUES (1, 0, 0);

                    CREATE TRIGGER history_rewrite_size_after_insert
                    AFTER INSERT ON history_rewrite_entries
                    BEGIN
                        UPDATE history_rewrite_metadata
                        SET total_size_bytes = total_size_bytes + NEW.size_bytes
                        WHERE singleton = 1;
                    END;

                    CREATE TRIGGER history_rewrite_size_after_delete
                    AFTER DELETE ON history_rewrite_entries
                    BEGIN
                        UPDATE history_rewrite_metadata
                        SET total_size_bytes = total_size_bytes - OLD.size_bytes
                        WHERE singleton = 1;
                    END;

                    CREATE INDEX history_rewrite_prefix
                        ON history_rewrite_entries (
                            cache_namespace,
                            provider,
                            is_array,
                            revision_anchor_hash,
                            original_item_count DESC);

                    CREATE INDEX history_rewrite_last_access
                        ON history_rewrite_entries (last_access_utc);

                    CREATE INDEX history_rewrite_lru
                        ON history_rewrite_entries (last_access_sequence);

                    PRAGMA application_id = {ApplicationId};
                    PRAGMA user_version = {CurrentSchemaVersion};
                    """;
                await schema.ExecuteNonQueryAsync(cancellationToken);
            }

            await migration.CommitAsync(cancellationToken);
            if (await ReadAutoVacuumModeAsync(connection, cancellationToken) != 2)
            {
                throw new HistoryRewriteCacheException(
                    "The durable cache schema does not use incremental auto-vacuum.");
            }

            await using (SqliteCommand pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode = WAL;";
                string? journalMode = Convert.ToString(
                    await pragmas.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    throw new HistoryRewriteCacheException(
                        "The durable cache could not enable SQLite WAL mode.");
                }
            }

            SetOwnerOnlyPermissions(DatabasePath, isDirectory: false);
            SetOwnerOnlyPermissions($"{DatabasePath}-wal", isDirectory: false);
            SetOwnerOnlyPermissions($"{DatabasePath}-shm", isDirectory: false);
            initialized = true;
            MarkHealthy();
        }
        catch (HistoryRewriteCacheException)
        {
            MarkUnhealthy();
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            MarkUnhealthy();
            throw new HistoryRewriteCacheException(
                "The durable history rewrite cache could not be initialized.",
                exception);
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private static async Task<DatabaseIdentity> ReadDatabaseIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM pragma_application_id(), pragma_user_version();";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new HistoryRewriteCacheException(
                "The durable cache metadata could not be read.");
        }

        return new DatabaseIdentity(reader.GetInt32(0), reader.GetInt32(1));
    }

    private static async Task<long> CountSchemaObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*)
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%';
            """;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadAutoVacuumModeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA auto_vacuum;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ValidateDatabaseIdentity(DatabaseIdentity identity)
    {
        if (identity.ApplicationId is not 0 and not ApplicationId)
        {
            throw new HistoryRewriteCacheException(
                "The configured durable cache path contains a database owned by another application.");
        }

        if (identity.ApplicationId == 0 && identity.SchemaVersion != 0)
        {
            throw new HistoryRewriteCacheException(
                "The configured durable cache path contains an unrecognized versioned schema.");
        }

        if (identity.SchemaVersion > CurrentSchemaVersion)
        {
            throw new HistoryRewriteCacheException(
                $"The durable cache schema version {identity.SchemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }

        if (identity.SchemaVersion is not 0 and not CurrentSchemaVersion)
        {
            throw new HistoryRewriteCacheException(
                $"The durable cache schema version {identity.SchemaVersion} has no supported migration to version {CurrentSchemaVersion}.");
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand pragmas = connection.CreateCommand();
            long journalSizeLimit = Math.Clamp(
                options.MaximumBytes / 64,
                4L * 1024 * 1024,
                256L * 1024 * 1024);
            pragmas.CommandText = $"""
                PRAGMA synchronous = FULL;
                PRAGMA secure_delete = ON;
                PRAGMA busy_timeout = {Math.Max(100, checked((long)options.BusyTimeout.TotalMilliseconds))};
                PRAGMA wal_autocheckpoint = 1000;
                PRAGMA journal_size_limit = {journalSizeLimit};
                """;
            await pragmas.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<bool> TouchAsync(
        ProviderRequestKind provider,
        bool isArray,
        byte[] historyHash,
        long createdSequence,
        CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            long accessSequence = await GetNextAccessSequenceAsync(
                connection,
                transaction,
                cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE history_rewrite_entries
                SET last_access_utc = $last_access_utc,
                    last_access_sequence = $last_access_sequence
                WHERE cache_namespace = $cache_namespace
                  AND provider = $provider
                  AND is_array = $is_array
                  AND history_hash = $history_hash
                  AND created_sequence = $created_sequence
                  AND last_access_utc > $expiry_cutoff;
                """;
            AddKeyParameters(command, cacheNamespace, provider, isArray, historyHash);
            command.Parameters.Add("$created_sequence", SqliteType.Integer).Value = createdSequence;
            command.Parameters.Add("$expiry_cutoff", SqliteType.Integer).Value =
                GetExpiryCutoffMilliseconds();
            command.Parameters.Add("$last_access_utc", SqliteType.Integer).Value =
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            command.Parameters.Add("$last_access_sequence", SqliteType.Integer).Value =
                accessSequence;
            bool touched = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            await transaction.CommitAsync(cancellationToken);
            return touched;
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task DeleteExpiredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM history_rewrite_entries
            WHERE last_access_utc <= $expiry_cutoff;
            """;
        command.Parameters.Add("$expiry_cutoff", SqliteType.Integer).Value =
            GetExpiryCutoffMilliseconds();
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnforceSizeLimitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            long totalSize = await ReadTotalSizeAsync(
                connection,
                transaction,
                cancellationToken);
            if (totalSize <= options.MaximumBytes)
            {
                return;
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                WITH candidates AS (
                    SELECT last_access_sequence,
                           size_bytes
                    FROM history_rewrite_entries
                    ORDER BY last_access_sequence
                    LIMIT 512
                ),
                ranked AS (
                    SELECT last_access_sequence,
                           SUM(size_bytes) OVER (
                               ORDER BY last_access_sequence) AS evicted_bytes
                    FROM candidates
                ),
                cutoff AS (
                    SELECT COALESCE(
                        (
                            SELECT last_access_sequence
                            FROM ranked
                            WHERE evicted_bytes >= $bytes_to_free
                            ORDER BY last_access_sequence
                            LIMIT 1
                        ),
                        (
                            SELECT max(last_access_sequence)
                            FROM ranked
                        )) AS last_access_sequence
                )
                DELETE FROM history_rewrite_entries
                WHERE last_access_sequence <= (
                    SELECT last_access_sequence
                    FROM cutoff);
                """;
            command.Parameters.Add("$bytes_to_free", SqliteType.Integer).Value =
                totalSize - options.MaximumBytes;
            int deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            if (deleted == 0)
            {
                throw new HistoryRewriteCacheException(
                    "The durable cache could not enforce its configured size limit.");
            }
        }
    }

    private static async Task<long> ReadTotalSizeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT total_size_bytes
            FROM history_rewrite_metadata
            WHERE singleton = 1;
            """;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> GetNextAccessSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE history_rewrite_metadata
            SET access_sequence = access_sequence + 1
            WHERE singleton = 1
            RETURNING access_sequence;
            """;
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<HistoryCacheMatch?> ReadExactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] cacheNamespace,
        HistoryCacheSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT original_item_count,
                   rewritten_history,
                   edit_count,
                   opaque_block_count
            FROM history_rewrite_entries
            WHERE cache_namespace = $cache_namespace
              AND provider = $provider
              AND is_array = $is_array
              AND history_hash = $history_hash;
            """;
        AddKeyParameters(
            command,
            cacheNamespace,
            snapshot.Provider,
            snapshot.IsArray,
            snapshot.HistoryHash);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new HistoryCacheMatch(
            (byte[])reader[1],
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(0),
            IsExact: true);
    }

    private static void AddKeyParameters(
        SqliteCommand command,
        byte[] cacheNamespace,
        ProviderRequestKind provider,
        bool isArray,
        byte[] historyHash)
    {
        command.Parameters.Add("$cache_namespace", SqliteType.Blob).Value = cacheNamespace;
        command.Parameters.Add("$provider", SqliteType.Text).Value = GetProviderKey(provider);
        command.Parameters.Add("$is_array", SqliteType.Integer).Value = isArray ? 1 : 0;
        command.Parameters.Add("$history_hash", SqliteType.Blob).Value = historyHash;
    }

    private static string GetProviderKey(ProviderRequestKind provider) => provider switch
    {
        ProviderRequestKind.OpenAIChatCompletions => "openai-chat-completions",
        ProviderRequestKind.OpenAIResponses => "openai-responses",
        ProviderRequestKind.AnthropicMessages => "anthropic-messages",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider kind."),
    };

    private long GetExpiryCutoffMilliseconds() =>
        (timeProvider.GetUtcNow() - options.TimeToLive).ToUnixTimeMilliseconds();

    private static byte[] FlattenItemHashes(byte[][] itemHashes)
    {
        byte[] flattened = GC.AllocateUninitializedArray<byte>(
            checked(itemHashes.Length * HashLength));
        for (int index = 0; index < itemHashes.Length; index++)
        {
            byte[] itemHash = itemHashes[index];
            if (itemHash.Length != HashLength)
            {
                throw new InvalidOperationException("A history item hash has an unexpected length.");
            }

            itemHash.CopyTo(flattened, index * HashLength);
        }

        return flattened;
    }

    private static byte[] ComputeArrayHistoryHash(byte[][] itemHashes)
        => ComputeArrayHistoryHash(itemHashes, itemHashes.Length);

    private static byte[] ComputeCacheNamespace(
        int generation,
        IReadOnlyList<string> triggerTokens)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("PinkUnicornProxy.HistoryRewriteCache"u8);
        Span<byte> integer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(integer, CachePolicyVersion);
        hash.AppendData(integer);
        BinaryPrimitives.WriteInt32BigEndian(integer, generation);
        hash.AppendData(integer);

        foreach (string token in triggerTokens.Order(StringComparer.Ordinal))
        {
            byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
            BinaryPrimitives.WriteInt32BigEndian(integer, tokenBytes.Length);
            hash.AppendData(integer);
            hash.AppendData(tokenBytes);
        }

        return hash.GetHashAndReset();
    }

    private static byte[] ComputeArrayHistoryHash(byte[][] itemHashes, int count)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int index = 0; index < count; index++)
        {
            hash.AppendData(itemHashes[index]);
        }

        return hash.GetHashAndReset();
    }

    private static byte[] CreateRevisionAnchorHash(
        HistoryCacheSnapshot snapshot,
        IReadOnlyList<int> triggerTurnIndices)
    {
        if (!snapshot.IsArray)
        {
            return snapshot.HistoryHash.ToArray();
        }

        if (triggerTurnIndices.Count == 0)
        {
            throw new InvalidOperationException(
                "An array revision cache entry requires at least one trigger turn.");
        }

        int anchorItemCount = checked(triggerTurnIndices.Max() + 1);
        if (anchorItemCount > snapshot.OriginalItemHashes.Length)
        {
            throw new InvalidOperationException(
                "A revision anchor extends beyond the supplied history snapshot.");
        }

        return ComputeArrayHistoryHash(snapshot.OriginalItemHashes, anchorItemCount);
    }

    private static string ResolveDatabasePath(string configuredPath, string? contentRootPath)
    {
        string path = Environment.ExpandEnvironmentVariables(configuredPath);
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(contentRootPath ?? AppContext.BaseDirectory, path);
        }

        return Path.GetFullPath(path);
    }

    private static void SetOwnerOnlyPermissions(string path, bool isDirectory)
    {
        if ((!OperatingSystem.IsLinux()
                && !OperatingSystem.IsMacOS()
                && !OperatingSystem.IsFreeBSD())
            || !File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (isDirectory)
        {
            mode |= UnixFileMode.UserExecute;
        }

        File.SetUnixFileMode(path, mode);
    }

    private static bool IsStorageException(Exception exception) => exception is
        SqliteException
        or IOException
        or UnauthorizedAccessException
        or NotSupportedException;

    private void MarkHealthy() => Volatile.Write(ref healthy, 1);

    private void MarkUnhealthy() => Volatile.Write(ref healthy, 0);

    private sealed class SemaphoreReleaser : IDisposable
    {
        private readonly SemaphoreSlim semaphore;
        private bool disposed;

        public SemaphoreReleaser(SemaphoreSlim semaphore)
        {
            this.semaphore = semaphore;
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                semaphore.Release();
            }
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private readonly record struct DatabaseIdentity(
        int ApplicationId,
        int SchemaVersion);

    private sealed record CacheCandidate(
        byte[] HistoryHash,
        long CreatedSequence,
        HistoryCacheMatch Match);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Error,
        Message = "Durable history rewrite cache maintenance failed; marked requests will fail closed until storage recovers.")]
    private static partial void LogMaintenanceFailure(
        ILogger logger,
        Exception exception);
}

internal sealed class HistoryRewriteCacheException : Exception
{
    public HistoryRewriteCacheException(string message)
        : base(message)
    {
    }

    public HistoryRewriteCacheException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record HistoryCacheSnapshot(
    ProviderRequestKind Provider,
    bool IsArray,
    byte[] HistoryHash,
    byte[][] OriginalItemHashes);

internal sealed record HistoryCacheMatch(
    byte[] RewrittenHistory,
    int EditCount,
    int OpaqueBlockCount,
    int PrefixItemCount,
    bool IsExact);

internal sealed record HistoryCacheStoreResult(
    HistoryCacheMatch Match,
    bool Inserted);
