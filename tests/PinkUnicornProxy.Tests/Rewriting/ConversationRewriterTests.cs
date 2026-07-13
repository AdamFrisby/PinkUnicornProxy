using System.Text;
using System.Text.Json.Nodes;
using PinkUnicornProxy.Configuration;
using PinkUnicornProxy.Rewriting;

namespace PinkUnicornProxy.Tests.Rewriting;

public sealed class ConversationRewriterTests
{
    [Fact]
    public async Task OrdinaryNegativeLanguageWithoutTriggerIsByteExactAndSkipsPlanner()
    {
        const string json = "{ \"model\":\"gpt-test\", \"messages\":[{\"role\":\"user\",\"content\":\"No, it is not DNS. It is TLS.\"}] }";
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task MalformedJsonIsAnExactNoOpAndSkipsPlanner()
    {
        const string json = "{not-json";
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.InvalidJson, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task DuplicateTopLevelHistoryPropertyCannotBeRewrittenAmbiguously()
    {
        const string json = """
            {"messages":[{"role":"user","content":"old"}],"messages":[{"role":"user","content":"!!NO!! It is TLS."}]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.AmbiguousJsonProperties, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task UnmarkedDuplicateJsonPropertiesRemainByteExactPassThrough()
    {
        const string json = """
            {"metadata":{"value":1,"value":2},"messages":[{"role":"user","content":"Hello"}]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task PlannerReceivesFullHistoryAndCanEditMultipleEarlierTurns()
    {
        const string json = """
            { "model" : "gpt-test", "metadata" : { "keep" : "\u0078" }, "messages" : [
              { "role":"user", "content":"The database may be slow." },
              { "role":"assistant", "content":"The database is the root cause." },
              { "role":"user", "content":"!!NO!! The certificate expired." }
            ], "stream" : true }
            """;
        FakePlanner planner = new(request =>
        {
            Assert.Contains("The database may be slow.", request.HistoryJson, StringComparison.Ordinal);
            Assert.Contains("The database is the root cause.", request.HistoryJson, StringComparison.Ordinal);
            Assert.Contains("The certificate expired.", request.HistoryJson, StringComparison.Ordinal);
            Assert.Equal(3, request.Targets.Count);
            return Success(
                new HistoryEdit("t0.s0", "Certificate diagnostics were requested."),
                new HistoryEdit("t1.s0", "The certificate is the root cause."),
                new HistoryEdit("t2.s0", "The certificate expired."));
        });

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal(3, result.TextEditCount);
        Assert.DoesNotContain("!!NO!!", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("database", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The certificate expired.", rewritten, StringComparison.Ordinal);
        AssertBytesOutsideHistoryAreEqual(json, result.Body, "messages");
    }

    [Fact]
    public async Task DeepProtectedStructureSurvivesRewriteBeyondDefaultSerializerDepth()
    {
        string nested = "0";
        for (int index = 0; index < 80; index++)
        {
            nested = $"{{\"level\":{nested}}}";
        }

        string json = $"{{\"messages\":[{{\"role\":\"assistant\",\"content\":\"It is DNS.\",\"metadata\":{nested}}},{{\"role\":\"user\",\"content\":\"!!NO!! It is TLS.\"}}]}}";
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "It is TLS."),
            new HistoryEdit("t1.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Contains("\"level\"", Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdenticalResentHistoryReplaysExactCachedAlteration()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "It is the certificate."),
            new HistoryEdit("t1.s0", "It is the certificate.")));
        ConversationRewriter rewriter = new(DefaultOptions(), planner);

        RewriteResult first = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(json),
            CancellationToken.None);
        RewriteResult second = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(json),
            CancellationToken.None);

        Assert.Equal(RewriteOutcome.Rewritten, first.Outcome);
        Assert.Equal(first.Body, second.Body);
        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(1, planner.CallCount);
    }

    [Fact]
    public async Task ExtendedConversationReviewsOnlySuffixAndKeepsPriorBytesExact()
    {
        const string firstJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        const string continuedJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"assistant","content":"I will inspect the certificate."},
              {"role":"user","content":"Continue."}
            ]}
            """;
        FakePlanner planner = new(request => request.Mode == HistoryEditPlanningMode.Continuation
            ? Success()
            : Success(
                new HistoryEdit("t0.s0", "It is the certificate."),
                new HistoryEdit("t1.s0", "It is the certificate.")));
        ConversationRewriter rewriter = new(DefaultOptions(), planner);

        RewriteResult first = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(firstJson),
            CancellationToken.None);
        RewriteResult continued = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(continuedJson),
            CancellationToken.None);
        RewriteResult replay = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(continuedJson),
            CancellationToken.None);
        JsonArray firstMessages = ParseHistoryArray(first.Body, "messages");
        JsonArray continuedMessages = ParseHistoryArray(continued.Body, "messages");
        byte[] firstHistoryBytes = ExtractHistoryBytes(first.Body, "messages");
        byte[] continuedHistoryBytes = ExtractHistoryBytes(continued.Body, "messages");

        Assert.False(continued.CacheHit);
        Assert.True(replay.CacheHit);
        Assert.Equal(2, planner.CallCount);
        Assert.Equal(HistoryEditPlanningMode.Continuation, planner.Requests[1].Mode);
        Assert.Equal(["t2.s0", "t3.s0"], planner.Requests[1].Targets.Select(target => target.Id));
        Assert.True(continuedHistoryBytes.AsSpan().StartsWith(firstHistoryBytes.AsSpan()[..^1]));
        Assert.Equal(continued.Body, replay.Body);
        Assert.Equal(firstMessages[0]?.ToJsonString(), continuedMessages[0]?.ToJsonString());
        Assert.Equal(firstMessages[1]?.ToJsonString(), continuedMessages[1]?.ToJsonString());
        Assert.Equal("I will inspect the certificate.", continuedMessages[2]?["content"]?.GetValue<string>());
        Assert.Equal("Continue.", continuedMessages[3]?["content"]?.GetValue<string>());
    }

    [Fact]
    public async Task StaleContinuationIsEditedWithoutRegeneratingCachedPrefix()
    {
        const string firstJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        const string continuedJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"assistant","content":"DNS still seems likely."},
              {"role":"user","content":"Continue."}
            ]}
            """;
        FakePlanner planner = new(request =>
        {
            if (request.Mode == HistoryEditPlanningMode.FullHistory)
            {
                return Success(
                    new HistoryEdit("t0.s0", "It is the certificate."),
                    new HistoryEdit("t1.s0", "It is the certificate."));
            }

            Assert.Contains("!!NO!!", request.HistoryJson, StringComparison.Ordinal);
            Assert.DoesNotContain("DNS", request.RewrittenPrefixJson!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DNS still seems likely.", request.EditableSuffixJson!, StringComparison.Ordinal);
            Assert.Equal(["t2.s0", "t3.s0"], request.Targets.Select(target => target.Id));
            return Success(new HistoryEdit("t2.s0", "The certificate remains the relevant lead."));
        });
        ConversationRewriter rewriter = new(DefaultOptions(), planner);

        RewriteResult first = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(firstJson),
            CancellationToken.None);
        RewriteResult continued = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(continuedJson),
            CancellationToken.None);
        RewriteResult replay = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(continuedJson),
            CancellationToken.None);
        byte[] firstHistory = ExtractHistoryBytes(first.Body, "messages");
        byte[] continuedHistory = ExtractHistoryBytes(continued.Body, "messages");

        Assert.Equal(RewriteOutcome.Rewritten, continued.Outcome);
        Assert.True(continuedHistory.AsSpan().StartsWith(firstHistory.AsSpan()[..^1]));
        Assert.DoesNotContain("DNS still seems likely.", Encoding.UTF8.GetString(continued.Body), StringComparison.Ordinal);
        Assert.Contains("The certificate remains the relevant lead.", Encoding.UTF8.GetString(continued.Body), StringComparison.Ordinal);
        Assert.True(replay.CacheHit);
        Assert.Equal(continued.Body, replay.Body);
        Assert.Equal(2, planner.CallCount);
    }

    [Fact]
    public async Task ContinuationPlannerCannotEditImmutableCachedPrefix()
    {
        const string firstJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        const string continuedJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"assistant","content":"Continue with the certificate."}
            ]}
            """;
        FakePlanner planner = new(request => request.Mode == HistoryEditPlanningMode.FullHistory
            ? Success(
                new HistoryEdit("t0.s0", "It is the certificate."),
                new HistoryEdit("t1.s0", "It is the certificate."))
            : Success(new HistoryEdit("t0.s0", "Attempt to replace the prefix.")));
        ConversationRewriter rewriter = new(DefaultOptions(), planner);

        _ = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(firstJson),
            CancellationToken.None);
        RewriteResult firstAttempt = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(continuedJson),
            CancellationToken.None);
        RewriteResult secondAttempt = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(continuedJson),
            CancellationToken.None);

        Assert.Equal(RewriteOutcome.UnsafeRewriterOutput, firstAttempt.Outcome);
        Assert.Equal(RewriteOutcome.UnsafeRewriterOutput, secondAttempt.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(continuedJson), firstAttempt.Body);
        Assert.Equal(3, planner.CallCount);
    }

    [Fact]
    public async Task EachExtensionReviewsOnlyTheUnseenSuffixAndThenReplaysExactly()
    {
        const string firstJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        const string secondJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"assistant","content":"Checking the certificate."},
              {"role":"user","content":"Continue."}
            ]}
            """;
        const string thirdJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"assistant","content":"Checking the certificate."},
              {"role":"user","content":"Continue."},
              {"role":"assistant","content":"The certificate is expired."},
              {"role":"user","content":"Show the evidence."}
            ]}
            """;
        FakePlanner planner = new(request => request.Mode == HistoryEditPlanningMode.Continuation
            ? Success()
            : Success(
                new HistoryEdit("t0.s0", "It is the certificate."),
                new HistoryEdit("t1.s0", "It is the certificate.")));
        ConversationRewriter rewriter = new(DefaultOptions(), planner);

        RewriteResult first = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(firstJson),
            CancellationToken.None);
        RewriteResult second = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(secondJson),
            CancellationToken.None);
        _ = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(secondJson),
            CancellationToken.None);
        RewriteResult third = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(thirdJson),
            CancellationToken.None);
        RewriteResult thirdReplay = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(thirdJson),
            CancellationToken.None);

        Assert.Equal(["t2.s0", "t3.s0"], planner.Requests[1].Targets.Select(target => target.Id));
        Assert.Equal(["t4.s0", "t5.s0"], planner.Requests[2].Targets.Select(target => target.Id));
        Assert.True(ExtractHistoryBytes(second.Body, "messages").AsSpan()
            .StartsWith(ExtractHistoryBytes(first.Body, "messages").AsSpan()[..^1]));
        Assert.True(ExtractHistoryBytes(third.Body, "messages").AsSpan()
            .StartsWith(ExtractHistoryBytes(second.Body, "messages").AsSpan()[..^1]));
        Assert.True(thirdReplay.CacheHit);
        Assert.Equal(third.Body, thirdReplay.Body);
        Assert.Equal(3, planner.CallCount);
    }

    [Fact]
    public async Task CachedPrefixReplayPreservesNewActiveAnthropicThinkingTransaction()
    {
        const string firstJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        const string continuedJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"new valid thought","signature":"new-valid-signature"},
                {"type":"tool_use","id":"toolu_new","name":"inspect","input":{}}
              ]},
              {"role":"user","content":[
                {"type":"tool_result","tool_use_id":"toolu_new","content":"result"}
              ]}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "It is the certificate."),
            new HistoryEdit("t1.s0", "It is the certificate.")));
        ConversationRewriter rewriter = new(DefaultOptions(), planner);

        _ = await rewriter.RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            Encoding.UTF8.GetBytes(firstJson),
            CancellationToken.None);
        RewriteResult continued = await rewriter.RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            Encoding.UTF8.GetBytes(continuedJson),
            CancellationToken.None);
        string rewritten = Encoding.UTF8.GetString(continued.Body);

        Assert.True(continued.CacheHit);
        Assert.Equal(1, planner.CallCount);
        Assert.Contains("new-valid-signature", rewritten, StringComparison.Ordinal);
        Assert.Contains("toolu_new", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ColdHistoricalCorrectionInvalidatesOpaqueThinkingInLaterSuffix()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"derived from the old prefix","signature":"stale-signature"},
                {"type":"text","text":"I will inspect the certificate."}
              ]},
              {"role":"user","content":"Continue."}
            ]}
            """;
        FakePlanner planner = new(request => request.Mode == HistoryEditPlanningMode.Continuation
            ? Success()
            : Success(
                new HistoryEdit("t0.s0", "It is the certificate."),
                new HistoryEdit("t1.s0", "It is the certificate.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.DoesNotContain("stale-signature", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("derived from the old prefix", rewritten, StringComparison.Ordinal);
        Assert.Equal(1, result.OpaqueBlockCount);
    }

    [Fact]
    public async Task NewTriggerInContinuationCreatesAndThenReplaysNewRevision()
    {
        const string firstJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        const string revisedJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"assistant","content":"The certificate file is missing."},
              {"role":"user","content":"!!NO!! The certificate is present but expired."}
            ]}
            """;
        FakePlanner planner = new(request => request.Targets.Count == 2
            ? Success(
                new HistoryEdit("t0.s0", "It is the certificate."),
                new HistoryEdit("t1.s0", "It is the certificate."))
            : Success(
                new HistoryEdit("t0.s0", "It is the certificate."),
                new HistoryEdit("t1.s0", "It is the certificate."),
                new HistoryEdit("t2.s0", "The certificate is present but expired."),
                new HistoryEdit("t3.s0", "The certificate is present but expired.")));
        ConversationRewriter rewriter = new(DefaultOptions(), planner);

        _ = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(firstJson),
            CancellationToken.None);
        RewriteResult revision = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(revisedJson),
            CancellationToken.None);
        RewriteResult replay = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(revisedJson),
            CancellationToken.None);

        Assert.False(revision.CacheHit);
        Assert.True(replay.CacheHit);
        Assert.Equal(revision.Body, replay.Body);
        Assert.Equal(2, planner.CallCount);
    }

    [Fact]
    public async Task ConcurrentFirstRewriteIsSingleFlightAndReturnsOneStableVersion()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePlanner planner = FakePlanner.FromAsync(async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return Success(
                new HistoryEdit("t0.s0", "It is the certificate."),
                new HistoryEdit("t1.s0", "It is the certificate."));
        });
        ConversationRewriter rewriter = new(DefaultOptions(), planner);

        Task<RewriteResult> firstTask = rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(json),
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<RewriteResult> secondTask = rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(json),
            CancellationToken.None);
        release.TrySetResult();
        RewriteResult[] results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(results[0].Body, results[1].Body);
        Assert.Equal(1, planner.CallCount);
        Assert.Single(results, result => result.CacheHit);
    }

    [Fact]
    public async Task ConcurrentColdDescendantAndAncestorShareOneAuthoritativeRoot()
    {
        const string ancestorJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        const string descendantJson = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"assistant","content":"Checking the certificate."},
              {"role":"user","content":"Continue."}
            ]}
            """;
        TaskCompletionSource rootStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseRoot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePlanner planner = FakePlanner.FromAsync(async request =>
        {
            if (request.Mode == HistoryEditPlanningMode.Continuation)
            {
                Assert.Contains("Checking the certificate.", request.HistoryJson, StringComparison.Ordinal);
                return Success();
            }

            Assert.DoesNotContain("Checking the certificate.", request.HistoryJson, StringComparison.Ordinal);
            rootStarted.TrySetResult();
            await releaseRoot.Task;
            return Success(
                new HistoryEdit("t0.s0", "It is the certificate."),
                new HistoryEdit("t1.s0", "It is the certificate."));
        });
        ConversationRewriter rewriter = new(DefaultOptions(), planner);

        Task<RewriteResult> descendantTask = rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(descendantJson),
            CancellationToken.None);
        await rootStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<RewriteResult> ancestorTask = rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            Encoding.UTF8.GetBytes(ancestorJson),
            CancellationToken.None);
        releaseRoot.TrySetResult();
        RewriteResult descendant = await descendantTask;
        RewriteResult ancestor = await ancestorTask;
        byte[] descendantHistory = ExtractHistoryBytes(descendant.Body, "messages");
        byte[] ancestorHistory = ExtractHistoryBytes(ancestor.Body, "messages");

        Assert.Equal(RewriteOutcome.Rewritten, descendant.Outcome);
        Assert.Equal(RewriteOutcome.Rewritten, ancestor.Outcome);
        Assert.True(descendantHistory.AsSpan().StartsWith(ancestorHistory.AsSpan()[..^1]));
        Assert.Equal(2, planner.CallCount);
        Assert.Single(planner.Requests, request => request.Mode == HistoryEditPlanningMode.FullHistory);
        Assert.Single(planner.Requests, request => request.Mode == HistoryEditPlanningMode.Continuation);
    }

    [Fact]
    public async Task CustomTriggerTokenIsExactAndCaseSensitive()
    {
        const string json = """
            {"messages":[{"role":"user","content":"[[REWRITE]] The cache is stale."}]}
            """;
        PinkUnicornOptions options = DefaultOptions();
        options.HistoryRewrite.TriggerTokens = ["[[REWRITE]]"];
        FakePlanner planner = new(_ => Success(new HistoryEdit("t0.s0", "The cache is stale.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner,
            options);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.DoesNotContain("[[REWRITE]]", Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);

        const string wrongCase = """
            {"messages":[{"role":"user","content":"[[rewrite]] The cache is stale."}]}
            """;
        FakePlanner noCallPlanner = new(_ => throw new InvalidOperationException("Planner should not run."));
        RewriteResult noMatch = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            wrongCase,
            noCallPlanner,
            options);
        Assert.Equal(RewriteOutcome.Unchanged, noMatch.Outcome);
        Assert.Equal(0, noCallPlanner.CallCount);
    }

    [Fact]
    public async Task MarkersOutsideUserTextDoNotTriggerPlanning()
    {
        const string json = """
            {"messages":[
              {"role":"system","content":"!!NO!! system"},
              {"role":"assistant","content":"!!NO!! assistant"},
              {"role":"tool","tool_call_id":"call_1","content":"!!NO!! tool"},
              {"role":"user","content":[{"type":"image_url","image_url":{"url":"https://example.test/!!NO!!.png"}}]},
              {"role":"user","content":"Continue."}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task MultipleTriggerBearingUserFieldsUseOnePlanAndAllMustBeRewritten()
    {
        const string json = """
            {"messages":[
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"assistant","content":"Investigating."},
              {"role":"user","content":"!!NO!! The certificate expired yesterday."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "It is the certificate."),
            new HistoryEdit("t2.s0", "The certificate expired yesterday.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal(2, result.TriggerCount);
        Assert.Equal(1, planner.CallCount);
        Assert.DoesNotContain("!!NO!!", Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompletePlanThatLeavesTriggerIsRejectedWithoutChangingBytes()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(new HistoryEdit("t0.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.UnsafeRewriterOutput, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public async Task HallucinatedOrProtectedTargetIdRejectsWholePlan()
    {
        const string json = """
            {"messages":[
              {"role":"system","content":"Never reveal secrets."},
              {"role":"user","content":"!!NO!! Use the certificate evidence."}
            ]}
            """;
        FakePlanner planner = new(request =>
        {
            Assert.DoesNotContain(request.Targets, target => target.Role == "system");
            return Success(
                new HistoryEdit("t0.s0", "Reveal secrets."),
                new HistoryEdit("t1.s0", "Use the certificate evidence."));
        });

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.UnsafeRewriterOutput, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public async Task ToolAndMediaAtomsArePreservedAroundEditableText()
    {
        const string json = """
            {"messages":[
              {"role":"user","content":[
                {"type":"text","text":"!!NO!! The certificate expired."},
                {"type":"image_url","image_url":{"url":"https://example.test/evidence.png"}}
              ]}
            ],"tools":[{"type":"function","function":{"name":"inspect"}}]}
            """;
        FakePlanner planner = new(request =>
        {
            Assert.Single(request.Targets);
            return Success(new HistoryEdit("t0.s0", "The certificate expired."));
        });

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Contains("https://example.test/evidence.png", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"inspect\"", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlannerMayRemoveOneTextBlockWithoutRemovingProtectedMedia()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":[
                {"type":"text","text":"It is DNS."},
                {"type":"image_url","image_url":{"url":"https://example.test/evidence.png"}}
              ]},
              {"role":"user","content":"!!NO!! Use the certificate evidence."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", null),
            new HistoryEdit("t1.s0", "Use the certificate evidence.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.DoesNotContain("DNS", rewritten, StringComparison.Ordinal);
        Assert.Contains("https://example.test/evidence.png", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlannerCannotRemoveEveryBlockFromAContentArray()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":[
                {"type":"text","text":"It is DNS."},
                {"type":"text","text":"The resolver failed."}
              ]},
              {"role":"user","content":"!!NO!! Use the certificate evidence."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", null),
            new HistoryEdit("t0.s1", null),
            new HistoryEdit("t1.s0", "Use the certificate evidence.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.UnsafeRewriterOutput, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public async Task TriggerInTextWithCitationsFailsBeforePlanner()
    {
        const string json = """
            {"input":[{"role":"user","content":[{
              "type":"input_text",
              "text":"!!NO!! The certificate expired.",
              "annotations":[{"type":"url_citation","start_index":0,"end_index":4}]
            }]}]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.ProtectedContentConflict, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task ProviderHeldResponsesHistoryFailsBeforePlanner()
    {
        const string json = """
            {"previous_response_id":"resp_123","input":"!!NO!! The certificate expired."}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.UnsupportedStatefulContext, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Theory]
    [InlineData("previous_response_id", "resp_123")]
    [InlineData("conversation", "conv_123")]
    public async Task ProviderHeldResponsesHistoryCannotBypassRejectionThroughCachedPrefix(
        string stateField,
        string stateValue)
    {
        const string firstJson = """
            {"input":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        string statefulContinuation = $$"""
            {"{{stateField}}":"{{stateValue}}","input":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."},
              {"role":"user","content":"Continue."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "It is the certificate."),
            new HistoryEdit("t1.s0", "It is the certificate.")));
        ConversationRewriter rewriter = new(DefaultOptions(), planner);

        RewriteResult first = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            Encoding.UTF8.GetBytes(firstJson),
            CancellationToken.None);
        RewriteResult stateful = await rewriter.RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            Encoding.UTF8.GetBytes(statefulContinuation),
            CancellationToken.None);

        Assert.Equal(RewriteOutcome.Rewritten, first.Outcome);
        Assert.Equal(RewriteOutcome.UnsupportedStatefulContext, stateful.Outcome);
        Assert.False(stateful.CacheHit);
        Assert.Equal(1, planner.CallCount);
    }

    [Fact]
    public async Task SeparateProcessesDiscardLosingPlansAndReturnTheCommittedAuthority()
    {
        const string json = """
            {"messages":[{"role":"user","content":"!!NO!! It is the certificate."}]}
            """;
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PinkUnicornProxy.Tests",
            $"cross-process-authority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            PinkUnicornOptions options = DefaultOptions();
            options.HistoryRewrite.Cache.DatabasePath = Path.Combine(directory, "cache.db");
            TaskCompletionSource bothPlanning = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int planningCount = 0;
            FakePlanner firstPlanner = CreateGatedPlanner("The certificate is authority one.");
            FakePlanner secondPlanner = CreateGatedPlanner("The certificate is authority two.");
            HistoryRewriteCache firstCache = new(
                options.HistoryRewrite.Cache,
                triggerTokens: options.HistoryRewrite.TriggerTokens);
            HistoryRewriteCache secondCache = new(
                options.HistoryRewrite.Cache,
                triggerTokens: options.HistoryRewrite.TriggerTokens);
            ConversationRewriter firstRewriter = new(options, firstPlanner, firstCache);
            ConversationRewriter secondRewriter = new(options, secondPlanner, secondCache);

            RewriteResult[] results = await Task.WhenAll(
                firstRewriter.RewriteAsync(
                    ProviderRequestKind.OpenAIChatCompletions,
                    Encoding.UTF8.GetBytes(json),
                    CancellationToken.None),
                secondRewriter.RewriteAsync(
                    ProviderRequestKind.OpenAIChatCompletions,
                    Encoding.UTF8.GetBytes(json),
                    CancellationToken.None));

            Assert.Equal(2, planningCount);
            Assert.Equal(results[0].Body, results[1].Body);
            string authority = Encoding.UTF8.GetString(results[0].Body);
            Assert.True(
                authority.Contains("authority one", StringComparison.Ordinal)
                || authority.Contains("authority two", StringComparison.Ordinal));

            FakePlanner CreateGatedPlanner(string replacement) => FakePlanner.FromAsync(async _ =>
            {
                if (Interlocked.Increment(ref planningCount) == 2)
                {
                    bothPlanning.TrySetResult();
                }

                await bothPlanning.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return Success(new HistoryEdit("t0.s0", replacement));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StatelessResponsesStringInputIsRewrittenInPlace()
    {
        const string json = "{ \"model\" : \"gpt-test\", \"input\" : \"!!NO!! The certificate expired.\", \"store\" : false }";
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "The certificate expired.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        AssertBytesOutsideHistoryAreEqual(json, result.Body, "input");
        Assert.Contains("The certificate expired.", Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenToolSuffixFailsBeforePlanner()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":"Inspecting.","tool_calls":[{"id":"call_1","type":"function","function":{"name":"inspect","arguments":"{}"}}]},
              {"role":"user","content":"!!NO!! Use certificate evidence."}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task LaterAssistantDoesNotMaskEarlierUnresolvedChatToolCall()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":"Inspecting.","tool_calls":[{"id":"call_1","type":"function","function":{"name":"inspect","arguments":"{}"}}]},
              {"role":"assistant","content":"A later message cannot resolve the call."},
              {"role":"user","content":"!!NO!! Use certificate evidence."}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task CompletedChatToolCallCanBeRewrittenAfterItsResult()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"inspect","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"call_1","content":"certificate expired"},
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t2.s0", "It is TLS."),
            new HistoryEdit("t3.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Contains("call_1", Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatToolResultStillAwaitsTheNextAssistant()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"inspect","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"call_1","content":"certificate expired"},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task AnthropicClientToolTurnWithTriggerBesideResultIsDeferred()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"inspect","signature":"signed"},
                {"type":"tool_use","id":"toolu_1","name":"inspect","input":{}}
              ]},
              {"role":"user","content":[
                {"type":"tool_result","tool_use_id":"toolu_1","content":"result"},
                {"type":"text","text":"!!NO!! It is TLS."}
              ]}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task CompletedAnthropicClientToolTurnCanBeRewritten()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"inspect","signature":"signed"},
                {"type":"tool_use","id":"toolu_1","name":"inspect","input":{}}
              ]},
              {"role":"user","content":[
                {"type":"tool_result","tool_use_id":"toolu_1","content":"result"}
              ]},
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t2.s0", "It is TLS."),
            new HistoryEdit("t3.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.DoesNotContain("signed", rewritten, StringComparison.Ordinal);
        Assert.Contains("toolu_1", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootExtendsPastMarkerBesideToolResultToClosingAssistant()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"inspect","signature":"signed"},
                {"type":"tool_use","id":"toolu_1","name":"inspect","input":{}}
              ]},
              {"role":"user","content":[
                {"type":"tool_result","tool_use_id":"toolu_1","content":"result"},
                {"type":"text","text":"!!NO!! It is TLS."}
              ]},
              {"role":"assistant","content":"It is DNS."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t1.s0", "It is TLS."),
            new HistoryEdit("t2.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.DoesNotContain("signed", rewritten, StringComparison.Ordinal);
        Assert.Contains("toolu_1", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedAnthropicServerToolResultIsPreservedWhileThinkingIsRemoved()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"inspect","signature":"signed"},
                {"type":"server_tool_use","id":"srv_1","name":"web_search","input":{"query":"certificate"}},
                {"type":"web_search_tool_result","tool_use_id":"srv_1","content":[{"type":"web_search_result","encrypted_content":"tool-opaque"}]},
                {"type":"text","text":"It is DNS."}
              ]},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(request => request.Mode == HistoryEditPlanningMode.Continuation
            ? Success()
            : Success(
                new HistoryEdit("t0.s0", "It is TLS."),
                new HistoryEdit("t1.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.DoesNotContain("signed", rewritten, StringComparison.Ordinal);
        Assert.Contains("srv_1", rewritten, StringComparison.Ordinal);
        Assert.Contains("tool-opaque", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicServerToolResultCanResolveAcrossAssistantResponses()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":[
                {"type":"server_tool_use","id":"srv_1","name":"web_search","input":{"query":"certificate"}}
              ]},
              {"role":"assistant","content":[
                {"type":"web_search_tool_result","tool_use_id":"srv_1","content":[{"type":"web_search_result","encrypted_content":"tool-opaque"}]},
                {"type":"text","text":"It is DNS."}
              ]},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t1.s0", "It is TLS."),
            new HistoryEdit("t2.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Contains("srv_1", rewritten, StringComparison.Ordinal);
        Assert.Contains("tool-opaque", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnresolvedAnthropicServerToolUseIsDeferred()
    {
        const string json = """
            {"messages":[
              {"role":"user","content":"!!NO!! It is TLS."},
              {"role":"assistant","content":[
                {"type":"server_tool_use","id":"srv_1","name":"web_search","input":{"query":"certificate"}}
              ]}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task LaterAssistantDoesNotMaskEarlierUnresolvedAnthropicToolUse()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"inspect","input":{}}]},
              {"role":"assistant","content":"A later message cannot resolve the tool use."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task ActiveOpenAIResponsesClientManagedCallIsDeferred()
    {
        const string json = """
            {"input":[
              {"role":"user","content":"!!NO!! It is TLS."},
              {"type":"function_call","id":"fc_1","call_id":"call_1","status":"completed","name":"inspect","arguments":"{}"}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task LaterAssistantDoesNotMaskEarlierUnresolvedResponsesCall()
    {
        const string json = """
            {"input":[
              {"type":"function_call","id":"fc_1","call_id":"call_1","status":"completed","name":"inspect","arguments":"{}"},
              {"role":"assistant","content":"A later message cannot resolve the call."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task CompletedResponsesClientManagedCallCanBeRewrittenAfterOutput()
    {
        const string json = """
            {"input":[
              {"type":"function_call","id":"fc_1","call_id":"call_1","status":"completed","name":"inspect","arguments":"{}"},
              {"type":"function_call_output","call_id":"call_1","output":"certificate expired"},
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t2.s0", "It is TLS."),
            new HistoryEdit("t3.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Contains("call_1", Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponsesLocalShellOutputCorrelatesById()
    {
        const string json = """
            {"input":[
              {"type":"local_shell_call","id":"item_1","call_id":"shell_1","status":"completed","action":{"command":"pwd"}},
              {"type":"local_shell_call_output","id":"shell_1","output":"/workspace"},
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t2.s0", "It is TLS."),
            new HistoryEdit("t3.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Contains("shell_1", Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaterAssistantDoesNotMaskPendingResponsesMcpApproval()
    {
        const string json = """
            {"input":[
              {"type":"mcp_approval_request","id":"approval_1","server_label":"docs","name":"search","arguments":"{}"},
              {"role":"assistant","content":"A later message cannot resolve the approval."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task CompletedResponsesMcpApprovalCanBeRewrittenAfterAssistant()
    {
        const string json = """
            {"input":[
              {"type":"mcp_approval_request","id":"approval_1","server_label":"docs","name":"search","arguments":"{}"},
              {"type":"mcp_approval_response","approval_request_id":"approval_1","approve":true},
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t2.s0", "It is TLS."),
            new HistoryEdit("t3.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Contains("approval_1", Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponsesCallOutputStillAwaitsTheNextAssistant()
    {
        const string json = """
            {"input":[
              {"type":"function_call","id":"fc_1","call_id":"call_1","status":"completed","name":"inspect","arguments":"{}"},
              {"type":"function_call_output","call_id":"call_1","output":"certificate expired"},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task InProgressOpenAIResponsesHostedCallIsDeferred()
    {
        const string json = """
            {"input":[
              {"role":"user","content":"!!NO!! It is TLS."},
              {"type":"image_generation_call","id":"img_1","status":"in_progress"}
            ]}
            """;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task CompletedOpenAIResponsesHostedCallDoesNotDeferForever()
    {
        const string json = """
            {"input":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."},
              {"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search","query":"certificate"}},
              {"role":"user","content":"Continue."}
            ]}
            """;
        FakePlanner planner = new(request => request.Mode == HistoryEditPlanningMode.Continuation
            ? Success()
            : Success(
                new HistoryEdit("t0.s0", "It is TLS."),
                new HistoryEdit("t1.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Contains("ws_1", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicThinkingAndRedactedThinkingAreRemovedAsWholeBlocks()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"old inference","signature":"encrypted-signature"},
                {"type":"redacted_thinking","data":"encrypted-data"},
                {"type":"text","text":"It is DNS."}
              ]},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "It is the certificate."),
            new HistoryEdit("t1.s0", "It is the certificate.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal(2, result.OpaqueBlockCount);
        Assert.DoesNotContain("encrypted-signature", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted-data", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("thinking", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullEditCannotLeaveOnlyAnthropicBlocksThatCleanupWillRemove()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"old inference","signature":"opaque"},
                {"type":"text","text":"It is DNS."}
              ],"unknown_message_field":"preserve"},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", null),
            new HistoryEdit("t1.s0", "It is the certificate.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.AnthropicMessages,
            json,
            planner);

        Assert.Equal(RewriteOutcome.UnsafeRewriterOutput, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public async Task OpenAIReasoningAndCompactionItemsAreRemovedAfterRewrite()
    {
        const string json = """
            {"input":[
              {"type":"reasoning","id":"rs_1","encrypted_content":"opaque-reasoning"},
              {"type":"compaction","id":"cmp_1","encrypted_content":"opaque-compaction"},
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t2.s0", "It is the certificate."),
            new HistoryEdit("t3.s0", "It is the certificate.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIResponses,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Equal(2, result.OpaqueBlockCount);
        Assert.DoesNotContain("opaque-reasoning", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-compaction", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatReasoningPropertiesAreRemovedAfterRewrite()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":"It is DNS.","reasoning_content":"old","encrypted_content":"cipher"},
              {"role":"user","content":"!!NO!! It is the certificate."}
            ]}
            """;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "It is the certificate."),
            new HistoryEdit("t1.s0", "It is the certificate.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(2, result.OpaqueBlockCount);
        Assert.DoesNotContain("reasoning_content", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("cipher", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlannerFailureLeavesOriginalBytesAndMapsOutcome()
    {
        const string json = """
            {"messages":[{"role":"user","content":"!!NO!! It is TLS."}]}
            """;
        FakePlanner planner = new(_ => HistoryEditPlannerResult.Failed(
            HistoryEditPlannerFailure.Unavailable));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner);

        Assert.Equal(RewriteOutcome.RewriterUnavailable, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public async Task DurableCacheFailureRejectsMarkedHistoryBeforePlanning()
    {
        const string json = """
            {"messages":[{"role":"user","content":"!!NO!! It is TLS."}]}
            """;
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "PinkUnicornProxy.Tests",
            $"directory-instead-of-database-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databasePath);
        try
        {
            PinkUnicornOptions options = DefaultOptions();
            options.HistoryRewrite.Cache.DatabasePath = databasePath;
            FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

            RewriteResult result = await RewriteAsync(
                ProviderRequestKind.OpenAIChatCompletions,
                json,
                planner,
                options);

            Assert.Equal(RewriteOutcome.CacheUnavailable, result.Outcome);
            Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
            Assert.Equal(0, planner.CallCount);
        }
        finally
        {
            Directory.Delete(databasePath);
        }
    }

    [Fact]
    public async Task PublicationFailureAfterPlanningDoesNotForwardAnUncommittedRevision()
    {
        const string json = """
            {"messages":[{"role":"user","content":"!!NO!! It is TLS."}]}
            """;
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PinkUnicornProxy.Tests",
            $"publication-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "cache.db");
        try
        {
            PinkUnicornOptions options = DefaultOptions();
            options.HistoryRewrite.Cache.DatabasePath = databasePath;
            FakePlanner planner = new(_ =>
            {
                File.Delete(databasePath);
                return Success(new HistoryEdit("t0.s0", "It is TLS."));
            });

            RewriteResult result = await RewriteAsync(
                ProviderRequestKind.OpenAIChatCompletions,
                json,
                planner,
                options);

            Assert.Equal(RewriteOutcome.CacheUnavailable, result.Outcome);
            Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
            Assert.Equal(1, planner.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuditModeDetectsTriggerWithoutPlannerOrMutation()
    {
        const string json = """
            {"messages":[{"role":"user","content":"!!NO!! It is TLS."}]}
            """;
        PinkUnicornOptions options = DefaultOptions();
        options.Mode = ProxyMode.Audit;
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner,
            options);

        Assert.Equal(RewriteOutcome.AuditMatch, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task TooManyEditsRejectsWholePlan()
    {
        const string json = """
            {"messages":[
              {"role":"assistant","content":"It is DNS."},
              {"role":"user","content":"!!NO!! It is TLS."}
            ]}
            """;
        PinkUnicornOptions options = DefaultOptions();
        options.MaximumEditsPerRequest = 1;
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "It is TLS."),
            new HistoryEdit("t1.s0", "It is TLS.")));

        RewriteResult result = await RewriteAsync(
            ProviderRequestKind.OpenAIChatCompletions,
            json,
            planner,
            options);

        Assert.Equal(RewriteOutcome.EditLimitExceeded, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    private static HistoryEditPlannerResult Success(params HistoryEdit[] edits) =>
        HistoryEditPlannerResult.Success(new HistoryEditPlan(edits));

    private static async Task<RewriteResult> RewriteAsync(
        ProviderRequestKind provider,
        string json,
        FakePlanner planner,
        PinkUnicornOptions? options = null)
    {
        ConversationRewriter rewriter = new(options ?? DefaultOptions(), planner);
        return await rewriter.RewriteAsync(
            provider,
            Encoding.UTF8.GetBytes(json),
            CancellationToken.None);
    }

    private static PinkUnicornOptions DefaultOptions() => new()
    {
        Mode = ProxyMode.Rewrite,
        MaximumRequestBodyBytes = 4 * 1024 * 1024,
        MaximumEditsPerRequest = 128,
        HistoryRewrite = new HistoryRewriteOptions
        {
            TriggerTokens = ["!!NO!!"],
            Cache = new HistoryRewriteCacheOptions
            {
                DatabasePath = CreateTestDatabasePath(),
            },
        },
    };

    private static string CreateTestDatabasePath() => Path.Combine(
        Path.GetTempPath(),
        "PinkUnicornProxy.Tests",
        $"conversation-{Guid.NewGuid():N}.db");

    private static void AssertBytesOutsideHistoryAreEqual(
        string originalText,
        byte[] rewritten,
        string historyProperty)
    {
        byte[] original = Encoding.UTF8.GetBytes(originalText);
        Assert.True(JsonHistorySplicer.TryFindTopLevelValue(
            original,
            historyProperty,
            out JsonByteRange originalRange));
        Assert.True(JsonHistorySplicer.TryFindTopLevelValue(
            rewritten,
            historyProperty,
            out JsonByteRange rewrittenRange));
        Assert.Equal(original[..originalRange.Start], rewritten[..rewrittenRange.Start]);
        Assert.Equal(
            original[(originalRange.Start + originalRange.Length)..],
            rewritten[(rewrittenRange.Start + rewrittenRange.Length)..]);
    }

    private static JsonArray ParseHistoryArray(byte[] body, string propertyName)
    {
        JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(body));
        return Assert.IsType<JsonArray>(root[propertyName]);
    }

    private static byte[] ExtractHistoryBytes(byte[] body, string propertyName)
    {
        Assert.True(JsonHistorySplicer.TryFindTopLevelValue(
            body,
            propertyName,
            out JsonByteRange range));
        return body.AsSpan(range.Start, range.Length).ToArray();
    }

    private sealed class FakePlanner : IHistoryEditPlanner
    {
        private readonly Func<HistoryEditRequest, Task<HistoryEditPlannerResult>> callback;
        private readonly List<HistoryEditRequest> requests = [];

        public FakePlanner(Func<HistoryEditRequest, HistoryEditPlannerResult> callback)
        {
            this.callback = request => Task.FromResult(callback(request));
        }

        private FakePlanner(
            Func<HistoryEditRequest, Task<HistoryEditPlannerResult>> callback,
            bool isAsync)
        {
            _ = isAsync;
            this.callback = callback;
        }

        public static FakePlanner FromAsync(
            Func<HistoryEditRequest, Task<HistoryEditPlannerResult>> callback) =>
            new(callback, isAsync: true);

        public int CallCount { get; private set; }

        public List<HistoryEditRequest> Requests => requests;

        public Task<HistoryEditPlannerResult> CreatePlanAsync(
            HistoryEditRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            requests.Add(request);
            return callback(request);
        }
    }
}
