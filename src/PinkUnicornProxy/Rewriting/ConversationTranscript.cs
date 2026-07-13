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
        string historyPropertyName,
        JsonArray? itemArray,
        List<TranscriptTurn> turns,
        bool isStateful)
    {
        Provider = provider;
        this.root = root;
        HistoryPropertyName = historyPropertyName;
        this.itemArray = itemArray;
        Turns = turns;
        IsStateful = isStateful;
    }

    public ProviderRequestKind Provider { get; }

    public string HistoryPropertyName { get; }

    public JsonNode HistoryNode => root[HistoryPropertyName]
        ?? throw new InvalidOperationException("The transcript history is no longer attached.");

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
        return Provider switch
        {
            ProviderRequestKind.OpenAIChatCompletions => HasActiveChatTransaction(),
            ProviderRequestKind.AnthropicMessages => HasActiveAnthropicTransaction(),
            ProviderRequestKind.OpenAIResponses => HasActiveOpenAIResponsesTransaction(),
            _ => false,
        };
    }

    private bool HasActiveChatTransaction()
    {
        if (itemArray is null)
        {
            return false;
        }

        HashSet<string> outstandingCalls = new(StringComparer.Ordinal);
        bool legacyFunctionCallIsOpen = false;
        bool awaitingAssistantAfterResult = false;
        bool malformed = false;
        foreach (JsonNode? node in itemArray)
        {
            if (node is not JsonObject message
                || !TryGetString(message, "role", out string role))
            {
                continue;
            }

            if (role == "assistant")
            {
                malformed |= outstandingCalls.Count > 0 || legacyFunctionCallIsOpen;
                if (outstandingCalls.Count == 0 && !legacyFunctionCallIsOpen)
                {
                    awaitingAssistantAfterResult = false;
                }

                if (message["tool_calls"] is JsonArray calls)
                {
                    foreach (JsonNode? callNode in calls)
                    {
                        if (callNode is not JsonObject call
                            || !TryGetString(call, "id", out string callId)
                            || !outstandingCalls.Add(callId))
                        {
                            malformed = true;
                        }
                    }
                }

                if (message["function_call"] is not null)
                {
                    malformed |= legacyFunctionCallIsOpen;
                    legacyFunctionCallIsOpen = true;
                }
            }
            else if (role == "tool")
            {
                if (!TryGetString(message, "tool_call_id", out string callId)
                    || !outstandingCalls.Remove(callId))
                {
                    malformed = true;
                }
                else
                {
                    awaitingAssistantAfterResult = true;
                }
            }
            else if (role == "function")
            {
                if (!legacyFunctionCallIsOpen)
                {
                    malformed = true;
                }

                legacyFunctionCallIsOpen = false;
                awaitingAssistantAfterResult = true;
            }
        }

        return malformed
            || legacyFunctionCallIsOpen
            || awaitingAssistantAfterResult
            || outstandingCalls.Count > 0;
    }

    private bool HasActiveAnthropicTransaction()
    {
        if (itemArray is null)
        {
            return false;
        }

        HashSet<string> outstandingClientUses = new(StringComparer.Ordinal);
        HashSet<string> outstandingServerUses = new(StringComparer.Ordinal);
        bool awaitingAssistantAfterResult = false;
        bool malformed = false;
        foreach (JsonNode? node in itemArray)
        {
            if (node is not JsonObject message
                || !TryGetString(message, "role", out string role))
            {
                continue;
            }

            if (role == "assistant")
            {
                malformed |= outstandingClientUses.Count > 0;
                if (outstandingClientUses.Count == 0)
                {
                    awaitingAssistantAfterResult = false;
                }
            }

            if (message["content"] is not JsonArray content)
            {
                continue;
            }

            foreach (JsonNode? blockNode in content)
            {
                if (blockNode is not JsonObject block
                    || !TryGetString(block, "type", out string type))
                {
                    continue;
                }

                if (type == "tool_use")
                {
                    if (!TryGetString(block, "id", out string useId)
                        || !outstandingClientUses.Add(useId))
                    {
                        malformed = true;
                    }
                }
                else if (type == "tool_result")
                {
                    if (!TryGetString(block, "tool_use_id", out string useId)
                        || !outstandingClientUses.Remove(useId))
                    {
                        malformed = true;
                    }
                    else
                    {
                        awaitingAssistantAfterResult = true;
                    }
                }
                else if (type.EndsWith("_tool_use", StringComparison.Ordinal))
                {
                    if (!TryGetString(block, "id", out string useId)
                        || !outstandingServerUses.Add(useId))
                    {
                        malformed = true;
                    }
                }
                else if (type.EndsWith("_tool_result", StringComparison.Ordinal))
                {
                    if (!TryGetString(block, "tool_use_id", out string useId)
                        || !outstandingServerUses.Remove(useId))
                    {
                        malformed = true;
                    }
                }
            }
        }

        return malformed
            || awaitingAssistantAfterResult
            || outstandingClientUses.Count > 0
            || outstandingServerUses.Count > 0;
    }

    private bool HasActiveOpenAIResponsesTransaction()
    {
        if (itemArray is null)
        {
            return false;
        }

        int latestAssistantIndex = Turns
            .Where(turn => turn.IsAttached && turn.Role == "assistant")
            .Select(turn => turn.OriginalIndex)
            .DefaultIfEmpty(-1)
            .Max();
        HashSet<string> outstandingCalls = new(StringComparer.Ordinal);
        HashSet<string> outstandingApprovals = new(StringComparer.Ordinal);
        bool awaitingAssistantAfterOutput = false;
        bool malformed = false;
        bool trailingReasoning = false;
        for (int index = 0; index < itemArray.Count; index++)
        {
            if (itemArray[index] is not JsonObject item)
            {
                continue;
            }

            if (TryGetString(item, "role", out string role) && role == "assistant")
            {
                malformed |= outstandingCalls.Count > 0 || outstandingApprovals.Count > 0;
                if (outstandingCalls.Count == 0 && outstandingApprovals.Count == 0)
                {
                    awaitingAssistantAfterOutput = false;
                }
            }

            if (!TryGetString(item, "type", out string type))
            {
                continue;
            }

            if (type == "reasoning" && index > latestAssistantIndex)
            {
                trailingReasoning = true;
                continue;
            }

            if (type == "mcp_approval_request")
            {
                if (!TryGetString(item, "id", out string approvalId)
                    || !outstandingApprovals.Add(approvalId))
                {
                    malformed = true;
                }

                continue;
            }

            if (type == "mcp_approval_response")
            {
                if (!TryGetString(item, "approval_request_id", out string approvalId)
                    || !outstandingApprovals.Remove(approvalId))
                {
                    malformed = true;
                }
                else
                {
                    awaitingAssistantAfterOutput = true;
                }

                continue;
            }

            if (type.EndsWith("_call_output", StringComparison.Ordinal))
            {
                if ((!TryGetString(item, "call_id", out string callId)
                        && !TryGetString(item, "id", out callId))
                    || !outstandingCalls.Remove(callId))
                {
                    malformed = true;
                }
                else
                {
                    awaitingAssistantAfterOutput = true;
                }

                continue;
            }

            if (!type.EndsWith("_call", StringComparison.Ordinal))
            {
                continue;
            }

            if (OpenAIClientManagedCallTypes.Contains(type))
            {
                if ((!TryGetString(item, "call_id", out string callId)
                        && !TryGetString(item, "id", out callId))
                    || !outstandingCalls.Add(callId))
                {
                    malformed = true;
                }

                continue;
            }

            if (!TryGetString(item, "status", out string status)
                || (status != "completed" && status != "failed"))
            {
                return true;
            }
        }

        return malformed
            || trailingReasoning
            || awaitingAssistantAfterOutput
            || outstandingCalls.Count > 0
            || outstandingApprovals.Count > 0;
    }

    public PlanningTargetSet CreatePlanningTargets(
        IReadOnlyList<string> triggerTokens,
        int targetIndexOffset = 0)
    {
        List<HistoryTextTarget> targets = [];
        HashSet<int> triggerTurnIndices = [];
        int triggerCount = 0;
        bool protectedTrigger = false;

        foreach (TranscriptTurn turn in Turns)
        {
            if (!turn.IsAttached || (turn.Role != "user" && turn.Role != "assistant"))
            {
                continue;
            }

            for (int slotIndex = 0; slotIndex < turn.TextSlots.Count; slotIndex++)
            {
                TextSlot slot = turn.TextSlots[slotIndex];
                if (!slot.IsAttached)
                {
                    continue;
                }

                bool containsTrigger = turn.Role == "user"
                    && ContainsAny(slot.Text, triggerTokens);
                if (containsTrigger)
                {
                    triggerCount++;
                    triggerTurnIndices.Add(turn.OriginalIndex);
                    protectedTrigger |= slot.HasTextDependentMetadata;
                }

                if (!slot.HasTextDependentMetadata)
                {
                    targets.Add(new HistoryTextTarget(
                        GetTargetId(turn, slotIndex, targetIndexOffset),
                        checked(turn.OriginalIndex + targetIndexOffset),
                        turn.Role,
                        containsTrigger));
                }
            }
        }

        return new PlanningTargetSet(
            targets,
            triggerTurnIndices.Order().ToArray(),
            triggerCount,
            protectedTrigger);
    }

    public ApplyHistoryEditResult ApplyPlan(
        HistoryEditPlan plan,
        IReadOnlyList<string> triggerTokens,
        int maximumEdits,
        int maximumReplacementCharacters,
        int targetIndexOffset = 0,
        bool requireTriggeredTargets = true,
        bool allowNoMaterialEdits = false)
    {
        if (plan.Edits.Count > maximumEdits)
        {
            return new ApplyHistoryEditResult(ApplyHistoryEditOutcome.EditLimitExceeded, 0, 0);
        }

        Dictionary<string, EditableTarget> targets = CreateEditableTargetMap(
            triggerTokens,
            targetIndexOffset);
        HashSet<string> editedIds = new(StringComparer.Ordinal);
        List<(EditableTarget Target, string? Replacement)> pending = [];
        int materialEdits = 0;

        foreach (HistoryEdit edit in plan.Edits)
        {
            if (!targets.TryGetValue(edit.Id, out EditableTarget? target)
                || !editedIds.Add(edit.Id)
                || (edit.Replacement is null && !target.Slot.CanRemoveSafely)
                || (edit.Replacement is not null
                    && (string.IsNullOrWhiteSpace(edit.Replacement)
                        || edit.Replacement.Length > maximumReplacementCharacters
                        || ContainsAny(edit.Replacement, triggerTokens)))
                || (target.ContainsTrigger && string.IsNullOrWhiteSpace(edit.Replacement)))
            {
                return new ApplyHistoryEditResult(ApplyHistoryEditOutcome.Unsafe, 0, 0);
            }

            pending.Add((target, edit.Replacement));
            materialEdits += string.Equals(
                target.Slot.Text,
                edit.Replacement,
                StringComparison.Ordinal) ? 0 : 1;
        }

        if ((!allowNoMaterialEdits && materialEdits == 0)
            || (requireTriggeredTargets
                && targets.Values.Any(target =>
                    target.ContainsTrigger && !editedIds.Contains(target.Id))))
        {
            return new ApplyHistoryEditResult(ApplyHistoryEditOutcome.Unsafe, 0, 0);
        }

        bool emptiesContentArray = pending
            .Where(item => item.Replacement is null)
            .GroupBy(item => item.Target.Slot.BlockParent)
            .Any(group =>
            {
                if (group.Key is null)
                {
                    return true;
                }

                HashSet<JsonNode> removed = group
                    .Select(item => (JsonNode)item.Target.Slot.Owner)
                    .ToHashSet();
                return !group.Key.Any(node =>
                    node is not null
                    && !removed.Contains(node)
                    && !IsOpaqueContentBlock(node));
            });
        if (emptiesContentArray)
        {
            return new ApplyHistoryEditResult(ApplyHistoryEditOutcome.Unsafe, 0, 0);
        }

        foreach ((EditableTarget target, string? replacement) in pending)
        {
            if (replacement is null)
            {
                target.Slot.Remove();
            }
            else
            {
                target.Slot.Set(replacement);
            }
        }

        bool triggerRemains = Turns
            .Where(turn => turn.IsAttached && turn.Role == "user")
            .SelectMany(turn => turn.TextSlots)
            .Any(slot => slot.IsAttached && ContainsAny(slot.Text, triggerTokens));
        return triggerRemains
            ? new ApplyHistoryEditResult(ApplyHistoryEditOutcome.Unsafe, 0, 0)
            : new ApplyHistoryEditResult(
                ApplyHistoryEditOutcome.Applied,
                materialEdits,
                plan.Edits.Count);
    }

    private Dictionary<string, EditableTarget> CreateEditableTargetMap(
        IReadOnlyList<string> triggerTokens,
        int targetIndexOffset)
    {
        Dictionary<string, EditableTarget> targets = new(StringComparer.Ordinal);
        foreach (TranscriptTurn turn in Turns)
        {
            if (!turn.IsAttached || (turn.Role != "user" && turn.Role != "assistant"))
            {
                continue;
            }

            for (int slotIndex = 0; slotIndex < turn.TextSlots.Count; slotIndex++)
            {
                TextSlot slot = turn.TextSlots[slotIndex];
                if (!slot.IsAttached || slot.HasTextDependentMetadata)
                {
                    continue;
                }

                string id = GetTargetId(turn, slotIndex, targetIndexOffset);
                targets.Add(id, new EditableTarget(
                    id,
                    turn,
                    slot,
                    turn.Role == "user" && ContainsAny(slot.Text, triggerTokens)));
            }
        }

        return targets;
    }

    private static string GetTargetId(
        TranscriptTurn turn,
        int slotIndex,
        int targetIndexOffset) =>
        $"t{checked(turn.OriginalIndex + targetIndexOffset)}.s{slotIndex}";

    private static bool ContainsAny(string text, IReadOnlyList<string> tokens) =>
        tokens.Any(token => text.Contains(token, StringComparison.Ordinal));

    private bool IsOpaqueContentBlock(JsonNode node)
    {
        return Provider == ProviderRequestKind.AnthropicMessages
            && node is JsonObject block
            && TryGetString(block, "type", out string type)
            && (type == "thinking" || type == "redacted_thinking");
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
                hasProtocolStructure));
        }

        transcript = new ConversationTranscript(
            ProviderRequestKind.OpenAIChatCompletions,
            root,
            "messages",
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
            TranscriptTurn turn = new(root, null, 0, "user", slots, false);
            transcript = new ConversationTranscript(
                ProviderRequestKind.OpenAIResponses,
                root,
                "input",
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
                hasNonTextBlocks));
        }

        transcript = new ConversationTranscript(
            ProviderRequestKind.OpenAIResponses,
            root,
            "input",
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
            turns.Add(new TranscriptTurn(
                message,
                messages,
                index,
                role,
                slots,
                hasProtocolStructure));
        }

        transcript = new ConversationTranscript(
            ProviderRequestKind.AnthropicMessages,
            root,
            "messages",
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
        bool hasProtocolStructure)
    {
        Node = node;
        Parent = parent;
        OriginalIndex = originalIndex;
        Role = role;
        TextSlots = textSlots;
        HasProtocolStructure = hasProtocolStructure;
    }

    public JsonObject Node { get; }

    public JsonArray? Parent { get; }

    public int OriginalIndex { get; }

    public string Role { get; }

    public List<TextSlot> TextSlots { get; }

    public bool HasProtocolStructure { get; }

    public bool IsAttached => Parent is null || Node.Parent == Parent;

    public bool HasVisibleText => TextSlots.Any(slot => slot.IsAttached && !string.IsNullOrWhiteSpace(slot.Text));

    public bool HasAnyContent => Node["content"] switch
    {
        JsonValue value when value.TryGetValue(out string? text) => !string.IsNullOrWhiteSpace(text),
        JsonArray content => content.Count > 0,
        _ => false,
    };

    public bool HasProtectedTextMetadata => TextSlots.Any(slot => slot.IsAttached && slot.HasTextDependentMetadata);

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

    public bool CanRemoveSafely => blockParent is { Count: > 1 };

    public JsonArray? BlockParent => blockParent;

    public JsonObject Owner => owner;

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

internal sealed record PlanningTargetSet(
    IReadOnlyList<HistoryTextTarget> Targets,
    IReadOnlyList<int> TriggerTurnIndices,
    int TriggerCount,
    bool HasProtectedTrigger);

internal enum ApplyHistoryEditOutcome
{
    Applied,
    Unsafe,
    EditLimitExceeded,
}

internal sealed record ApplyHistoryEditResult(
    ApplyHistoryEditOutcome Outcome,
    int EditCount,
    int OperationCount);

internal sealed record EditableTarget(
    string Id,
    TranscriptTurn Turn,
    TextSlot Slot,
    bool ContainsTrigger);
