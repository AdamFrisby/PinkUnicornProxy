using System.Text.Json.Nodes;

namespace PinkUnicornProxy.Rewriting;

internal sealed class ConversationTranscript
{
    private static readonly HashSet<string> OpenAITextBlockTypes = new(StringComparer.Ordinal)
    {
        "text",
        "input_text",
        "output_text",
    };

    private static readonly HashSet<string> AnthropicTextBlockTypes = new(StringComparer.Ordinal)
    {
        "text",
    };

    private static readonly HashSet<string> AnthropicOpaqueBlockTypes = new(StringComparer.Ordinal)
    {
        "thinking",
        "redacted_thinking",
    };

    private static readonly HashSet<string> NoIgnoredBlockTypes = new(StringComparer.Ordinal);

    private static readonly HashSet<string> OpenAIClientManagedCallTypes = new(StringComparer.Ordinal)
    {
        "apply_patch_call",
        "computer_call",
        "custom_tool_call",
        "function_call",
        "local_shell_call",
        "shell_call",
    };

    private readonly JsonObject root;
    private readonly JsonArray? itemArray;

    private ConversationTranscript(
        ProviderRequestKind provider,
        JsonObject root,
        JsonArray? itemArray,
        List<TranscriptTurn> turns,
        bool isStateful)
    {
        Provider = provider;
        this.root = root;
        this.itemArray = itemArray;
        Turns = turns;
        IsStateful = isStateful;
    }

    public ProviderRequestKind Provider { get; }

    public IReadOnlyList<TranscriptTurn> Turns { get; }

    public bool IsStateful { get; }

    public static bool TryCreate(
        ProviderRequestKind provider,
        JsonObject root,
        out ConversationTranscript? transcript)
    {
        return provider switch
        {
            ProviderRequestKind.OpenAIChatCompletions => TryCreateOpenAIChat(root, out transcript),
            ProviderRequestKind.OpenAIResponses => TryCreateOpenAIResponses(root, out transcript),
            ProviderRequestKind.AnthropicMessages => TryCreateAnthropic(root, out transcript),
            _ => Unsupported(out transcript),
        };
    }

    public bool HasActiveAtomicSuffix()
    {
        TranscriptTurn? latestAssistant = Turns
            .Where(turn => turn.IsAttached && turn.Role == "assistant")
            .LastOrDefault();

        if (Provider != ProviderRequestKind.OpenAIResponses)
        {
            return latestAssistant?.HasOpenToolUse == true;
        }

        if (itemArray is null)
        {
            return false;
        }

        int latestAssistantIndex = latestAssistant?.OriginalIndex ?? -1;
        for (int index = latestAssistantIndex + 1; index < itemArray.Count; index++)
        {
            if (itemArray[index] is not JsonObject item
                || !TryGetString(item, "type", out string type))
            {
                continue;
            }

            if (IsActiveOpenAIResponseItem(item, type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsActiveOpenAIResponseItem(JsonObject item, string type)
    {
        if (type == "reasoning" || type.EndsWith("_call_output", StringComparison.Ordinal))
        {
            return true;
        }

        if (!type.EndsWith("_call", StringComparison.Ordinal))
        {
            return false;
        }

        if (OpenAIClientManagedCallTypes.Contains(type))
        {
            return true;
        }

        return !TryGetString(item, "status", out string status)
            || (status != "completed" && status != "failed");
    }

    public ScrubSummary ScrubRejectedClaim(TranscriptTurn correction, string rejectedClaim)
    {
        int edits = 0;
        bool protectedMatch = false;

        foreach (TranscriptTurn turn in Turns)
        {
            bool isEarlierHumanOrAssistant = turn.OriginalIndex < correction.OriginalIndex
                && (turn.Role == "user" || turn.Role == "assistant");
            bool isLaterAssistant = turn.OriginalIndex > correction.OriginalIndex
                && turn.Role == "assistant";

            if (!turn.IsAttached || (!isEarlierHumanOrAssistant && !isLaterAssistant))
            {
                continue;
            }

            foreach (TextSlot slot in turn.TextSlots.ToArray())
            {
                if (!slot.IsAttached)
                {
                    continue;
                }

                ScrubResult scrub = TextScrubber.RemoveClaim(slot.Text, rejectedClaim);
                protectedMatch |= scrub.ProtectedMatchFound;
                if (!scrub.Changed)
                {
                    continue;
                }

                if (slot.HasTextDependentMetadata)
                {
                    protectedMatch = true;
                    continue;
                }

                edits++;
                if (scrub.Text.Length == 0)
                {
                    slot.Remove();
                }
                else
                {
                    slot.Set(scrub.Text);
                }
            }

            RemoveIfEmpty(turn);
        }

        return new ScrubSummary(edits, protectedMatch);
    }

    public RemoveTurnResult RemovePreviousAssistant(TranscriptTurn correction)
    {
        TranscriptTurn? previousAssistant = Turns
            .Where(turn => turn.OriginalIndex < correction.OriginalIndex)
            .Where(turn => turn.IsAttached && turn.Role == "assistant")
            .LastOrDefault();

        if (previousAssistant is null)
        {
            return RemoveTurnResult.NoTarget;
        }

        if (!previousAssistant.CanRemoveAtomically)
        {
            return RemoveTurnResult.Protected;
        }

        previousAssistant.Remove();
        return RemoveTurnResult.Removed;
    }

    public static int ReplaceCorrectionText(TranscriptTurn correction, string affirmativeText)
    {
        TextSlot? first = correction.TextSlots.FirstOrDefault(slot => slot.IsAttached);
        if (first is null)
        {
            return 0;
        }

        int edits = 1;
        first.Set(affirmativeText);

        foreach (TextSlot extra in correction.TextSlots.Where(slot => slot != first).ToArray())
        {
            if (extra.IsAttached)
            {
                extra.Remove();
                edits++;
            }
        }

        return edits;
    }

    public int ClearStatefulReferences()
    {
        int count = 0;
        count += root.Remove("previous_response_id") ? 1 : 0;
        count += root.Remove("conversation") ? 1 : 0;
        return count;
    }

    public int RemoveOpaqueReasoning()
    {
        return Provider switch
        {
            ProviderRequestKind.AnthropicMessages => RemoveAnthropicThinking(),
            ProviderRequestKind.OpenAIResponses => RemoveOpenAIOpaqueItems(),
            ProviderRequestKind.OpenAIChatCompletions => RemoveChatReasoningProperties(),
            _ => 0,
        };
    }

    private int RemoveAnthropicThinking()
    {
        int removed = 0;
        foreach (TranscriptTurn turn in Turns.Where(turn => turn.Role == "assistant" && turn.IsAttached))
        {
            if (turn.Node["content"] is not JsonArray content)
            {
                continue;
            }

            for (int index = content.Count - 1; index >= 0; index--)
            {
                if (content[index] is JsonObject block
                    && TryGetString(block, "type", out string type)
                    && (type == "thinking" || type == "redacted_thinking"))
                {
                    content.RemoveAt(index);
                    removed++;
                }
            }

            RemoveIfEmpty(turn);
        }

        return removed;
    }

    private int RemoveOpenAIOpaqueItems()
    {
        if (itemArray is null)
        {
            return 0;
        }

        int removed = 0;
        for (int index = itemArray.Count - 1; index >= 0; index--)
        {
            if (itemArray[index] is JsonObject item
                && TryGetString(item, "type", out string type)
                && (type == "reasoning" || type == "compaction"))
            {
                itemArray.RemoveAt(index);
                removed++;
            }
        }

        return removed;
    }

    private int RemoveChatReasoningProperties()
    {
        int removed = 0;
        string[] opaqueProperties = ["reasoning", "reasoning_content", "encrypted_content"];

        foreach (TranscriptTurn turn in Turns.Where(turn => turn.Role == "assistant" && turn.IsAttached))
        {
            foreach (string property in opaqueProperties)
            {
                removed += turn.Node.Remove(property) ? 1 : 0;
            }
        }

        return removed;
    }

    private static bool TryCreateOpenAIChat(JsonObject root, out ConversationTranscript? transcript)
    {
        if (root["messages"] is not JsonArray messages)
        {
            return Unsupported(out transcript);
        }

        List<TranscriptTurn> turns = [];
        for (int index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not JsonObject message
                || !TryGetString(message, "role", out string role))
            {
                continue;
            }

            List<TextSlot> slots = ExtractTextSlots(message, OpenAITextBlockTypes);
            bool hasNonTextBlocks = HasNonTextBlocks(
                message,
                OpenAITextBlockTypes,
                NoIgnoredBlockTypes);
            bool hasToolCalls = message["tool_calls"] is JsonArray { Count: > 0 }
                || message["function_call"] is not null;
            bool hasProtocolStructure = hasNonTextBlocks || hasToolCalls || role == "tool";

            turns.Add(new TranscriptTurn(
                message,
                messages,
                index,
                role,
                slots,
                hasProtocolStructure,
                hasToolCalls,
                role == "tool"));
        }

        transcript = new ConversationTranscript(
            ProviderRequestKind.OpenAIChatCompletions,
            root,
            messages,
            turns,
            false);
        return true;
    }

    private static bool TryCreateOpenAIResponses(JsonObject root, out ConversationTranscript? transcript)
    {
        bool isStateful = HasValue(root, "previous_response_id") || HasValue(root, "conversation");

        if (root["input"] is JsonValue inputValue && inputValue.TryGetValue(out string? _))
        {
            List<TextSlot> slots = [new TextSlot(root, "input")];
            TranscriptTurn turn = new(root, null, 0, "user", slots, false, false, false);
            transcript = new ConversationTranscript(
                ProviderRequestKind.OpenAIResponses,
                root,
                null,
                [turn],
                isStateful);
            return true;
        }

        if (root["input"] is not JsonArray input)
        {
            return Unsupported(out transcript);
        }

        List<TranscriptTurn> turns = [];
        for (int index = 0; index < input.Count; index++)
        {
            if (input[index] is not JsonObject item
                || !TryGetString(item, "role", out string role))
            {
                continue;
            }

            List<TextSlot> slots = ExtractTextSlots(item, OpenAITextBlockTypes);
            bool hasNonTextBlocks = HasNonTextBlocks(
                item,
                OpenAITextBlockTypes,
                NoIgnoredBlockTypes);
            turns.Add(new TranscriptTurn(
                item,
                input,
                index,
                role,
                slots,
                hasNonTextBlocks,
                false,
                false));
        }

        transcript = new ConversationTranscript(
            ProviderRequestKind.OpenAIResponses,
            root,
            input,
            turns,
            isStateful);
        return true;
    }

    private static bool TryCreateAnthropic(JsonObject root, out ConversationTranscript? transcript)
    {
        if (root["messages"] is not JsonArray messages)
        {
            return Unsupported(out transcript);
        }

        List<TranscriptTurn> turns = [];
        for (int index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not JsonObject message
                || !TryGetString(message, "role", out string role))
            {
                continue;
            }

            List<TextSlot> slots = ExtractTextSlots(message, AnthropicTextBlockTypes);
            bool hasProtocolStructure = HasNonTextBlocks(
                message,
                AnthropicTextBlockTypes,
                AnthropicOpaqueBlockTypes);
            bool hasToolUse = HasOpenAnthropicToolUse(message);
            bool hasToolResult = ContentContainsType(message, static type =>
                type == "tool_result"
                || type.EndsWith("_tool_result", StringComparison.Ordinal));

            turns.Add(new TranscriptTurn(
                message,
                messages,
                index,
                role,
                slots,
                hasProtocolStructure,
                hasToolUse,
                hasToolResult));
        }

        transcript = new ConversationTranscript(
            ProviderRequestKind.AnthropicMessages,
            root,
            messages,
            turns,
            false);
        return true;
    }

    private static List<TextSlot> ExtractTextSlots(JsonObject message, HashSet<string> textBlockTypes)
    {
        if (message["content"] is JsonValue value && value.TryGetValue(out string? _))
        {
            return [new TextSlot(message, "content")];
        }

        if (message["content"] is not JsonArray content)
        {
            return [];
        }

        List<TextSlot> slots = [];
        foreach (JsonNode? node in content)
        {
            if (node is JsonObject block
                && TryGetString(block, "type", out string type)
                && textBlockTypes.Contains(type)
                && block["text"] is JsonValue textValue
                && textValue.TryGetValue(out string? _))
            {
                slots.Add(new TextSlot(block, "text", content));
            }
        }

        return slots;
    }

    private static bool HasNonTextBlocks(
        JsonObject message,
        HashSet<string> textBlockTypes,
        HashSet<string> ignoredTypes)
    {
        if (message["content"] is not JsonArray content)
        {
            return false;
        }

        foreach (JsonNode? node in content)
        {
            if (node is not JsonObject block
                || !TryGetString(block, "type", out string type)
                || (!textBlockTypes.Contains(type) && !ignoredTypes.Contains(type)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContentContainsType(JsonObject message, Func<string, bool> predicate)
    {
        if (message["content"] is not JsonArray content)
        {
            return false;
        }

        foreach (JsonNode? node in content)
        {
            if (node is JsonObject block
                && TryGetString(block, "type", out string type)
                && predicate(type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOpenAnthropicToolUse(JsonObject message)
    {
        if (message["content"] is not JsonArray content)
        {
            return false;
        }

        HashSet<string> serverToolUses = new(StringComparer.Ordinal);
        HashSet<string> serverToolResults = new(StringComparer.Ordinal);
        bool unresolvedServerToolWithoutId = false;

        foreach (JsonNode? node in content)
        {
            if (node is not JsonObject block
                || !TryGetString(block, "type", out string type))
            {
                continue;
            }

            if (type == "tool_use")
            {
                return true;
            }

            if (type.EndsWith("_tool_use", StringComparison.Ordinal))
            {
                if (TryGetString(block, "id", out string id))
                {
                    serverToolUses.Add(id);
                }
                else
                {
                    unresolvedServerToolWithoutId = true;
                }
            }
            else if (type.EndsWith("_tool_result", StringComparison.Ordinal)
                && TryGetString(block, "tool_use_id", out string toolUseId))
            {
                serverToolResults.Add(toolUseId);
            }
        }

        serverToolUses.ExceptWith(serverToolResults);
        return unresolvedServerToolWithoutId || serverToolUses.Count > 0;
    }

    private static bool HasValue(JsonObject root, string propertyName) =>
        root.TryGetPropertyValue(propertyName, out JsonNode? value)
        && value is not null
        && (!(value is JsonValue jsonValue && jsonValue.TryGetValue(out string? text))
            || !string.IsNullOrWhiteSpace(text));

    private static bool TryGetString(JsonObject value, string propertyName, out string result)
    {
        result = string.Empty;
        if (value[propertyName] is not JsonValue jsonValue
            || !jsonValue.TryGetValue(out string? candidate)
            || candidate is null)
        {
            return false;
        }

        result = candidate;
        return true;
    }

    private static bool Unsupported(out ConversationTranscript? transcript)
    {
        transcript = null;
        return false;
    }

    private static void RemoveIfEmpty(TranscriptTurn turn)
    {
        if (turn.IsAttached && !turn.HasAnyContent && !turn.HasProtocolStructure && turn.Parent is not null)
        {
            turn.Remove();
        }
    }
}

internal sealed class TranscriptTurn
{
    public TranscriptTurn(
        JsonObject node,
        JsonArray? parent,
        int originalIndex,
        string role,
        List<TextSlot> textSlots,
        bool hasProtocolStructure,
        bool hasOpenToolUse,
        bool hasToolResult)
    {
        Node = node;
        Parent = parent;
        OriginalIndex = originalIndex;
        Role = role;
        TextSlots = textSlots;
        HasProtocolStructure = hasProtocolStructure;
        HasOpenToolUse = hasOpenToolUse;
        HasToolResult = hasToolResult;
    }

    public JsonObject Node { get; }

    public JsonArray? Parent { get; }

    public int OriginalIndex { get; }

    public string Role { get; }

    public List<TextSlot> TextSlots { get; }

    public bool HasProtocolStructure { get; }

    public bool HasOpenToolUse { get; }

    public bool HasToolResult { get; }

    public bool IsAttached => Parent is null || Node.Parent == Parent;

    public bool HasVisibleText => TextSlots.Any(slot => slot.IsAttached && !string.IsNullOrWhiteSpace(slot.Text));

    public bool HasAnyContent => Node["content"] switch
    {
        JsonValue value when value.TryGetValue(out string? text) => !string.IsNullOrWhiteSpace(text),
        JsonArray content => content.Count > 0,
        _ => false,
    };

    public bool HasProtectedTextSpan => TextSlots.Any(slot =>
        slot.IsAttached && TextScrubber.ContainsProtectedSpan(slot.Text));

    public bool HasProtectedTextMetadata => TextSlots.Any(slot => slot.IsAttached && slot.HasTextDependentMetadata);

    public bool CanRemoveAtomically => Parent is not null && !HasProtocolStructure && !HasProtectedTextSpan;

    public string CombinedText => string.Join('\n', TextSlots.Where(slot => slot.IsAttached).Select(slot => slot.Text));

    public void Remove()
    {
        Parent?.Remove(Node);
    }
}

internal sealed class TextSlot
{
    private readonly JsonObject owner;
    private readonly string propertyName;
    private readonly JsonArray? blockParent;

    public TextSlot(JsonObject owner, string propertyName, JsonArray? blockParent = null)
    {
        this.owner = owner;
        this.propertyName = propertyName;
        this.blockParent = blockParent;
    }

    public bool IsAttached => blockParent is null || owner.Parent == blockParent;

    public bool HasTextDependentMetadata =>
        owner.ContainsKey("annotations")
        || owner.ContainsKey("citations")
        || owner.ContainsKey("logprobs");

    public string Text => owner[propertyName] is JsonValue value && value.TryGetValue(out string? text)
        ? text ?? string.Empty
        : string.Empty;

    public void Set(string value)
    {
        owner[propertyName] = value;
    }

    public void Remove()
    {
        if (blockParent is not null)
        {
            blockParent.Remove(owner);
        }
        else
        {
            owner[propertyName] = string.Empty;
        }
    }
}

internal sealed record ScrubSummary(int EditCount, bool ProtectedMatchFound);

internal enum RemoveTurnResult
{
    NoTarget,
    Removed,
    Protected,
}
