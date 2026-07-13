using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PinkUnicornProxy.Configuration;

namespace PinkUnicornProxy.Rewriting;

internal sealed partial class ConversationRewriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 256,
        WriteIndented = false,
    };

    private readonly PinkUnicornOptions options;
    private readonly IHistoryEditPlanner planner;
    private readonly HistoryRewriteCache cache;
    private readonly ILogger<ConversationRewriter>? logger;

    public ConversationRewriter(
        IOptions<PinkUnicornOptions> options,
        IHistoryEditPlanner planner,
        HistoryRewriteCache cache,
        ILogger<ConversationRewriter> logger)
        : this(options.Value, planner, cache, logger)
    {
    }

    internal ConversationRewriter(
        PinkUnicornOptions options,
        IHistoryEditPlanner planner)
        : this(
            options,
            planner,
            new HistoryRewriteCache(
                options.HistoryRewrite.Cache,
                triggerTokens: options.HistoryRewrite.TriggerTokens),
            logger: null)
    {
    }

    internal ConversationRewriter(
        PinkUnicornOptions options,
        IHistoryEditPlanner planner,
        HistoryRewriteCache cache,
        ILogger<ConversationRewriter>? logger = null)
    {
        this.options = options;
        this.planner = planner;
        this.cache = cache;
        this.logger = logger;
    }

    public Task<RewriteResult> RewriteAsync(
        ProviderRequestKind provider,
        byte[] originalBody,
        CancellationToken cancellationToken) =>
        RewriteAsync(
            provider,
            originalBody,
            hasBodyIntegrityProtection: false,
            cancellationToken);

    public async Task<RewriteResult> RewriteAsync(
        ProviderRequestKind provider,
        byte[] originalBody,
        bool hasBodyIntegrityProtection,
        CancellationToken cancellationToken)
    {
        if (options.Mode == ProxyMode.Off)
        {
            return RewriteResult.Unchanged(originalBody);
        }

        string[] triggerTokens = options.HistoryRewrite.TriggerTokens;
        JsonObject root;
        try
        {
            JsonNode? parsed = JsonNode.Parse(
                originalBody,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 256,
                });

            if (parsed is not JsonObject parsedObject)
            {
                return RewriteResult.Unchanged(originalBody, RewriteOutcome.UnsupportedShape);
            }

            root = parsedObject;
        }
        catch (JsonException)
        {
            return RewriteResult.Unchanged(originalBody, RewriteOutcome.InvalidJson);
        }

        if (JsonHistorySplicer.ContainsDuplicateProperties(originalBody))
        {
            return JsonHistorySplicer.ContainsUserTrigger(originalBody, provider, triggerTokens)
                ? new RewriteResult(originalBody, RewriteOutcome.AmbiguousJsonProperties, 1)
                : RewriteResult.Unchanged(originalBody);
        }

        if (!ConversationTranscript.TryCreate(provider, root, out ConversationTranscript? transcript)
            || transcript is null)
        {
            return RewriteResult.Unchanged(originalBody, RewriteOutcome.UnsupportedShape);
        }

        PlanningTargetSet planningTargets = transcript.CreatePlanningTargets(triggerTokens);
        if (planningTargets.TriggerCount == 0)
        {
            return RewriteResult.Unchanged(originalBody);
        }

        if (options.Mode == ProxyMode.Audit)
        {
            return new RewriteResult(
                originalBody,
                RewriteOutcome.AuditMatch,
                planningTargets.TriggerCount);
        }

        if (hasBodyIntegrityProtection)
        {
            return Failure(
                originalBody,
                RewriteOutcome.SignedBodyConflict,
                planningTargets.TriggerCount);
        }

        if (planningTargets.HasProtectedTrigger)
        {
            return Failure(
                originalBody,
                RewriteOutcome.ProtectedContentConflict,
                planningTargets.TriggerCount);
        }

        // Provider-held history is outside both the rewrite plan and the cache key.
        // Reject it even when the explicit input happens to extend a cached revision.
        if (transcript.IsStateful)
        {
            return Failure(
                originalBody,
                RewriteOutcome.UnsupportedStatefulContext,
                planningTargets.TriggerCount);
        }

        if (!JsonHistorySplicer.TryFindTopLevelValue(
                originalBody,
                transcript.HistoryPropertyName,
                out JsonByteRange historyRange))
        {
            return Failure(
                originalBody,
                RewriteOutcome.UnsafeRewriterOutput,
                planningTargets.TriggerCount);
        }

        string originalHistoryJson = Encoding.UTF8.GetString(
            originalBody.AsSpan(historyRange.Start, historyRange.Length));
        HistoryCacheSnapshot fullSnapshot = HistoryRewriteCache.CreateSnapshot(
            provider,
            transcript.HistoryNode);

        if (!cache.Enabled)
        {
            return await RewriteWholeWithoutCacheAsync(
                provider,
                originalBody,
                historyRange,
                transcript,
                planningTargets,
                originalHistoryJson,
                cancellationToken);
        }

        try
        {
            if (!fullSnapshot.IsArray)
            {
                return await RewriteScalarWithCacheAsync(
                    provider,
                    originalBody,
                    historyRange,
                    transcript,
                    planningTargets,
                    originalHistoryJson,
                    fullSnapshot,
                    cancellationToken);
            }

            return await RewriteArrayWithCacheAsync(
                provider,
                originalBody,
                historyRange,
                transcript,
                planningTargets,
                originalHistoryJson,
                fullSnapshot,
                cancellationToken);
        }
        catch (HistoryRewriteCacheException exception)
        {
            if (logger is not null)
            {
                LogCacheUnavailable(logger, exception);
            }

            return Failure(
                originalBody,
                RewriteOutcome.CacheUnavailable,
                planningTargets.TriggerCount);
        }
    }

    private async Task<RewriteResult> RewriteWholeWithoutCacheAsync(
        ProviderRequestKind provider,
        byte[] originalBody,
        JsonByteRange historyRange,
        ConversationTranscript transcript,
        PlanningTargetSet planningTargets,
        string historyJson,
        CancellationToken cancellationToken)
    {
        if (transcript.HasActiveAtomicSuffix())
        {
            return Failure(
                originalBody,
                RewriteOutcome.DeferredOpenToolTurn,
                planningTargets.TriggerCount);
        }

        HistoryRevisionResult revision = await CreateWholeRevisionAsync(
            provider,
            transcript,
            planningTargets,
            historyJson,
            cancellationToken);
        return CreateRewriteResult(
            originalBody,
            historyRange,
            planningTargets.TriggerCount,
            revision,
            cacheHit: false);
    }

    private async Task<RewriteResult> RewriteScalarWithCacheAsync(
        ProviderRequestKind provider,
        byte[] originalBody,
        JsonByteRange historyRange,
        ConversationTranscript transcript,
        PlanningTargetSet planningTargets,
        string historyJson,
        HistoryCacheSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        HistoryCacheMatch? cached = await GetCacheMatchAsync(
            snapshot,
            planningTargets,
            cancellationToken);
        if (cached is not null)
        {
            return CreateCachedRewriteResult(
                originalBody,
                historyRange,
                planningTargets.TriggerCount,
                cached);
        }

        using IDisposable creationLock = await cache.AcquireCreationLockAsync(
            snapshot,
            cancellationToken);
        cached = await GetCacheMatchAsync(snapshot, planningTargets, cancellationToken);
        if (cached is not null)
        {
            return CreateCachedRewriteResult(
                originalBody,
                historyRange,
                planningTargets.TriggerCount,
                cached);
        }

        if (transcript.HasActiveAtomicSuffix())
        {
            return Failure(
                originalBody,
                RewriteOutcome.DeferredOpenToolTurn,
                planningTargets.TriggerCount);
        }

        HistoryRevisionResult revision = await CreateWholeRevisionAsync(
            provider,
            transcript,
            planningTargets,
            historyJson,
            cancellationToken);
        RewriteResult result = CreateRewriteResult(
            originalBody,
            historyRange,
            planningTargets.TriggerCount,
            revision,
            cacheHit: false);
        if (result.Outcome == RewriteOutcome.Rewritten)
        {
            HistoryCacheStoreResult stored = await cache.StoreAsync(
                snapshot,
                planningTargets.TriggerTurnIndices,
                revision.RewrittenHistory!,
                revision.EditCount,
                revision.OpaqueBlockCount,
                cancellationToken);
            if (!stored.Inserted)
            {
                return CreateCachedRewriteResult(
                    originalBody,
                    historyRange,
                    planningTargets.TriggerCount,
                    stored.Match);
            }
        }

        return result;
    }

    private async Task<RewriteResult> RewriteArrayWithCacheAsync(
        ProviderRequestKind provider,
        byte[] originalBody,
        JsonByteRange historyRange,
        ConversationTranscript transcript,
        PlanningTargetSet planningTargets,
        string originalHistoryJson,
        HistoryCacheSnapshot fullSnapshot,
        CancellationToken cancellationToken)
    {
        HistoryCacheMatch? cached = await GetCacheMatchAsync(
            fullSnapshot,
            planningTargets,
            cancellationToken);
        if (cached is not null)
        {
            if (cached.IsExact)
            {
                return CreateCachedRewriteResult(
                    originalBody,
                    historyRange,
                    planningTargets.TriggerCount,
                    cached);
            }

            return await RewriteContinuationFullSuffixAsync(
                provider,
                originalBody,
                historyRange,
                transcript,
                planningTargets,
                originalHistoryJson,
                fullSnapshot,
                cached,
                options.MaximumEditsPerRequest,
                semanticEditAlreadyMade: false,
                priorEditsThisRequest: 0,
                priorOpaqueBlocksThisRequest: 0,
                cancellationToken: cancellationToken);
        }

        return await RewriteArrayFromRootAsync(
            provider,
            originalBody,
            historyRange,
            transcript,
            planningTargets,
            originalHistoryJson,
            fullSnapshot,
            cancellationToken);
    }

    private async Task<RewriteResult> RewriteArrayFromRootAsync(
        ProviderRequestKind provider,
        byte[] originalBody,
        JsonByteRange historyRange,
        ConversationTranscript fullTranscript,
        PlanningTargetSet fullPlanningTargets,
        string originalHistoryJson,
        HistoryCacheSnapshot fullSnapshot,
        CancellationToken cancellationToken)
    {
        // Without an already-authoritative cached prefix, changing any part of a
        // history that ends in an active tool/thinking transaction is unsafe.
        if (fullTranscript.HasActiveAtomicSuffix())
        {
            return Failure(
                originalBody,
                RewriteOutcome.DeferredOpenToolTurn,
                fullPlanningTargets.TriggerCount);
        }

        JsonArray originalHistory = fullTranscript.HistoryNode as JsonArray
            ?? throw new InvalidOperationException("An array cache snapshot requires array history.");
        int rootItemCount = checked(fullPlanningTargets.TriggerTurnIndices.Max() + 1);
        ConversationTranscript? rootTranscript;
        while (true)
        {
            JsonArray rootHistory = CloneRange(originalHistory, 0, rootItemCount);
            if (!TryCreateSyntheticTranscript(
                    provider,
                    fullTranscript.HistoryPropertyName,
                    rootHistory,
                    out rootTranscript)
                || rootTranscript is null)
            {
                return Failure(
                    originalBody,
                    RewriteOutcome.UnsafeRewriterOutput,
                    fullPlanningTargets.TriggerCount);
            }

            if (!rootTranscript.HasActiveAtomicSuffix() || rootItemCount == originalHistory.Count)
            {
                break;
            }

            rootItemCount++;
        }

        PlanningTargetSet rootPlanningTargets = rootTranscript.CreatePlanningTargets(
            options.HistoryRewrite.TriggerTokens);
        HistoryCacheSnapshot rootSnapshot = HistoryRewriteCache.CreateSnapshot(
            provider,
            rootTranscript.HistoryNode);

        HistoryCacheMatch? concurrentRevision = null;
        HistoryCacheMatch? rootRevision;
        bool rootWasCached;
        int rootOperationsThisRequest = 0;
        using (await cache.AcquireCreationLockAsync(rootSnapshot, cancellationToken))
        {
            // A concurrent ancestor request may have established either the exact
            // full revision or a usable immutable prefix while this request waited.
            HistoryCacheMatch? cached = await GetCacheMatchAsync(
                fullSnapshot,
                fullPlanningTargets,
                cancellationToken);
            if (cached is not null)
            {
                concurrentRevision = cached;
                rootRevision = null;
                rootWasCached = true;
            }
            else
            {
                rootRevision = await GetCacheMatchAsync(
                    rootSnapshot,
                    rootPlanningTargets,
                    cancellationToken);
                rootWasCached = rootRevision is not null;
                if (!rootWasCached)
                {
                    if (rootTranscript.HasActiveAtomicSuffix())
                    {
                        return Failure(
                            originalBody,
                            RewriteOutcome.DeferredOpenToolTurn,
                            fullPlanningTargets.TriggerCount);
                    }

                    string rootHistoryJson = rootTranscript.HistoryNode.ToJsonString(SerializerOptions);
                    HistoryRevisionResult plannedRoot = await CreateWholeRevisionAsync(
                        provider,
                        rootTranscript,
                        rootPlanningTargets,
                        rootHistoryJson,
                        cancellationToken);
                    if (!plannedRoot.IsSuccess)
                    {
                        return Failure(
                            originalBody,
                            plannedRoot.Failure!.Value,
                            fullPlanningTargets.TriggerCount);
                    }

                    if (plannedRoot.RewrittenHistory!.LongLength > options.MaximumRequestBodyBytes)
                    {
                        return Failure(
                            originalBody,
                            RewriteOutcome.UnsafeRewriterOutput,
                            fullPlanningTargets.TriggerCount);
                    }

                    HistoryCacheStoreResult stored = await cache.StoreAsync(
                        rootSnapshot,
                        rootPlanningTargets.TriggerTurnIndices,
                        plannedRoot.RewrittenHistory,
                        plannedRoot.EditCount,
                        plannedRoot.OpaqueBlockCount,
                        cancellationToken);
                    rootRevision = stored.Match;
                    rootWasCached = !stored.Inserted;
                    rootOperationsThisRequest = stored.Inserted
                        ? plannedRoot.OperationCount
                        : 0;
                }
            }
        }

        if (concurrentRevision is not null)
        {
            if (concurrentRevision.IsExact)
            {
                return CreateCachedRewriteResult(
                    originalBody,
                    historyRange,
                    fullPlanningTargets.TriggerCount,
                    concurrentRevision);
            }

            return await RewriteContinuationFullSuffixAsync(
                provider,
                originalBody,
                historyRange,
                fullTranscript,
                fullPlanningTargets,
                originalHistoryJson,
                fullSnapshot,
                concurrentRevision,
                options.MaximumEditsPerRequest,
                semanticEditAlreadyMade: false,
                priorEditsThisRequest: 0,
                priorOpaqueBlocksThisRequest: 0,
                cancellationToken: cancellationToken);
        }

        if (rootItemCount == originalHistory.Count)
        {
            return CreateCachedRewriteResult(
                originalBody,
                historyRange,
                fullPlanningTargets.TriggerCount,
                rootRevision!,
                cacheHit: rootWasCached);
        }

        return await RewriteContinuationFullSuffixAsync(
            provider,
            originalBody,
            historyRange,
            fullTranscript,
            fullPlanningTargets,
            originalHistoryJson,
            fullSnapshot,
            rootRevision!,
            rootWasCached
                ? options.MaximumEditsPerRequest
                : options.MaximumEditsPerRequest - rootOperationsThisRequest,
            semanticEditAlreadyMade: !rootWasCached,
            priorEditsThisRequest: rootWasCached ? 0 : rootRevision!.EditCount,
            priorOpaqueBlocksThisRequest: rootWasCached ? 0 : rootRevision!.OpaqueBlockCount,
            cancellationToken: cancellationToken);
    }

    private async Task<RewriteResult> RewriteContinuationFullSuffixAsync(
        ProviderRequestKind provider,
        byte[] originalBody,
        JsonByteRange historyRange,
        ConversationTranscript fullTranscript,
        PlanningTargetSet fullPlanningTargets,
        string originalHistoryJson,
        HistoryCacheSnapshot fullSnapshot,
        HistoryCacheMatch immutablePrefix,
        int maximumEdits,
        bool semanticEditAlreadyMade,
        int priorEditsThisRequest,
        int priorOpaqueBlocksThisRequest,
        CancellationToken cancellationToken)
    {
        JsonArray originalHistory = fullTranscript.HistoryNode as JsonArray
            ?? throw new InvalidOperationException("A continuation requires array history.");
        if (immutablePrefix.PrefixItemCount < 0
            || immutablePrefix.PrefixItemCount >= originalHistory.Count)
        {
            return Failure(
                originalBody,
                RewriteOutcome.UnsafeRewriterOutput,
                fullPlanningTargets.TriggerCount);
        }

        using IDisposable creationLock = await cache.AcquireCreationLockAsync(
            fullSnapshot,
            cancellationToken);
        HistoryCacheMatch? concurrentRevision = await GetCacheMatchAsync(
            fullSnapshot,
            fullPlanningTargets,
            cancellationToken);
        if (concurrentRevision is not null)
        {
            if (concurrentRevision.IsExact)
            {
                return CreateCachedRewriteResult(
                    originalBody,
                    historyRange,
                    fullPlanningTargets.TriggerCount,
                    concurrentRevision);
            }

            if (concurrentRevision.PrefixItemCount > immutablePrefix.PrefixItemCount)
            {
                immutablePrefix = concurrentRevision;
                semanticEditAlreadyMade = false;
                priorEditsThisRequest = 0;
                priorOpaqueBlocksThisRequest = 0;
                maximumEdits = options.MaximumEditsPerRequest;
            }
        }

        int prefixItemCount = immutablePrefix.PrefixItemCount;
        JsonArray suffixHistory = CloneRange(
            originalHistory,
            prefixItemCount,
            originalHistory.Count - prefixItemCount);
        if (!TryCreateSyntheticTranscript(
                provider,
                fullTranscript.HistoryPropertyName,
                suffixHistory,
                out ConversationTranscript? suffixTranscript)
            || suffixTranscript is null)
        {
            return Failure(
                originalBody,
                RewriteOutcome.UnsafeRewriterOutput,
                fullPlanningTargets.TriggerCount);
        }

        PlanningTargetSet suffixTargets = suffixTranscript.CreatePlanningTargets(
            options.HistoryRewrite.TriggerTokens,
            prefixItemCount);
        if (suffixTargets.TriggerCount != 0)
        {
            return Failure(
                originalBody,
                RewriteOutcome.UnsafeRewriterOutput,
                fullPlanningTargets.TriggerCount);
        }

        string suffixHistoryJson = suffixTranscript.HistoryNode.ToJsonString(SerializerOptions);
        ApplyHistoryEditResult apply = new(ApplyHistoryEditOutcome.Applied, 0, 0);
        bool plannerCalled = suffixTargets.Targets.Count > 0;
        if (plannerCalled)
        {
            HistoryEditPlannerResult plannerResult = await planner.CreatePlanAsync(
                new HistoryEditRequest(
                    provider,
                    originalHistoryJson,
                    suffixTargets.Targets,
                    options.HistoryRewrite.TriggerTokens,
                    HistoryEditPlanningMode.Continuation,
                    Encoding.UTF8.GetString(immutablePrefix.RewrittenHistory),
                    suffixHistoryJson),
                cancellationToken);
            if (plannerResult.Plan is null)
            {
                return Failure(
                    originalBody,
                    MapPlannerFailure(plannerResult.Failure),
                    fullPlanningTargets.TriggerCount);
            }

            apply = suffixTranscript.ApplyPlan(
                plannerResult.Plan,
                options.HistoryRewrite.TriggerTokens,
                maximumEdits,
                checked((int)Math.Min(options.MaximumRequestBodyBytes, int.MaxValue)),
                prefixItemCount,
                requireTriggeredTargets: false,
                allowNoMaterialEdits: true);
            if (apply.Outcome != ApplyHistoryEditOutcome.Applied)
            {
                return Failure(
                    originalBody,
                    MapApplyFailure(apply.Outcome),
                    fullPlanningTargets.TriggerCount);
            }
        }

        bool invalidatesSuffixOpaqueState = semanticEditAlreadyMade || apply.EditCount > 0;
        if (invalidatesSuffixOpaqueState && fullTranscript.HasActiveAtomicSuffix())
        {
            return Failure(
                originalBody,
                RewriteOutcome.DeferredOpenToolTurn,
                fullPlanningTargets.TriggerCount);
        }

        int suffixOpaqueBlocks = invalidatesSuffixOpaqueState
            ? suffixTranscript.RemoveOpaqueReasoning()
            : 0;
        byte[] rewrittenSuffix;
        try
        {
            rewrittenSuffix = JsonSerializer.SerializeToUtf8Bytes(
                suffixTranscript.HistoryNode,
                SerializerOptions);
        }
        catch (JsonException)
        {
            return Failure(
                originalBody,
                RewriteOutcome.UnsafeRewriterOutput,
                fullPlanningTargets.TriggerCount);
        }

        if (!JsonHistorySplicer.TryConcatenateArrays(
                immutablePrefix.RewrittenHistory,
                rewrittenSuffix,
                out byte[]? rewrittenHistory)
            || rewrittenHistory is null)
        {
            return Failure(
                originalBody,
                RewriteOutcome.UnsafeRewriterOutput,
                fullPlanningTargets.TriggerCount);
        }

        RewriteResult result = CreateRewriteResult(
            originalBody,
            historyRange,
            fullPlanningTargets.TriggerCount,
            HistoryRevisionResult.Success(
                rewrittenHistory,
                checked(priorEditsThisRequest + apply.EditCount),
                checked(priorOpaqueBlocksThisRequest + suffixOpaqueBlocks),
                apply.OperationCount),
            cacheHit: !plannerCalled && priorEditsThisRequest == 0);
        if (result.Outcome == RewriteOutcome.Rewritten)
        {
            HistoryCacheStoreResult stored = await cache.StoreAsync(
                fullSnapshot,
                fullPlanningTargets.TriggerTurnIndices,
                rewrittenHistory,
                checked(immutablePrefix.EditCount + apply.EditCount),
                checked(immutablePrefix.OpaqueBlockCount + suffixOpaqueBlocks),
                cancellationToken);
            if (!stored.Inserted)
            {
                return CreateCachedRewriteResult(
                    originalBody,
                    historyRange,
                    fullPlanningTargets.TriggerCount,
                    stored.Match);
            }
        }

        return result;
    }

    private async Task<HistoryRevisionResult> CreateWholeRevisionAsync(
        ProviderRequestKind provider,
        ConversationTranscript transcript,
        PlanningTargetSet planningTargets,
        string historyJson,
        CancellationToken cancellationToken)
    {
        HistoryEditPlannerResult plannerResult = await planner.CreatePlanAsync(
            new HistoryEditRequest(
                provider,
                historyJson,
                planningTargets.Targets,
                options.HistoryRewrite.TriggerTokens),
            cancellationToken);
        if (plannerResult.Plan is null)
        {
            return HistoryRevisionResult.Failed(MapPlannerFailure(plannerResult.Failure));
        }

        ApplyHistoryEditResult apply = transcript.ApplyPlan(
            plannerResult.Plan,
            options.HistoryRewrite.TriggerTokens,
            options.MaximumEditsPerRequest,
            checked((int)Math.Min(options.MaximumRequestBodyBytes, int.MaxValue)));
        if (apply.Outcome != ApplyHistoryEditOutcome.Applied)
        {
            return HistoryRevisionResult.Failed(MapApplyFailure(apply.Outcome));
        }

        int opaqueBlocks = transcript.RemoveOpaqueReasoning();
        try
        {
            byte[] rewrittenHistory = JsonSerializer.SerializeToUtf8Bytes(
                transcript.HistoryNode,
                SerializerOptions);
            return HistoryRevisionResult.Success(
                rewrittenHistory,
                apply.EditCount,
                opaqueBlocks,
                apply.OperationCount);
        }
        catch (JsonException)
        {
            return HistoryRevisionResult.Failed(RewriteOutcome.UnsafeRewriterOutput);
        }
    }

    private Task<HistoryCacheMatch?> GetCacheMatchAsync(
        HistoryCacheSnapshot snapshot,
        PlanningTargetSet planningTargets,
        CancellationToken cancellationToken) =>
        cache.GetLongestPrefixAsync(
            snapshot,
            planningTargets.TriggerTurnIndices,
            cancellationToken);

    private RewriteResult CreateCachedRewriteResult(
        byte[] originalBody,
        JsonByteRange historyRange,
        int triggerCount,
        HistoryCacheMatch cached,
        bool cacheHit = true) =>
        CreateRewriteResult(
            originalBody,
            historyRange,
            triggerCount,
            HistoryRevisionResult.Success(
                cached.RewrittenHistory,
                cached.EditCount,
                cached.OpaqueBlockCount),
            cacheHit);

    private RewriteResult CreateRewriteResult(
        byte[] originalBody,
        JsonByteRange historyRange,
        int triggerCount,
        HistoryRevisionResult revision,
        bool cacheHit)
    {
        if (!revision.IsSuccess)
        {
            return Failure(originalBody, revision.Failure!.Value, triggerCount);
        }

        byte[] rewrittenBody = JsonHistorySplicer.Splice(
            originalBody,
            historyRange,
            revision.RewrittenHistory!);
        if (rewrittenBody.LongLength > options.MaximumRequestBodyBytes)
        {
            return Failure(
                originalBody,
                RewriteOutcome.UnsafeRewriterOutput,
                triggerCount);
        }

        return new RewriteResult(
            rewrittenBody,
            RewriteOutcome.Rewritten,
            triggerCount,
            revision.EditCount,
            revision.OpaqueBlockCount,
            cacheHit);
    }

    private static JsonArray CloneRange(JsonArray source, int start, int count)
    {
        JsonArray clone = [];
        int end = checked(start + count);
        for (int index = start; index < end; index++)
        {
            clone.Add(source[index]?.DeepClone());
        }

        return clone;
    }

    private static bool TryCreateSyntheticTranscript(
        ProviderRequestKind provider,
        string historyPropertyName,
        JsonNode history,
        out ConversationTranscript? transcript)
    {
        JsonObject syntheticRoot = new()
        {
            [historyPropertyName] = history,
        };
        return ConversationTranscript.TryCreate(provider, syntheticRoot, out transcript);
    }

    private static RewriteOutcome MapPlannerFailure(HistoryEditPlannerFailure? failure) =>
        failure switch
        {
            HistoryEditPlannerFailure.NotConfigured => RewriteOutcome.RewriterNotConfigured,
            HistoryEditPlannerFailure.Unavailable => RewriteOutcome.RewriterUnavailable,
            HistoryEditPlannerFailure.InputTooLarge => RewriteOutcome.PlannerInputTooLarge,
            HistoryEditPlannerFailure.InvalidResponse => RewriteOutcome.InvalidRewriterResponse,
            _ => RewriteOutcome.InvalidRewriterResponse,
        };

    private static RewriteOutcome MapApplyFailure(ApplyHistoryEditOutcome failure) =>
        failure == ApplyHistoryEditOutcome.EditLimitExceeded
            ? RewriteOutcome.EditLimitExceeded
            : RewriteOutcome.UnsafeRewriterOutput;

    private static RewriteResult Failure(
        byte[] originalBody,
        RewriteOutcome outcome,
        int triggerCount) =>
        new(originalBody, outcome, triggerCount);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Error,
        Message = "The durable history rewrite cache is unavailable; the marked request was rejected before primary forwarding.")]
    private static partial void LogCacheUnavailable(
        ILogger logger,
        Exception exception);

    private sealed record HistoryRevisionResult(
        byte[]? RewrittenHistory,
        RewriteOutcome? Failure,
        int EditCount = 0,
        int OpaqueBlockCount = 0,
        int OperationCount = 0)
    {
        public bool IsSuccess => RewrittenHistory is not null && Failure is null;

        public static HistoryRevisionResult Success(
            byte[] rewrittenHistory,
            int editCount,
            int opaqueBlockCount,
            int operationCount = 0) =>
            new(rewrittenHistory, null, editCount, opaqueBlockCount, operationCount);

        public static HistoryRevisionResult Failed(RewriteOutcome failure) =>
            new(null, failure);
    }
}
