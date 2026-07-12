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
    UnsupportedStatefulContext,
    DeferredOpenToolTurn,
    ProtectedContentConflict,
    UnsupportedContentType,
    UnsupportedContentEncoding,
    SignedBodyConflict,
    CorrectionLimitExceeded,
}

internal sealed record RewriteResult(
    byte[] Body,
    RewriteOutcome Outcome,
    int CorrectionCount = 0,
    int TextEditCount = 0,
    int OpaqueBlockCount = 0)
{
    public bool BodyChanged => Outcome == RewriteOutcome.Rewritten;

    public string HeaderValue => Outcome switch
    {
        RewriteOutcome.Unchanged => "unchanged",
        RewriteOutcome.Rewritten => "rewritten",
        RewriteOutcome.AuditMatch => "audit-match",
        RewriteOutcome.InvalidJson => "invalid-json",
        RewriteOutcome.UnsupportedShape => "unsupported-shape",
        RewriteOutcome.UnsupportedStatefulContext => "unsupported-stateful-context",
        RewriteOutcome.DeferredOpenToolTurn => "deferred-open-tool-turn",
        RewriteOutcome.ProtectedContentConflict => "protected-content-conflict",
        RewriteOutcome.UnsupportedContentType => "unsupported-content-type",
        RewriteOutcome.UnsupportedContentEncoding => "unsupported-content-encoding",
        RewriteOutcome.SignedBodyConflict => "signed-body-conflict",
        RewriteOutcome.CorrectionLimitExceeded => "correction-limit-exceeded",
        _ => "unknown",
    };

    public static RewriteResult Unchanged(byte[] body, RewriteOutcome outcome = RewriteOutcome.Unchanged) =>
        new(body, outcome);
}

internal sealed record CorrectionIntent(
    string? RejectedClaim,
    string AffirmativeText,
    bool IsDeictic,
    string Rule);
