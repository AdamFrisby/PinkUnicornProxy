using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinkUnicornProxy.Configuration;
using PinkUnicornProxy.Rewriting;

namespace PinkUnicornProxy.Tests.Proxy;

public sealed class ProviderProxyIntegrationTests
{
    [Fact]
    public async Task TriggeredOpenAIRequestRewritesHistoryAndPreservesEndToEndHeaders()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "It is TLS."),
            new HistoryEdit("t1.s0", "It is TLS.")));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();
        const string requestJson = """
            {
              "model": "gpt-test",
              "messages": [
                { "role": "assistant", "content": "It is DNS." },
                { "role": "user", "content": "!!NO!! It is TLS." }
              ]
            }
            """;
        using HttpRequestMessage request = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "provider-secret");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
        request.Headers.TryAddWithoutValidation("X-Pink-Unicorn-Debug", "preserve-me");
        request.Headers.TryAddWithoutValidation("Cookie", "session=preserve-me");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Uri("https://api.openai.com/v1/chat/completions"), upstream.RequestUri);
        Assert.Equal("Bearer provider-secret", upstream.Authorization);
        Assert.Equal("203.0.113.10", upstream.GetHeader("X-Forwarded-For"));
        Assert.Equal("preserve-me", upstream.GetHeader("X-Pink-Unicorn-Debug"));
        Assert.Equal("session=preserve-me", upstream.GetHeader("Cookie"));
        Assert.DoesNotContain(response.Headers, header =>
            header.Key.StartsWith("X-Pink-Unicorn-", StringComparison.OrdinalIgnoreCase));
        string forwarded = Encoding.UTF8.GetString(Assert.IsType<byte[]>(upstream.Body));
        Assert.DoesNotContain("!!NO!!", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("DNS", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("It is TLS.", forwarded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TriggeredAnthropicRequestForwardsVendorHeadersAndDropsOpaqueThinking()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => Success(
            new HistoryEdit("t0.s0", "It is TLS."),
            new HistoryEdit("t1.s0", "It is TLS.")));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();
        const string requestJson = """
            {
              "model": "claude-test",
              "max_tokens": 100,
              "messages": [
                { "role": "assistant", "content": [
                  { "type": "thinking", "thinking": "DNS", "signature": "opaque-signature" },
                  { "type": "redacted_thinking", "data": "opaque-data" },
                  { "type": "text", "text": "It is DNS." }
                ] },
                { "role": "user", "content": "!!NO!! It is TLS." }
              ]
            }
            """;
        using HttpRequestMessage request = new(HttpMethod.Post, "/anthropic/v1/messages")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", "provider-anthropic-secret");
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Uri("https://api.anthropic.com/v1/messages"), upstream.RequestUri);
        Assert.Equal("provider-anthropic-secret", upstream.GetHeader("x-api-key"));
        Assert.Equal("2023-06-01", upstream.GetHeader("anthropic-version"));
        string forwarded = Encoding.UTF8.GetString(Assert.IsType<byte[]>(upstream.Body));
        Assert.DoesNotContain("opaque-signature", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-data", forwarded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CachePublicationFailureReturns503WithoutCallingPrimaryUpstream()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PinkUnicornProxy.Tests",
            $"integration-publication-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "cache.db");
        try
        {
            RecordingHandler upstream = new();
            FakePlanner planner = new(_ =>
            {
                File.Delete(databasePath);
                return Success(new HistoryEdit("t0.s0", "It is TLS."));
            });
            Dictionary<string, string?> configuration = new()
            {
                ["PinkUnicorn:HistoryRewrite:Cache:DatabasePath"] = databasePath,
            };
            await using WebApplicationFactory<Program> factory = CreateFactory(
                upstream,
                planner,
                configuration);
            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.PostAsync(
                "/openai/v1/chat/completions",
                new StringContent(
                    "{\"messages\":[{\"role\":\"user\",\"content\":\"!!NO!! It is TLS.\"}]}",
                    Encoding.UTF8,
                    "application/json"),
                CancellationToken.None);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains(
                "history-rewrite-cache-unavailable",
                await response.Content.ReadAsStringAsync(CancellationToken.None),
                StringComparison.Ordinal);
            Assert.Equal(0, upstream.CallCount);
            Assert.Equal(1, planner.CallCount);
            using HttpResponseMessage readiness = await client.GetAsync(
                "/health/ready",
                CancellationToken.None);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NoTriggerPreservesBodyAndHeadersAndNeverCallsPlanner()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();
        const string requestJson = "{ \"model\":\"gpt-test\", \"unknown\": 7, \"messages\": [ {\"role\":\"user\",\"content\":\"Hello\"} ] }";
        using HttpRequestMessage request = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Custom-End-To-End", "preserved");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(requestJson), upstream.Body);
        Assert.Equal("preserved", upstream.GetHeader("X-Custom-End-To-End"));
        Assert.Null(upstream.GetHeader("X-Forwarded-For"));
        Assert.Null(upstream.GetHeader("X-Forwarded-Host"));
        Assert.Null(upstream.GetHeader("X-Forwarded-Proto"));
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task NaturalLanguageCorrectionWithoutTokenPassesThroughUnchanged()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();
        const string requestJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"No, it is not DNS. It is TLS.\"}]}";

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(requestJson, Encoding.UTF8, "application/json"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(requestJson), upstream.Body);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task DuplicateJsonIsTransparentUntilACompetingHistoryContainsATrigger()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();
        const string unmarked = "{\"metadata\":{\"x\":1,\"x\":2},\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}";
        const string marked = "{\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}],\"messages\":[{\"role\":\"user\",\"content\":\"!!NO!! It is TLS.\"}]}";

        using HttpResponseMessage unmarkedResponse = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(unmarked, Encoding.UTF8, "application/json"),
            CancellationToken.None);
        using HttpResponseMessage markedResponse = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(marked, Encoding.UTF8, "application/json"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, unmarkedResponse.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(unmarked), upstream.Bodies[0]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, markedResponse.StatusCode);
        Assert.Equal(1, upstream.CallCount);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task NonGenerationProviderPathPassesThroughWithQuery()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/openai/v1/models?limit=2&after=model_1",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Uri("https://api.openai.com/v1/models?limit=2&after=model_1"), upstream.RequestUri);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task DuplicatePathSlashIsPreservedWithoutChangingConfiguredAuthority()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/openai//evil.example/v1/models",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("api.openai.com", upstream.RequestUri?.Host);
        Assert.Equal("//evil.example/v1/models", upstream.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task EncodedNonJsonAndOversizedBodiesPassThroughUnchanged()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        Dictionary<string, string?> configuration = new()
        {
            ["PinkUnicorn:MaximumRequestBodyBytes"] = "1024",
        };
        await using WebApplicationFactory<Program> factory = CreateFactory(
            upstream,
            planner,
            configuration);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage encoded = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new ByteArrayContent([1, 2, 3]),
        };
        encoded.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        encoded.Content.Headers.ContentEncoding.Add("gzip");
        using HttpResponseMessage encodedResponse = await client.SendAsync(encoded, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, encodedResponse.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, upstream.Bodies[0]);

        using HttpResponseMessage textResponse = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent("not-json", Encoding.UTF8, "text/plain"),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, textResponse.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes("not-json"), upstream.Bodies[1]);

        string oversized = new string('x', 2_000);
        using HttpResponseMessage oversizedResponse = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(oversized, Encoding.UTF8, "application/json"),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, oversizedResponse.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(oversized), upstream.Bodies[2]);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task UnknownLengthBodyBeyondInspectionLimitReplaysBufferedPrefixExactly()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        Dictionary<string, string?> configuration = new()
        {
            ["PinkUnicorn:MaximumRequestBodyBytes"] = "1024",
        };
        await using WebApplicationFactory<Program> factory = CreateFactory(
            upstream,
            planner,
            configuration);
        using HttpClient client = factory.CreateClient();
        byte[] body = Encoding.UTF8.GetBytes(new string('z', 2_000));
        using HttpRequestMessage request = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new UnknownLengthContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, upstream.Body);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task RewriterFailureFailsClosedWithoutCallingPrimaryUpstream()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => HistoryEditPlannerResult.Failed(
            HistoryEditPlannerFailure.Unavailable));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();
        const string json = "{\"messages\":[{\"role\":\"user\",\"content\":\"!!NO!! It is TLS.\"}]}";

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(json, Encoding.UTF8, "application/json"),
            CancellationToken.None);
        string problem = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("history-rewriter-unavailable", problem, StringComparison.Ordinal);
        Assert.Equal(0, upstream.CallCount);
    }

    [Fact]
    public async Task InternalRewriterRequestCannotReenterProviderRoutes()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent("{\"messages\":[]}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            HistoryRewriteOptions.InternalRequestHeaderName,
            "1");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal((HttpStatusCode)508, response.StatusCode);
        Assert.Equal(0, upstream.CallCount);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task BodyIntegrityHeaderFailsClosedOnlyWhenRewriteWouldOccur()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => Success(new HistoryEdit("t0.s0", "It is TLS.")));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();
        const string json = "{\"messages\":[{\"role\":\"user\",\"content\":\"!!NO!! It is TLS.\"}]}";
        using HttpRequestMessage request = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Content-Digest", "sha-256=:synthetic:");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, upstream.CallCount);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task StatefulResponsesTriggerFailsClosedBeforePlannerAndUpstream()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();
        const string json = "{\"previous_response_id\":\"resp_123\",\"input\":\"!!NO!! It is TLS.\"}";

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/responses",
            new StringContent(json, Encoding.UTF8, "application/json"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, upstream.CallCount);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task UpstreamEventStreamIsReturnedWithoutModification()
    {
        const string events = "event: message\ndata: {\"delta\":\"one\"}\n\nevent: done\ndata: [DONE]\n\n";
        RecordingHandler upstream = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(events)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        });
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, planner);
        using HttpClient client = factory.CreateClient();
        const string json = "{\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}";

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(json, Encoding.UTF8, "application/json"),
            CancellationToken.None);
        string received = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(events, received);
    }

    [Fact]
    public async Task OptionalProxyAccessTokenIsConsumedOnlyWhenConfigured()
    {
        RecordingHandler upstream = new();
        FakePlanner planner = new(_ => throw new InvalidOperationException("Planner should not run."));
        Dictionary<string, string?> configuration = new()
        {
            ["PinkUnicorn:AccessToken"] = "a-synthetic-token-for-tests",
        };
        await using WebApplicationFactory<Program> factory = CreateFactory(
            upstream,
            planner,
            configuration);
        using HttpClient client = factory.CreateClient();
        const string json = "{\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}";

        using HttpResponseMessage denied = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(json, Encoding.UTF8, "application/json"),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using HttpRequestMessage allowedRequest = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        allowedRequest.Headers.TryAddWithoutValidation(
            "X-Pink-Unicorn-Key",
            "a-synthetic-token-for-tests");
        using HttpResponseMessage allowed = await client.SendAsync(allowedRequest, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Null(upstream.GetHeader("X-Pink-Unicorn-Key"));
    }

    private static WebApplicationFactory<Program> CreateFactory(
        RecordingHandler upstream,
        FakePlanner planner,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            Dictionary<string, string?> testConfiguration = configuration is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(configuration, StringComparer.OrdinalIgnoreCase);
            const string cachePathKey = "PinkUnicorn:HistoryRewrite:Cache:DatabasePath";
            if (!testConfiguration.ContainsKey(cachePathKey))
            {
                testConfiguration[cachePathKey] = Path.Combine(
                    Path.GetTempPath(),
                    "PinkUnicornProxy.Tests",
                    $"integration-{Guid.NewGuid():N}.db");
            }
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(testConfiguration));

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<HttpMessageInvoker>();
                services.AddSingleton(new HttpMessageInvoker(upstream, disposeHandler: false));
                services.RemoveAll<IHistoryEditPlanner>();
                services.AddSingleton<IHistoryEditPlanner>(planner);
            });
        });
    }

    private static HistoryEditPlannerResult Success(params HistoryEdit[] edits) =>
        HistoryEditPlannerResult.Success(new HistoryEditPlan(edits));

    private sealed class FakePlanner : IHistoryEditPlanner
    {
        private readonly Func<HistoryEditRequest, HistoryEditPlannerResult> callback;

        public FakePlanner(Func<HistoryEditRequest, HistoryEditPlannerResult> callback)
        {
            this.callback = callback;
        }

        public int CallCount { get; private set; }

        public Task<HistoryEditPlannerResult> CreatePlanAsync(
            HistoryEditRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(callback(request));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responseFactory;
        private readonly Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responseFactory = null)
        {
            this.responseFactory = responseFactory ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
            });
        }

        public int CallCount { get; private set; }

        public Uri? RequestUri { get; private set; }

        public byte[]? Body { get; private set; }

        public List<byte[]> Bodies { get; } = [];

        public string? Authorization { get; private set; }

        public string? GetHeader(string name) =>
            headers.TryGetValue(name, out string[]? values) ? Assert.Single(values) : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            headers.Clear();
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                headers[name] = values.ToArray();
            }

            if (request.Content is not null)
            {
                foreach ((string name, IEnumerable<string> values) in request.Content.Headers)
                {
                    headers[name] = values.ToArray();
                }

                Body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                Bodies.Add(Body);
            }

            return responseFactory(request);
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] body;

        public UnknownLengthContent(byte[] body)
        {
            this.body = body;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
