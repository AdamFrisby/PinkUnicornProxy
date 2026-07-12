using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PinkUnicornProxy.Configuration;

namespace PinkUnicornProxy.Rewriting;

internal sealed class ConversationRewriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly PinkUnicornOptions options;

    public ConversationRewriter(IOptions<PinkUnicornOptions> options)
        : this(options.Value)
    {
    }

    internal ConversationRewriter(PinkUnicornOptions options)
    {
        this.options = options;
    }

    public RewriteResult Rewrite(ProviderRequestKind provider, byte[] originalBody)
    {
        if (options.Mode == ProxyMode.Off)
        {
            return RewriteResult.Unchanged(originalBody);
        }

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

        if (!ConversationTranscript.TryCreate(provider, root, out ConversationTranscript? transcript)
            || transcript is null)
        {
            return RewriteResult.Unchanged(originalBody, RewriteOutcome.UnsupportedShape);
        }

        List<DetectedCorrection> corrections = DetectCorrections(transcript);
        if (corrections.Count == 0)
        {
            return RewriteResult.Unchanged(originalBody);
        }

        if (corrections.Count > options.MaximumCorrectionsPerRequest)
        {
            return new RewriteResult(
                originalBody,
                RewriteOutcome.CorrectionLimitExceeded,
                corrections.Count);
        }

        if (options.Mode == ProxyMode.Audit)
        {
            return new RewriteResult(originalBody, RewriteOutcome.AuditMatch, corrections.Count);
        }

        if (transcript.IsStateful
            && options.StatefulResponsesPolicy != StatefulResponsesPolicy.FreshStart)
        {
            return new RewriteResult(
                originalBody,
                RewriteOutcome.UnsupportedStatefulContext,
                corrections.Count);
        }

        if (transcript.HasActiveAtomicSuffix())
        {
            return new RewriteResult(
                originalBody,
                RewriteOutcome.DeferredOpenToolTurn,
                corrections.Count);
        }

        int textEdits = 0;
        if (transcript.IsStateful)
        {
            textEdits += transcript.ClearStatefulReferences();
        }

        foreach (DetectedCorrection correction in corrections)
        {
            ApplyResult apply = ApplyCorrection(transcript, correction);
            if (apply.Failed)
            {
                return new RewriteResult(
                    originalBody,
                    RewriteOutcome.ProtectedContentConflict,
                    corrections.Count);
            }

            textEdits += apply.EditCount;
        }

        int opaqueBlocks = transcript.RemoveOpaqueReasoning();
        byte[] rewritten = JsonSerializer.SerializeToUtf8Bytes(root, SerializerOptions);

        return new RewriteResult(
            rewritten,
            RewriteOutcome.Rewritten,
            corrections.Count,
            textEdits,
            opaqueBlocks);
    }

    private List<DetectedCorrection> DetectCorrections(ConversationTranscript transcript)
    {
        List<DetectedCorrection> corrections = [];

        foreach (TranscriptTurn turn in transcript.Turns)
        {
            if (turn.Role != "user" || !turn.HasVisibleText)
            {
                continue;
            }

            if (CorrectionDetector.TryDetect(
                    turn.CombinedText,
                    options.MaximumCorrectionTextCharacters,
                    out CorrectionIntent? intent)
                && intent is not null)
            {
                corrections.Add(new DetectedCorrection(turn, intent));
            }
        }

        return corrections;
    }

    private static ApplyResult ApplyCorrection(
        ConversationTranscript transcript,
        DetectedCorrection correction)
    {
        int edits = 0;

        if (correction.Turn.HasProtectedTextMetadata)
        {
            return ApplyResult.Fail;
        }

        if (correction.Intent.IsDeictic || correction.Intent.RejectedClaim is null)
        {
            RemoveTurnResult removal = transcript.RemovePreviousAssistant(correction.Turn);
            if (removal == RemoveTurnResult.Protected)
            {
                return ApplyResult.Fail;
            }

            edits += removal == RemoveTurnResult.Removed ? 1 : 0;
        }
        else
        {
            ScrubSummary scrub = transcript.ScrubRejectedClaim(
                correction.Turn,
                correction.Intent.RejectedClaim);

            if (scrub.ProtectedMatchFound)
            {
                return ApplyResult.Fail;
            }

            edits += scrub.EditCount;
        }

        edits += ConversationTranscript.ReplaceCorrectionText(
            correction.Turn,
            correction.Intent.AffirmativeText);
        return new ApplyResult(edits, false);
    }

    private sealed record DetectedCorrection(TranscriptTurn Turn, CorrectionIntent Intent);

    private sealed record ApplyResult(int EditCount, bool Failed)
    {
        public static ApplyResult Fail { get; } = new(0, true);
    }
}
