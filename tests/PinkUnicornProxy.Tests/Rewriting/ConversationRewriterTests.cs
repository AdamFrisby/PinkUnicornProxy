using System.Text;
using System.Text.Json.Nodes;
using PinkUnicornProxy.Configuration;
using PinkUnicornProxy.Rewriting;

namespace PinkUnicornProxy.Tests.Rewriting;

public sealed class ConversationRewriterTests
{
    [Fact]
    public void PreservesNoOpBodyByteForByte()
    {
        const string json = "{ \"model\" : \"gpt-test\", \"unknown\":42, \"messages\" : [ { \"role\":\"user\", \"content\":\"Help me investigate.\" } ] }";

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);

        Assert.Equal(RewriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void RewritesOpenAIChatCorrectionAndKeepsUnrelatedText()
    {
        const string json = """
            {
              "model": "gpt-test",
              "unknown": { "preserve": true },
              "messages": [
                { "role": "system", "content": "DNS is an allowed topic." },
                { "role": "user", "content": "Why is checkout failing?" },
                { "role": "assistant", "content": "The database is healthy. The cause is DNS." },
                { "role": "user", "content": "No, it's not DNS. It's an expired TLS certificate." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);
        JsonObject root = Parse(result);
        JsonArray messages = Assert.IsType<JsonArray>(root["messages"]);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.True(root["unknown"]?["preserve"]?.GetValue<bool>());
        Assert.Equal("DNS is an allowed topic.", messages[0]?["content"]?.GetValue<string>());
        Assert.Equal("The database is healthy.", messages[2]?["content"]?.GetValue<string>());
        Assert.Equal("It is an expired TLS certificate.", messages[3]?["content"]?.GetValue<string>());
        Assert.DoesNotContain("not DNS", Encoding.UTF8.GetString(result.Body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeicticCorrectionRemovesPreviousAssistantTurn()
    {
        const string json = """
            {
              "messages": [
                { "role": "user", "content": "Find the fault." },
                { "role": "assistant", "content": "The queue is full." },
                { "role": "user", "content": "No, it's not that. It's a race condition." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);
        JsonArray messages = Assert.IsType<JsonArray>(Parse(result)["messages"]);

        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0]?["role"]?.GetValue<string>());
        Assert.Equal("It is a race condition.", messages[1]?["content"]?.GetValue<string>());
    }

    [Fact]
    public void RejectionWithoutReplacementUsesNeutralPositivePrompt()
    {
        const string json = """
            {
              "messages": [
                { "role": "user", "content": "Find the fault." },
                { "role": "assistant", "content": "It is DNS." },
                { "role": "user", "content": "No, it is not DNS." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.DoesNotContain("DNS", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reassess the issue using the remaining evidence.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void ReappliesHistoricalCorrectionAndRemovesLaterAssistantRelapse()
    {
        const string json = """
            {
              "messages": [
                { "role": "user", "content": "Find the fault." },
                { "role": "assistant", "content": "DNS is the cause." },
                { "role": "user", "content": "No, it is not DNS. It is an expired certificate." },
                { "role": "assistant", "content": "DNS still seems plausible. I will inspect the certificate." },
                { "role": "user", "content": "What should I do next?" }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.DoesNotContain("DNS", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("It is an expired certificate.", rewritten, StringComparison.Ordinal);
        Assert.Contains("I will inspect the certificate.", rewritten, StringComparison.Ordinal);
        Assert.Contains("What should I do next?", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void AppliesMultipleCorrectionsChronologically()
    {
        const string json = """
            {
              "messages": [
                { "role": "assistant", "content": "The cause is DNS." },
                { "role": "user", "content": "No, it is not DNS. It is the cache." },
                { "role": "assistant", "content": "The cache is exhausted." },
                { "role": "user", "content": "No, it is not the cache. It is a race condition." },
                { "role": "user", "content": "Continue." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(2, result.CorrectionCount);
        Assert.DoesNotContain("DNS", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the cache", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("It is a race condition.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesImagesWhenRewritingTextBlocks()
    {
        const string json = """
            {
              "messages": [
                { "role": "assistant", "content": "It is DNS." },
                {
                  "role": "user",
                  "content": [
                    { "type": "text", "text": "No, it is not DNS. It is a certificate error." },
                    { "type": "image_url", "image_url": { "url": "data:image/png;base64,AA==" } }
                  ]
                }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);
        JsonArray messages = Assert.IsType<JsonArray>(Parse(result)["messages"]);
        JsonArray content = Assert.IsType<JsonArray>(messages[^1]?["content"]);

        Assert.Equal(2, content.Count);
        Assert.Equal("It is a certificate error.", content[0]?["text"]?.GetValue<string>());
        Assert.Equal("image_url", content[1]?["type"]?.GetValue<string>());
    }

    [Fact]
    public void RemovesAnthropicThinkingAndRedactedThinkingAfterRewrite()
    {
        const string json = """
            {
              "model": "claude-test",
              "max_tokens": 100,
              "messages": [
                { "role": "user", "content": "Find the fault." },
                {
                  "role": "assistant",
                  "content": [
                    { "type": "thinking", "thinking": "Maybe DNS", "signature": "signed-secret" },
                    { "type": "redacted_thinking", "data": "encrypted-secret" },
                    { "type": "text", "text": "The cause is DNS." }
                  ]
                },
                { "role": "user", "content": "No, it is not DNS. It is the certificate." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.AnthropicMessages, json);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(2, result.OpaqueBlockCount);
        Assert.DoesNotContain("thinking", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-secret", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted-secret", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("DNS", rewritten, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreservesAnthropicThinkingByteForByteWithoutCorrection()
    {
        const string json = "{ \"messages\": [{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"x\",\"signature\":\"sig\"},{\"type\":\"text\",\"text\":\"Done\"}]}] }";

        RewriteResult result = Rewrite(ProviderRequestKind.AnthropicMessages, json);

        Assert.Equal(RewriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void DefersAnthropicRewriteDuringOpenToolTurn()
    {
        const string json = """
            {
              "messages": [
                { "role": "user", "content": "Investigate." },
                {
                  "role": "assistant",
                  "content": [
                    { "type": "thinking", "thinking": "x", "signature": "sig" },
                    { "type": "tool_use", "id": "toolu_1", "name": "inspect", "input": {} }
                  ]
                },
                { "role": "user", "content": "No, it is not DNS. It is TLS." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.AnthropicMessages, json);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void DefersHistoricalCorrectionWhenALaterAnthropicToolTurnIsActive()
    {
        const string json = """
            {
              "messages": [
                { "role": "assistant", "content": "It is DNS." },
                { "role": "user", "content": "No, it is not DNS. It is TLS." },
                { "role": "assistant", "content": [
                  { "type": "thinking", "thinking": "continue", "signature": "signed" },
                  { "type": "tool_use", "id": "toolu_1", "name": "inspect", "input": {} }
                ] },
                { "role": "user", "content": [
                  { "type": "tool_result", "tool_use_id": "toolu_1", "content": "result" }
                ] }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.AnthropicMessages, json);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void CompletedAnthropicToolTurnCanBeRewritten()
    {
        const string json = """
            {
              "messages": [
                { "role": "assistant", "content": [
                  { "type": "thinking", "thinking": "inspect", "signature": "signed" },
                  { "type": "tool_use", "id": "toolu_1", "name": "inspect", "input": {} }
                ] },
                { "role": "user", "content": [
                  { "type": "tool_result", "tool_use_id": "toolu_1", "content": "result" }
                ] },
                { "role": "assistant", "content": "It is DNS." },
                { "role": "user", "content": "No, it is not DNS. It is TLS." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.AnthropicMessages, json);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.DoesNotContain("signed", rewritten, StringComparison.Ordinal);
        Assert.Contains("toolu_1", rewritten, StringComparison.Ordinal);
        Assert.Contains("It is TLS.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrectionBesideAnthropicToolResultIsDetectedAndDeferred()
    {
        const string json = """
            {
              "messages": [
                { "role": "assistant", "content": [
                  { "type": "thinking", "thinking": "inspect", "signature": "signed" },
                  { "type": "tool_use", "id": "toolu_1", "name": "inspect", "input": {} }
                ] },
                { "role": "user", "content": [
                  { "type": "tool_result", "tool_use_id": "toolu_1", "content": "result" },
                  { "type": "text", "text": "No, it is not DNS. It is TLS." }
                ] }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.AnthropicMessages, json);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(1, result.CorrectionCount);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void CompletedAnthropicServerToolResponseCanBeRewritten()
    {
        const string json = """
            {
              "messages": [
                { "role": "assistant", "content": [
                  { "type": "thinking", "thinking": "inspect", "signature": "signed" },
                  { "type": "server_tool_use", "id": "srv_1", "name": "web_search", "input": { "query": "certificate failure" } },
                  { "type": "web_search_tool_result", "tool_use_id": "srv_1", "content": [{ "type": "web_search_result", "encrypted_content": "opaque" }] },
                  { "type": "text", "text": "It is DNS." }
                ] },
                { "role": "user", "content": "No, it is not DNS. It is TLS." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.AnthropicMessages, json);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.DoesNotContain("signed", rewritten, StringComparison.Ordinal);
        Assert.Contains("srv_1", rewritten, StringComparison.Ordinal);
        Assert.Contains("opaque", rewritten, StringComparison.Ordinal);
        Assert.Contains("It is TLS.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedAnthropicServerToolResponseIsDeferred()
    {
        const string json = """
            {
              "messages": [
                { "role": "user", "content": "No, it is not DNS. It is TLS." },
                { "role": "assistant", "content": [
                  { "type": "server_tool_use", "id": "srv_1", "name": "web_search", "input": { "query": "certificate failure" } }
                ] }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.AnthropicMessages, json);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void RemovesOpenAIReasoningAndCompactionItemsAfterRewrite()
    {
        const string json = """
            {
              "model": "gpt-test",
              "input": [
                { "role": "user", "content": "Find the fault." },
                { "type": "reasoning", "id": "rs_1", "encrypted_content": "opaque-reasoning", "summary": [] },
                { "type": "compaction", "id": "cmp_1", "encrypted_content": "opaque-summary" },
                { "role": "assistant", "content": [{ "type": "output_text", "text": "It is DNS." }] },
                { "role": "user", "content": [{ "type": "input_text", "text": "No, it is not DNS. It is TLS." }] }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIResponses, json);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(2, result.OpaqueBlockCount);
        Assert.DoesNotContain("opaque-reasoning", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-summary", rewritten, StringComparison.Ordinal);
        Assert.Contains("It is TLS.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void DefersHistoricalCorrectionDuringActiveOpenAIResponsesToolContinuation()
    {
        const string json = """
            {
              "input": [
                { "role": "assistant", "content": "It is DNS." },
                { "role": "user", "content": "No, it is not DNS. It is TLS." },
                { "type": "reasoning", "id": "rs_1", "encrypted_content": "opaque" },
                { "type": "function_call", "id": "fc_1", "call_id": "call_1", "name": "inspect", "arguments": "{}" },
                { "type": "function_call_output", "call_id": "call_1", "output": "result" }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIResponses, json);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void CompletedOpenAIHostedToolCallDoesNotDeferForever()
    {
        const string json = """
            {
              "input": [
                { "role": "assistant", "content": "It is DNS." },
                { "role": "user", "content": "No, it is not DNS. It is TLS." },
                { "type": "web_search_call", "id": "ws_1", "status": "completed", "action": { "type": "search", "query": "certificate failure" } },
                { "role": "user", "content": "Continue." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIResponses, json);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Contains("ws_1", rewritten, StringComparison.Ordinal);
        Assert.Contains("It is TLS.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void InProgressOpenAIHostedToolCallIsDeferred()
    {
        const string json = """
            {
              "input": [
                { "role": "user", "content": "No, it is not DNS. It is TLS." },
                { "type": "image_generation_call", "id": "img_1", "status": "in_progress" }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIResponses, json);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void CompletedClientManagedOpenAIFunctionCallStillRequiresOutput()
    {
        const string json = """
            {
              "input": [
                { "role": "user", "content": "No, it is not DNS. It is TLS." },
                { "type": "function_call", "id": "fc_1", "call_id": "call_1", "status": "completed", "name": "inspect", "arguments": "{}" }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIResponses, json);

        Assert.Equal(RewriteOutcome.DeferredOpenToolTurn, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void RejectPolicyIdentifiesProviderManagedResponsesHistory()
    {
        const string json = """
            {
              "model": "gpt-test",
              "previous_response_id": "resp_123",
              "input": "No, it is not DNS. It is TLS."
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIResponses, json);

        Assert.Equal(RewriteOutcome.UnsupportedStatefulContext, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void FreshStartPolicyDropsProviderManagedResponsesHistory()
    {
        const string json = """
            {
              "model": "gpt-test",
              "previous_response_id": "resp_123",
              "conversation": "conv_123",
              "input": "No, it is not DNS. It is TLS."
            }
            """;

        PinkUnicornOptions options = DefaultOptions();
        options.StatefulResponsesPolicy = StatefulResponsesPolicy.FreshStart;
        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIResponses, json, options);
        JsonObject root = Parse(result);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.False(root.ContainsKey("previous_response_id"));
        Assert.False(root.ContainsKey("conversation"));
        Assert.Equal("It is TLS.", root["input"]?.GetValue<string>());
    }

    [Fact]
    public void AuditModeDetectsWithoutChangingBytes()
    {
        const string json = """
            { "messages": [
              { "role": "assistant", "content": "It is DNS." },
              { "role": "user", "content": "No, it is not DNS. It is TLS." }
            ] }
            """;
        PinkUnicornOptions options = DefaultOptions();
        options.Mode = ProxyMode.Audit;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json, options);

        Assert.Equal(RewriteOutcome.AuditMatch, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void MalformedJsonPassesThroughForProviderToValidate()
    {
        const string json = "{ not-json";

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);

        Assert.Equal(RewriteOutcome.InvalidJson, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void ProtectedCodeOccurrenceFailsWithoutChangingBytes()
    {
        const string json = """
            {
              "messages": [
                { "role": "assistant", "content": "```text\nDNS\n```" },
                { "role": "user", "content": "No, it is not DNS. It is TLS." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);

        Assert.Equal(RewriteOutcome.ProtectedContentConflict, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void DeicticCorrectionCannotDeleteTildeFencedCodeTurn()
    {
        const string json = """
            {
              "messages": [
                { "role": "assistant", "content": "~~~text\nimportant output\n~~~" },
                { "role": "user", "content": "No, it is not that. It is TLS." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);

        Assert.Equal(RewriteOutcome.ProtectedContentConflict, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Theory]
    [InlineData("Run `dig example.test` before continuing.")]
    [InlineData("Read https://example.test/runbook before continuing.")]
    public void DeicticCorrectionCannotDeleteInlineCodeOrUrlTurn(string assistantText)
    {
        JsonObject request = new()
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = assistantText,
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "No, it is not that. It is TLS.",
                },
            },
        };
        string json = request.ToJsonString();

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);

        Assert.Equal(RewriteOutcome.ProtectedContentConflict, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    [Fact]
    public void SpecificUnanchoredClaimDoesNotDeleteUnrelatedAssistantText()
    {
        const string json = """
            {
              "messages": [
                { "role": "assistant", "content": "Check the logs." },
                { "role": "user", "content": "No, it is not DNS. It is TLS." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIChatCompletions, json);
        string rewritten = Encoding.UTF8.GetString(result.Body);

        Assert.Equal(RewriteOutcome.Rewritten, result.Outcome);
        Assert.Contains("Check the logs.", rewritten, StringComparison.Ordinal);
        Assert.Contains("It is TLS.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void TextDependentAnnotationsCauseProtectedContentConflict()
    {
        const string json = """
            {
              "input": [
                { "role": "assistant", "content": [
                  { "type": "output_text", "text": "It is DNS.", "annotations": [{ "type": "url_citation", "start_index": 6, "end_index": 9 }] }
                ] },
                { "role": "user", "content": "No, it is not DNS. It is TLS." }
              ]
            }
            """;

        RewriteResult result = Rewrite(ProviderRequestKind.OpenAIResponses, json);

        Assert.Equal(RewriteOutcome.ProtectedContentConflict, result.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Body);
    }

    private static RewriteResult Rewrite(
        ProviderRequestKind provider,
        string json,
        PinkUnicornOptions? options = null)
    {
        ConversationRewriter rewriter = new(options ?? DefaultOptions());
        return rewriter.Rewrite(provider, Encoding.UTF8.GetBytes(json));
    }

    private static PinkUnicornOptions DefaultOptions() => new()
    {
        Mode = ProxyMode.Rewrite,
        MaximumCorrectionTextCharacters = 16_384,
        MaximumCorrectionsPerRequest = 32,
        StatefulResponsesPolicy = StatefulResponsesPolicy.Reject,
    };

    private static JsonObject Parse(RewriteResult result) =>
        Assert.IsType<JsonObject>(JsonNode.Parse(result.Body));
}
