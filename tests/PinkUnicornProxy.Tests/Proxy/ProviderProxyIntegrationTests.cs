using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinkUnicornProxy.Proxy;

namespace PinkUnicornProxy.Tests.Proxy;

public sealed class ProviderProxyIntegrationTests
{
    [Fact]
    public async Task OpenAIEndpointRewritesAndForwardsToConfiguredOrigin()
    {
        RecordingHandler upstream = new();
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream);
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        const string requestJson = """
            {
              "model": "gpt-test",
              "messages": [
                { "role": "assistant", "content": "It is DNS." },
                { "role": "user", "content": "No, it is not DNS. It is TLS." }
              ]
            }
            """;
        using HttpRequestMessage request = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
        request.Headers.TryAddWithoutValidation("X-Pink-Unicorn-Debug", "do-not-forward");
        request.Headers.TryAddWithoutValidation("Cookie", "session=do-not-forward");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("rewritten", GetSingleHeader(response, ProviderProxy.ResultHeader));
        Assert.Equal("1", GetSingleHeader(response, ProviderProxy.CorrectionCountHeader));
        Assert.Equal(new Uri("https://api.openai.com/v1/chat/completions"), upstream.RequestUri);
        Assert.Equal("Bearer test-secret", upstream.Authorization);
        Assert.Null(upstream.GetHeader("X-Forwarded-For"));
        Assert.Null(upstream.GetHeader("X-Pink-Unicorn-Debug"));
        Assert.Null(upstream.GetHeader("Cookie"));
        Assert.NotNull(upstream.Body);
        string forwarded = Encoding.UTF8.GetString(upstream.Body);
        Assert.DoesNotContain("DNS", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("It is TLS.", forwarded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicEndpointForwardsVendorHeadersAndStripsThinkingOnRewrite()
    {
        RecordingHandler upstream = new();
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream);
        using HttpClient client = factory.CreateClient();
        const string requestJson = """
            {
              "model": "claude-test",
              "max_tokens": 100,
              "messages": [
                { "role": "assistant", "content": [
                  { "type": "thinking", "thinking": "DNS", "signature": "opaque" },
                  { "type": "text", "text": "It is DNS." }
                ] },
                { "role": "user", "content": "No, it is not DNS. It is TLS." }
              ]
            }
            """;
        using HttpRequestMessage request = new(HttpMethod.Post, "/anthropic/v1/messages")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", "anthropic-secret");
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Uri("https://api.anthropic.com/v1/messages"), upstream.RequestUri);
        Assert.Equal("anthropic-secret", upstream.GetHeader("x-api-key"));
        Assert.Equal("2023-06-01", upstream.GetHeader("anthropic-version"));
        Assert.Equal("1", GetSingleHeader(response, ProviderProxy.OpaqueBlockCountHeader));
        Assert.DoesNotContain("opaque", Encoding.UTF8.GetString(Assert.IsType<byte[]>(upstream.Body)));
    }

    [Fact]
    public async Task NoOpRequestIsForwardedByteForByte()
    {
        RecordingHandler upstream = new();
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream);
        using HttpClient client = factory.CreateClient();
        const string requestJson = "{ \"model\":\"gpt-test\", \"unknown\": 7, \"messages\": [ {\"role\":\"user\",\"content\":\"Hello\"} ] }";

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(requestJson, Encoding.UTF8, "application/json"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("unchanged", GetSingleHeader(response, ProviderProxy.ResultHeader));
        Assert.Equal(Encoding.UTF8.GetBytes(requestJson), upstream.Body);
    }

    [Fact]
    public async Task NonGenerationOpenAIEndpointIsDeniedByDefault()
    {
        RecordingHandler upstream = new();
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/openai/v1/models?limit=2",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("path-not-allowed", GetSingleHeader(response, ProviderProxy.ResultHeader));
        Assert.Equal(0, upstream.CallCount);
    }

    [Fact]
    public async Task DoubleSlashPathCannotReplaceConfiguredUpstreamAuthority()
    {
        RecordingHandler upstream = new();
        Dictionary<string, string?> configuration = new()
        {
            ["PinkUnicorn:AllowOtherPaths"] = "true",
        };
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, configuration);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/openai//evil.example/v1/models",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("api.openai.com", upstream.RequestUri?.Host);
        Assert.Equal("/evil.example/v1/models", upstream.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task OptionalProxyAccessTokenIsRequiredAndNeverForwarded()
    {
        RecordingHandler upstream = new();
        Dictionary<string, string?> configuration = new()
        {
            ["PinkUnicorn:AccessToken"] = "a-synthetic-token-for-tests",
        };
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, configuration);
        using HttpClient client = factory.CreateClient();
        const string requestJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}";

        using HttpResponseMessage denied = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(requestJson, Encoding.UTF8, "application/json"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(0, upstream.CallCount);

        using HttpRequestMessage allowedRequest = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        allowedRequest.Headers.TryAddWithoutValidation(
            "X-Pink-Unicorn-Key",
            "a-synthetic-token-for-tests");
        using HttpResponseMessage allowed = await client.SendAsync(allowedRequest, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(1, upstream.CallCount);
        Assert.Null(upstream.GetHeader("X-Pink-Unicorn-Key"));
    }

    [Fact]
    public async Task EncodedGenerationBodyFailsClosedInRewriteMode()
    {
        RecordingHandler upstream = new();
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new ByteArrayContent([1, 2, 3]),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentEncoding.Add("gzip");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported-content-encoding", GetSingleHeader(response, ProviderProxy.ResultHeader));
        Assert.Equal(0, upstream.CallCount);
    }

    [Fact]
    public async Task NonJsonGenerationBodyFailsClosedInRewriteMode()
    {
        RecordingHandler upstream = new();
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent("not-json", Encoding.UTF8, "text/plain"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported-content-type", GetSingleHeader(response, ProviderProxy.ResultHeader));
        Assert.Equal(0, upstream.CallCount);
    }

    [Fact]
    public async Task BodyIntegrityHeaderFailsClosedWhenRewriteWouldChangeBytes()
    {
        RecordingHandler upstream = new();
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream);
        using HttpClient client = factory.CreateClient();
        const string requestJson = """
            { "messages": [
              { "role": "assistant", "content": "It is DNS." },
              { "role": "user", "content": "No, it is not DNS. It is TLS." }
            ] }
            """;
        using HttpRequestMessage request = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Content-Digest", "sha-256=:synthetic:");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("signed-body-conflict", GetSingleHeader(response, ProviderProxy.ResultHeader));
        Assert.Equal(0, upstream.CallCount);
    }

    [Fact]
    public async Task StatefulResponsesCorrectionFailsClosedByDefault()
    {
        RecordingHandler upstream = new();
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream);
        using HttpClient client = factory.CreateClient();
        const string requestJson = """
            {
              "model": "gpt-test",
              "previous_response_id": "resp_123",
              "input": "No, it is not DNS. It is TLS."
            }
            """;

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/responses",
            new StringContent(requestJson, Encoding.UTF8, "application/json"),
            CancellationToken.None);
        string problem = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("unsupported-stateful-context", GetSingleHeader(response, ProviderProxy.ResultHeader));
        Assert.Contains("stateful-context-cannot-be-rewritten", problem, StringComparison.Ordinal);
        Assert.Equal(0, upstream.CallCount);
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
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream);
        using HttpClient client = factory.CreateClient();
        const string requestJson = "{\"model\":\"gpt-test\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}";
        using HttpRequestMessage request = new(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None);
        string received = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(events, received);
    }

    [Fact]
    public async Task OversizedInspectableBodyIsRejectedBeforeUpstream()
    {
        RecordingHandler upstream = new();
        Dictionary<string, string?> configuration = new()
        {
            ["PinkUnicorn:MaximumRequestBodyBytes"] = "1024",
        };
        await using WebApplicationFactory<Program> factory = CreateFactory(upstream, configuration);
        using HttpClient client = factory.CreateClient();
        JsonObject request = new()
        {
            ["model"] = "gpt-test",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new string('x', 2_000),
                },
            },
        };

        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request-body-too-large", GetSingleHeader(response, ProviderProxy.ResultHeader));
        Assert.Equal(0, upstream.CallCount);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        RecordingHandler upstream,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (configuration is not null)
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(configuration));
            }

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<HttpMessageInvoker>();
                services.AddSingleton(new HttpMessageInvoker(upstream, disposeHandler: false));
            });
        });
    }

    private static string GetSingleHeader(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues(name));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responseFactory;

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

        public string? Authorization { get; private set; }

        private Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetHeader(string name) =>
            Headers.TryGetValue(name, out string[]? values) ? Assert.Single(values) : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                Headers[name] = values.ToArray();
            }

            if (request.Content is not null)
            {
                foreach ((string name, IEnumerable<string> values) in request.Content.Headers)
                {
                    Headers[name] = values.ToArray();
                }

                Body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            return responseFactory(request);
        }
    }
}
