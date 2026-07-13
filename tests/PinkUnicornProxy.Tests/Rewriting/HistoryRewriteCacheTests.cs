using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PinkUnicornProxy.Configuration;
using PinkUnicornProxy.Rewriting;

namespace PinkUnicornProxy.Tests.Rewriting;

public sealed class HistoryRewriteCacheTests
{
    [Fact]
    public async Task ExactRevisionSurvivesACompleteCacheRecreation()
    {
        using TestDatabase database = new();
        HistoryRewriteCacheOptions options = CreateOptions(database.Path);
        JsonArray history = ParseArray("""
            [{"role":"user","content":"!!NO!! It is TLS."}]
            """);
        HistoryCacheSnapshot snapshot = HistoryRewriteCache.CreateSnapshot(
            ProviderRequestKind.OpenAIChatCompletions,
            history);
        byte[] rewritten = Encoding.UTF8.GetBytes(
            "[{\"role\":\"user\",\"content\":\"It is TLS.\"}]");
        HistoryRewriteCache firstProcess = new(options);

        HistoryCacheStoreResult stored = await firstProcess.StoreAsync(
            snapshot,
            [0],
            rewritten,
            editCount: 1,
            opaqueBlockCount: 0,
            CancellationToken.None);
        firstProcess.Dispose();
        HistoryRewriteCache restartedProcess = new(options);
        HistoryCacheMatch? replay = await restartedProcess.GetLongestPrefixAsync(
            snapshot,
            [0],
            CancellationToken.None);

        Assert.True(stored.Inserted);
        Assert.NotNull(replay);
        Assert.True(replay.IsExact);
        Assert.Equal(rewritten, replay.RewrittenHistory);
        Assert.Equal(1, replay.EditCount);

        await using SqliteConnection verify = new($"Data Source={database.Path};Pooling=False");
        await verify.OpenAsync();
        await using SqliteCommand autoVacuum = verify.CreateCommand();
        autoVacuum.CommandText = "PRAGMA auto_vacuum;";
        Assert.Equal(2L, await autoVacuum.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ProviderAndGenerationArePartOfPersistentIdentity()
    {
        using TestDatabase database = new();
        HistoryRewriteCacheOptions options = CreateOptions(database.Path);
        JsonArray history = ParseArray("""
            [{"role":"user","content":"!!NO!! It is TLS."}]
            """);
        HistoryCacheSnapshot openAI = HistoryRewriteCache.CreateSnapshot(
            ProviderRequestKind.OpenAIChatCompletions,
            history);
        HistoryCacheSnapshot anthropic = HistoryRewriteCache.CreateSnapshot(
            ProviderRequestKind.AnthropicMessages,
            history);
        HistoryRewriteCache generationOne = new(options);
        await generationOne.StoreAsync(
            openAI,
            [0],
            Encoding.UTF8.GetBytes("[{\"role\":\"user\",\"content\":\"It is TLS.\"}]"),
            editCount: 1,
            opaqueBlockCount: 0,
            CancellationToken.None);

        HistoryRewriteCache generationTwo = new(CreateOptions(database.Path, generation: 2));
        HistoryRewriteCache differentTriggerPolicy = new(
            options,
            triggerTokens: ["!!REWRITE!!"]);
        byte[] generationTwoRewrite = Encoding.UTF8.GetBytes(
            "[{\"role\":\"user\",\"content\":\"Generation two.\"}]");
        await generationTwo.StoreAsync(
            openAI,
            [0],
            generationTwoRewrite,
            editCount: 1,
            opaqueBlockCount: 0,
            CancellationToken.None);

        Assert.Null(await generationOne.GetLongestPrefixAsync(
            anthropic,
            [0],
            CancellationToken.None));
        Assert.Equal(
            generationTwoRewrite,
            Assert.IsType<HistoryCacheMatch>(await generationTwo.GetLongestPrefixAsync(
                openAI,
                [0],
                CancellationToken.None)).RewrittenHistory);
        Assert.NotNull(await generationOne.GetLongestPrefixAsync(
            openAI,
            [0],
            CancellationToken.None));
        Assert.Null(await differentTriggerPolicy.GetLongestPrefixAsync(
            openAI,
            [0],
            CancellationToken.None));
    }

    [Fact]
    public async Task ScalarHistoriesUseExactMatchingOnly()
    {
        using TestDatabase database = new();
        HistoryRewriteCache cache = new(CreateOptions(database.Path));
        HistoryCacheSnapshot original = HistoryRewriteCache.CreateSnapshot(
            ProviderRequestKind.OpenAIResponses,
            JsonValue.Create("!!NO!! The certificate expired."));
        byte[] rewritten = Encoding.UTF8.GetBytes("\"The certificate expired.\"");
        await cache.StoreAsync(original, [0], rewritten, 1, 0, CancellationToken.None);
        HistoryCacheSnapshot different = HistoryRewriteCache.CreateSnapshot(
            ProviderRequestKind.OpenAIResponses,
            JsonValue.Create("!!NO!! The certificate expired. Continue."));

        HistoryCacheMatch? exact = await cache.GetLongestPrefixAsync(
            original,
            [0],
            CancellationToken.None);

        Assert.Equal(rewritten, Assert.IsType<HistoryCacheMatch>(exact).RewrittenHistory);
        Assert.Null(await cache.GetLongestPrefixAsync(different, [0], CancellationToken.None));
    }

    [Fact]
    public async Task LongestOriginalPrefixIsReturnedButANewMarkerForcesARebase()
    {
        using TestDatabase database = new();
        HistoryRewriteCache cache = new(CreateOptions(database.Path));
        JsonArray root = ParseArray("""
            [
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]
            """);
        HistoryCacheSnapshot rootSnapshot = HistoryRewriteCache.CreateSnapshot(
            ProviderRequestKind.OpenAIChatCompletions,
            root);
        byte[] rewrittenRoot = Encoding.UTF8.GetBytes(
            "[{\"role\":\"assistant\",\"content\":\"It is TLS.\"},{\"role\":\"user\",\"content\":\"It is TLS.\"}]");
        await cache.StoreAsync(
            rootSnapshot,
            [1],
            rewrittenRoot,
            editCount: 2,
            opaqueBlockCount: 0,
            CancellationToken.None);
        JsonArray continuation = ParseArray("""
            [
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."},
              {"role":"assistant","content":"Continue."}
            ]
            """);
        HistoryCacheSnapshot continuationSnapshot = HistoryRewriteCache.CreateSnapshot(
            ProviderRequestKind.OpenAIChatCompletions,
            continuation);
        byte[] rewrittenContinuation = Encoding.UTF8.GetBytes(
            "[{\"role\":\"assistant\",\"content\":\"It is TLS.\"},{\"role\":\"user\",\"content\":\"It is TLS.\"},{\"role\":\"assistant\",\"content\":\"Continue.\"}]");
        await cache.StoreAsync(
            continuationSnapshot,
            [1],
            rewrittenContinuation,
            editCount: 2,
            opaqueBlockCount: 0,
            CancellationToken.None);
        JsonArray descendant = ParseArray("""
            [
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."},
              {"role":"assistant","content":"Continue."},
              {"role":"user","content":"Next."}
            ]
            """);
        HistoryCacheSnapshot descendantSnapshot = HistoryRewriteCache.CreateSnapshot(
            ProviderRequestKind.OpenAIChatCompletions,
            descendant);

        HistoryCacheMatch? prefix = await cache.GetLongestPrefixAsync(
            descendantSnapshot,
            [1],
            CancellationToken.None);
        HistoryCacheMatch? rejectedByNewMarker = await cache.GetLongestPrefixAsync(
            descendantSnapshot,
            [1, 3],
            CancellationToken.None);

        Assert.NotNull(prefix);
        Assert.False(prefix.IsExact);
        Assert.Equal(3, prefix.PrefixItemCount);
        Assert.Equal(rewrittenContinuation, prefix.RewrittenHistory);
        Assert.Null(rejectedByNewMarker);
    }

    [Fact]
    public async Task SuccessfulLookupRenewsSlidingTtlAcrossInstances()
    {
        using TestDatabase database = new();
        ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));
        HistoryRewriteCacheOptions options = CreateOptions(database.Path);
        options.TimeToLive = TimeSpan.FromSeconds(1);
        HistoryRewriteCache writer = new(options, time);
        JsonArray history = ParseArray("""
            [{"role":"user","content":"!!NO!! It is TLS."}]
            """);
        HistoryCacheSnapshot snapshot = HistoryRewriteCache.CreateSnapshot(
            ProviderRequestKind.OpenAIChatCompletions,
            history);
        await writer.StoreAsync(
            snapshot,
            [0],
            Encoding.UTF8.GetBytes("[{\"role\":\"user\",\"content\":\"It is TLS.\"}]"),
            editCount: 1,
            opaqueBlockCount: 0,
            CancellationToken.None);

        time.Advance(TimeSpan.FromMilliseconds(750));
        HistoryRewriteCache reader = new(options, time);
        Assert.NotNull(await reader.GetLongestPrefixAsync(snapshot, [0], CancellationToken.None));
        time.Advance(TimeSpan.FromMilliseconds(750));
        await writer.PurgeExpiredAsync();
        Assert.NotNull(await writer.GetLongestPrefixAsync(snapshot, [0], CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(1));
        await writer.PurgeExpiredAsync();

        Assert.Null(await writer.GetLongestPrefixAsync(snapshot, [0], CancellationToken.None));
        await using SqliteConnection verify = new($"Data Source={database.Path};Pooling=False");
        await verify.OpenAsync();
        await using SqliteCommand count = verify.CreateCommand();
        count.CommandText = "SELECT count(*) FROM history_rewrite_entries;";
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LogicalSizeLimitEvictsLeastRecentlyUsedRevision()
    {
        using TestDatabase database = new();
        HistoryRewriteCacheOptions options = CreateOptions(database.Path);
        options.MaximumBytes = 1_100;
        ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));
        HistoryRewriteCache cache = new(options, time);
        HistoryCacheSnapshot first = CreateSingleItemSnapshot("!!NO!! first");
        HistoryCacheSnapshot second = CreateSingleItemSnapshot("!!NO!! second");
        HistoryCacheSnapshot third = CreateSingleItemSnapshot("!!NO!! third");
        byte[] rewrite = Encoding.UTF8.GetBytes(
            "[{\"role\":\"user\",\"content\":\"A compact authoritative rewrite.\"}]");
        await cache.StoreAsync(first, [0], rewrite, 1, 0, CancellationToken.None);
        await cache.StoreAsync(second, [0], rewrite, 1, 0, CancellationToken.None);
        HistoryRewriteCache secondProcess = new(options, time);
        Assert.NotNull(await secondProcess.GetLongestPrefixAsync(first, [0], CancellationToken.None));

        HistoryRewriteCache thirdProcess = new(options, time);
        await thirdProcess.StoreAsync(third, [0], rewrite, 1, 0, CancellationToken.None);

        Assert.NotNull(await cache.GetLongestPrefixAsync(first, [0], CancellationToken.None));
        Assert.Null(await cache.GetLongestPrefixAsync(second, [0], CancellationToken.None));
        Assert.NotNull(await cache.GetLongestPrefixAsync(third, [0], CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentProcessesPublishAndReturnOneAuthoritativeRevision()
    {
        using TestDatabase database = new();
        HistoryRewriteCacheOptions options = CreateOptions(database.Path);
        HistoryRewriteCache firstProcess = new(options);
        HistoryRewriteCache secondProcess = new(options);
        HistoryCacheSnapshot snapshot = CreateSingleItemSnapshot("!!NO!! original");
        byte[] firstRewrite = Encoding.UTF8.GetBytes(
            "[{\"role\":\"user\",\"content\":\"first version\"}]");
        byte[] secondRewrite = Encoding.UTF8.GetBytes(
            "[{\"role\":\"user\",\"content\":\"second version\"}]");

        HistoryCacheStoreResult[] results = await Task.WhenAll(
            firstProcess.StoreAsync(snapshot, [0], firstRewrite, 1, 0, CancellationToken.None),
            secondProcess.StoreAsync(snapshot, [0], secondRewrite, 2, 3, CancellationToken.None));

        HistoryCacheStoreResult winner = Assert.Single(results, result => result.Inserted);
        byte[] expectedWinner = results[0].Inserted ? firstRewrite : secondRewrite;
        Assert.Equal(results[0].Match.RewrittenHistory, results[1].Match.RewrittenHistory);
        Assert.Equal(expectedWinner, winner.Match.RewrittenHistory);
        Assert.Equal(results[0].Inserted ? 1 : 2, winner.Match.EditCount);
        Assert.Equal(results[0].Inserted ? 0 : 3, winner.Match.OpaqueBlockCount);
        HistoryCacheMatch? replay = await firstProcess.GetLongestPrefixAsync(
            snapshot,
            [0],
            CancellationToken.None);
        Assert.Equal(results[0].Match.RewrittenHistory, Assert.IsType<HistoryCacheMatch>(replay).RewrittenHistory);
    }

    [Fact]
    public async Task ReturnedBytesAreDetachedFromPersistentStorage()
    {
        using TestDatabase database = new();
        HistoryRewriteCache cache = new(CreateOptions(database.Path));
        HistoryCacheSnapshot snapshot = CreateSingleItemSnapshot("!!NO!! original");
        byte[] rewritten = Encoding.UTF8.GetBytes(
            "[{\"role\":\"user\",\"content\":\"authoritative\"}]");
        await cache.StoreAsync(snapshot, [0], rewritten, 1, 0, CancellationToken.None);
        HistoryCacheMatch first = Assert.IsType<HistoryCacheMatch>(
            await cache.GetLongestPrefixAsync(snapshot, [0], CancellationToken.None));

        first.RewrittenHistory[0] = (byte)'!';
        HistoryCacheMatch second = Assert.IsType<HistoryCacheMatch>(
            await cache.GetLongestPrefixAsync(snapshot, [0], CancellationToken.None));

        Assert.Equal(rewritten, second.RewrittenHistory);
    }

    [Fact]
    public async Task CorruptDatabaseIsRejectedInsteadOfTreatedAsAColdCache()
    {
        using TestDatabase database = new();
        await File.WriteAllTextAsync(database.Path, "not a sqlite database");
        HistoryRewriteCache cache = new(CreateOptions(database.Path));
        HistoryCacheSnapshot snapshot = CreateSingleItemSnapshot("!!NO!! original");

        await Assert.ThrowsAsync<HistoryRewriteCacheException>(() =>
            cache.GetLongestPrefixAsync(snapshot, [0], CancellationToken.None));
    }

    [Fact]
    public async Task NonRewriteModeDoesNotCreateOrOpenTheConfiguredDatabase()
    {
        using TestDatabase database = new();
        HistoryRewriteCache cache = new(
            CreateOptions(database.Path),
            rewriteModeEnabled: false);

        await cache.StartAsync(CancellationToken.None);
        await cache.StopAsync(CancellationToken.None);
        cache.Dispose();

        Assert.False(File.Exists(database.Path));
        Assert.True(cache.IsHealthy);
    }

    [Fact]
    public async Task UnrelatedSqliteDatabaseIsRejectedWithoutChangingItsJournalMode()
    {
        using TestDatabase database = new();
        await using (SqliteConnection setup = new($"Data Source={database.Path};Pooling=False"))
        {
            await setup.OpenAsync();
            await using SqliteCommand create = setup.CreateCommand();
            create.CommandText = "CREATE TABLE unrelated (value TEXT); PRAGMA journal_mode = DELETE;";
            await create.ExecuteNonQueryAsync();
        }

        HistoryRewriteCache cache = new(CreateOptions(database.Path));
        HistoryCacheSnapshot snapshot = CreateSingleItemSnapshot("!!NO!! original");
        await Assert.ThrowsAsync<HistoryRewriteCacheException>(() =>
            cache.GetLongestPrefixAsync(snapshot, [0], CancellationToken.None));

        await using SqliteConnection verify = new($"Data Source={database.Path};Pooling=False");
        await verify.OpenAsync();
        await using SqliteCommand journalMode = verify.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("delete", await journalMode.ExecuteScalarAsync());
    }

    private static HistoryRewriteCacheOptions CreateOptions(string path, int generation = 1) => new()
    {
        DatabasePath = path,
        Generation = generation,
        MaximumBytes = 128 * 1024 * 1024,
        TimeToLive = TimeSpan.FromDays(30),
        CleanupInterval = TimeSpan.FromMinutes(15),
        BusyTimeout = TimeSpan.FromSeconds(10),
    };

    private static HistoryCacheSnapshot CreateSingleItemSnapshot(string content)
    {
        JsonArray history = new()
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = content,
            },
        };
        return HistoryRewriteCache.CreateSnapshot(
            ProviderRequestKind.OpenAIChatCompletions,
            history);
    }

    private static JsonArray ParseArray(string json) =>
        Assert.IsType<JsonArray>(JsonNode.Parse(json));

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "PinkUnicornProxy.Tests",
            Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "cache.db");
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            utcNow += duration;
        }
    }
}
