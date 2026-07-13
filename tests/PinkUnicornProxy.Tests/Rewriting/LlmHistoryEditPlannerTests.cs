using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinkUnicornProxy.Configuration;
using PinkUnicornProxy.Rewriting;

namespace PinkUnicornProxy.Tests.Rewriting;

public sealed class LlmHistoryEditPlannerTests
{
    [Fact]
    public async Task OpenAICompatibleProtocolUsesConfiguredModelAndBearerCredential()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
            {"choices":[{"message":{"role":"assistant","content":"{\"edits\":[{\"id\":\"t0.s0\",\"replacement\":\"It is TLS.\"}]}"}}]}
            """));
        PinkUnicornOptions options = OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions);
        options.HistoryRewrite.ApiKey = "rewriter-secret";
        LlmHistoryEditPlanner planner = CreatePlanner(handler, options);

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            Request(),
            CancellationToken.None);

        HistoryEdit edit = Assert.Single(Assert.IsType<HistoryEditPlan>(result.Plan).Edits);
        Assert.Equal("It is TLS.", edit.Replacement);
        Assert.Equal(new Uri("https://rewriter.example.test/v1/chat/completions"), handler.Uri);
        Assert.Equal("Bearer rewriter-secret", handler.Authorization);
        Assert.Equal("1", handler.GetHeader(HistoryRewriteOptions.InternalRequestHeaderName));
        JsonObject request = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.IsType<string>(handler.Body)));
        Assert.Equal("small-model", request["model"]?.GetValue<string>());
        Assert.Equal(4_096, request["max_completion_tokens"]?.GetValue<int>());
        JsonArray messages = Assert.IsType<JsonArray>(request["messages"]);
        Assert.Equal("system", messages[0]?["role"]?.GetValue<string>());
        string planningInput = messages[1]?["content"]?.GetValue<string>() ?? string.Empty;
        Assert.Contains("earlier false premise", planningInput, StringComparison.Ordinal);
        Assert.Contains("!!NO!!", planningInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicProtocolUsesMessagesShapeAndVendorCredential()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
            {"content":[{"type":"text","text":"{\"edits\":[{\"id\":\"t0.s0\",\"replacement\":\"It is TLS.\"}]}"}]}
            """));
        PinkUnicornOptions options = OptionsFor(HistoryRewriterProtocol.AnthropicMessages);
        options.HistoryRewrite.Endpoint = "https://rewriter.example.test/v1/messages";
        options.HistoryRewrite.ApiKey = "anthropic-rewriter-secret";
        options.HistoryRewrite.AnthropicVersion = "2023-06-01";
        LlmHistoryEditPlanner planner = CreatePlanner(handler, options);

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            Request(),
            CancellationToken.None);

        Assert.NotNull(result.Plan);
        Assert.Equal("anthropic-rewriter-secret", handler.GetHeader("x-api-key"));
        Assert.Equal("2023-06-01", handler.GetHeader("anthropic-version"));
        JsonObject request = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.IsType<string>(handler.Body)));
        Assert.Equal(4_096, request["max_tokens"]?.GetValue<int>());
        Assert.NotNull(request["system"]);
    }

    [Fact]
    public async Task ConfiguredHeadersCanSupportCustomOrLocalGateways()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
            {"choices":[{"message":{"content":"{\"edits\":[]}"}}]}
            """));
        PinkUnicornOptions options = OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions);
        options.HistoryRewrite.Headers["X-Gateway-Key"] = "gateway-secret";
        LlmHistoryEditPlanner planner = CreatePlanner(handler, options);

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            Request(),
            CancellationToken.None);

        Assert.NotNull(result.Plan);
        Assert.Equal("gateway-secret", handler.GetHeader("X-Gateway-Key"));
    }

    [Fact]
    public async Task ContinuationPlanningSeparatesOriginalContextImmutablePrefixAndEditableSuffix()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
            {"choices":[{"message":{"content":"{\"edits\":[]}"}}]}
            """));
        LlmHistoryEditPlanner planner = CreatePlanner(
            handler,
            OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions));
        HistoryEditRequest request = new(
            ProviderRequestKind.OpenAIChatCompletions,
            "[{\"role\":\"assistant\",\"content\":\"It is DNS.\"},{\"role\":\"user\",\"content\":\"!!NO!! It is TLS.\"},{\"role\":\"assistant\",\"content\":\"DNS again.\"}]",
            [new HistoryTextTarget("t2.s0", 2, "assistant", false)],
            ["!!NO!!"],
            HistoryEditPlanningMode.Continuation,
            "[{\"role\":\"assistant\",\"content\":\"It is TLS.\"},{\"role\":\"user\",\"content\":\"It is TLS.\"}]",
            "[{\"role\":\"assistant\",\"content\":\"DNS again.\"}]");

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            request,
            CancellationToken.None);

        Assert.Empty(Assert.IsType<HistoryEditPlan>(result.Plan).Edits);
        JsonObject outer = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.IsType<string>(handler.Body)));
        JsonArray messages = Assert.IsType<JsonArray>(outer["messages"]);
        string planningJson = Assert.IsAssignableFrom<JsonValue>(
            messages[1]?["content"]).GetValue<string>();
        JsonObject planning = Assert.IsType<JsonObject>(JsonNode.Parse(planningJson));
        Assert.Equal("continuation", planning["task"]?.GetValue<string>());
        Assert.NotNull(planning["original_history"]);
        Assert.NotNull(planning["authoritative_rewritten_prefix"]);
        Assert.NotNull(planning["editable_suffix"]);
        Assert.Null(planning["history"]);
        Assert.Equal("t2.s0", planning["editable_text"]?[0]?["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task MalformedModelOutputIsRejected()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
            {"choices":[{"message":{"content":"I cannot provide JSON."}}]}
            """));
        LlmHistoryEditPlanner planner = CreatePlanner(
            handler,
            OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions));

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            Request(),
            CancellationToken.None);

        Assert.Null(result.Plan);
        Assert.Equal(HistoryEditPlannerFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task SurroundingProseWithEmbeddedPlanIsRejected()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
            {"choices":[{"message":{"content":"Example only: {\"edits\":[{\"id\":\"t0.s0\",\"replacement\":\"It is TLS.\"}]}"}}]}
            """));
        LlmHistoryEditPlanner planner = CreatePlanner(
            handler,
            OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions));

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(HistoryEditPlannerFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task DeepProviderHistoryUsesConfiguredDepthThroughoutPlanning()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
            {"choices":[{"message":{"content":"{\"edits\":[{\"id\":\"t0.s0\",\"replacement\":\"It is TLS.\"}]}"}}]}
            """));
        LlmHistoryEditPlanner planner = CreatePlanner(
            handler,
            OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions));
        string nested = "0";
        for (int index = 0; index < 80; index++)
        {
            nested = $"{{\"level\":{nested}}}";
        }

        HistoryEditRequest request = Request() with
        {
            HistoryJson = $"[{{\"role\":\"user\",\"content\":\"!!NO!! It is TLS.\",\"metadata\":{nested}}}]",
        };

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            request,
            CancellationToken.None);

        Assert.NotNull(result.Plan);
    }

    [Fact]
    public async Task PlannerRequestOverConfiguredByteLimitFailsBeforeNetworkCall()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("Network should not run."));
        PinkUnicornOptions options = OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions);
        options.HistoryRewrite.MaximumPlannerRequestBytes = 16 * 1024;
        LlmHistoryEditPlanner planner = CreatePlanner(handler, options);
        HistoryEditRequest request = Request() with
        {
            HistoryJson = $"[{{\"role\":\"user\",\"content\":\"!!NO!! {new string('x', 20_000)}\"}}]",
        };

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal(HistoryEditPlannerFailure.InputTooLarge, result.Failure);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task OversizedRewriterResponseIsUnavailable()
    {
        RecordingHandler handler = new(_ => JsonResponse(new string('x', 2_000)));
        PinkUnicornOptions options = OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions);
        options.HistoryRewrite.MaximumResponseBodyBytes = 1_024;
        LlmHistoryEditPlanner planner = CreatePlanner(handler, options);

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(HistoryEditPlannerFailure.Unavailable, result.Failure);
    }

    [Fact]
    public async Task InterruptedRewriterResponseBodyIsUnavailable()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingReadStream()),
        });
        LlmHistoryEditPlanner planner = CreatePlanner(
            handler,
            OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions));

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(HistoryEditPlannerFailure.Unavailable, result.Failure);
    }

    [Fact]
    public async Task NonSuccessResponseIsUnavailableAndDoesNotParseErrorBody()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("secret provider error", Encoding.UTF8, "text/plain"),
        });
        LlmHistoryEditPlanner planner = CreatePlanner(
            handler,
            OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions));

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            Request(),
            CancellationToken.None);

        Assert.Null(result.Plan);
        Assert.Equal(HistoryEditPlannerFailure.Unavailable, result.Failure);
    }

    [Fact]
    public async Task MissingEndpointIsReportedWithoutNetworkCall()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("Network should not run."));
        PinkUnicornOptions options = OptionsFor(HistoryRewriterProtocol.OpenAIChatCompletions);
        options.HistoryRewrite.Endpoint = null;
        LlmHistoryEditPlanner planner = CreatePlanner(handler, options);

        HistoryEditPlannerResult result = await planner.CreatePlanAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(HistoryEditPlannerFailure.NotConfigured, result.Failure);
        Assert.Equal(0, handler.CallCount);
    }

    private static LlmHistoryEditPlanner CreatePlanner(
        RecordingHandler handler,
        PinkUnicornOptions options)
    {
        return new LlmHistoryEditPlanner(
            new SingleClientFactory(new HttpClient(handler, disposeHandler: false)),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<LlmHistoryEditPlanner>.Instance);
    }

    private static PinkUnicornOptions OptionsFor(HistoryRewriterProtocol protocol) => new()
    {
        HistoryRewrite = new HistoryRewriteOptions
        {
            Protocol = protocol,
            Endpoint = "https://rewriter.example.test/v1/chat/completions",
            Model = "small-model",
            MaximumOutputTokens = 4_096,
            MaximumResponseBodyBytes = 1024 * 1024,
            Timeout = TimeSpan.FromSeconds(5),
        },
    };

    private static HistoryEditRequest Request() => new(
        ProviderRequestKind.OpenAIChatCompletions,
        "[{\"role\":\"assistant\",\"content\":\"earlier false premise\"},{\"role\":\"user\",\"content\":\"!!NO!! It is TLS.\"}]",
        [new HistoryTextTarget("t0.s0", 0, "user", true)],
        ["!!NO!!"]);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient client;

        public SingleClientFactory(HttpClient client)
        {
            this.client = client;
        }

        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responseFactory;
        private readonly Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            this.responseFactory = responseFactory;
        }

        public int CallCount { get; private set; }

        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        public string? GetHeader(string name) =>
            headers.TryGetValue(name, out string[]? values) ? Assert.Single(values) : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                headers[name] = values.ToArray();
            }

            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return responseFactory(request);
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("Synthetic interrupted response.");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Synthetic interrupted response."));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
