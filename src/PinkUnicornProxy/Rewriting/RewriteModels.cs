namespace PinkUnicornProxy.Rewriting;

internal enum ProviderRequestKind
{
    OpenAIChatCompletions,
    OpenAIResponses,
    AnthropicMessages,
}

internal enum RewriteOutcome
{
    Unchanged,
    Rewritten,
    AuditMatch,
    InvalidJson,
    UnsupportedShape,
    AmbiguousJsonProperties,
    UnsupportedStatefulContext,
    DeferredOpenToolTurn,
    ProtectedContentConflict,
    SignedBodyConflict,
    RewriterNotConfigured,
    RewriterUnavailable,
    CacheUnavailable,
    PlannerInputTooLarge,
    InvalidRewriterResponse,
    UnsafeRewriterOutput,
    EditLimitExceeded,
}

internal sealed record RewriteResult(
    byte[] Body,
    RewriteOutcome Outcome,
    int TriggerCount = 0,
    int TextEditCount = 0,
    int OpaqueBlockCount = 0,
    bool CacheHit = false)
{
    public bool BodyChanged => Outcome == RewriteOutcome.Rewritten;

    public static RewriteResult Unchanged(byte[] body, RewriteOutcome outcome = RewriteOutcome.Unchanged) =>
        new(body, outcome);
}

internal sealed record HistoryTextTarget(
    string Id,
    int TurnIndex,
    string Role,
    bool ContainsTrigger);

internal sealed record HistoryEdit(string Id, string? Replacement);

internal sealed record HistoryEditPlan(IReadOnlyList<HistoryEdit> Edits);

internal sealed record HistoryEditRequest(
    ProviderRequestKind Provider,
    string HistoryJson,
    IReadOnlyList<HistoryTextTarget> Targets,
    IReadOnlyList<string> TriggerTokens,
    HistoryEditPlanningMode Mode = HistoryEditPlanningMode.FullHistory,
    string? RewrittenPrefixJson = null,
    string? EditableSuffixJson = null);

internal enum HistoryEditPlanningMode
{
    FullHistory,
    Continuation,
}

internal enum HistoryEditPlannerFailure
{
    NotConfigured,
    Unavailable,
    InputTooLarge,
    InvalidResponse,
}

internal sealed record HistoryEditPlannerResult(
    HistoryEditPlan? Plan,
    HistoryEditPlannerFailure? Failure = null)
{
    public static HistoryEditPlannerResult Success(HistoryEditPlan plan) => new(plan);

    public static HistoryEditPlannerResult Failed(HistoryEditPlannerFailure failure) => new(null, failure);
}
