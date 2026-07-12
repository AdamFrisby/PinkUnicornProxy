using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PinkUnicornProxy.Configuration;
using PinkUnicornProxy.Rewriting;
using Yarp.ReverseProxy.Forwarder;

namespace PinkUnicornProxy.Proxy;

internal sealed partial class ProviderProxy
{
    internal const string ResultHeader = "X-Pink-Unicorn-Result";
    internal const string CorrectionCountHeader = "X-Pink-Unicorn-Corrections";
    internal const string OpaqueBlockCountHeader = "X-Pink-Unicorn-Opaque-Blocks-Removed";

    private readonly IHttpForwarder forwarder;
    private readonly HttpMessageInvoker httpClient;
    private readonly ConversationRewriter rewriter;
    private readonly PinkUnicornOptions options;
    private readonly ForwarderRequestConfig requestConfig;
    private readonly ILogger<ProviderProxy> logger;
    private readonly byte[]? accessTokenHash;

    private static readonly string[] BodyIntegrityHeaders =
    [
        "Content-Digest",
        "Content-MD5",
        "Digest",
        "Repr-Digest",
        "Signature",
        "Signature-Input",
    ];

    public ProviderProxy(
        IHttpForwarder forwarder,
        HttpMessageInvoker httpClient,
        ConversationRewriter rewriter,
        IOptions<PinkUnicornOptions> options,
        ILogger<ProviderProxy> logger)
    {
        this.forwarder = forwarder;
        this.httpClient = httpClient;
        this.rewriter = rewriter;
        this.options = options.Value;
        this.logger = logger;
        accessTokenHash = string.IsNullOrEmpty(this.options.AccessToken)
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(this.options.AccessToken));
        requestConfig = new ForwarderRequestConfig
        {
            ActivityTimeout = this.options.ActivityTimeout,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public async Task ForwardOpenAIAsync(HttpContext context)
    {
        string path = GetForwardPath(context, "/openai");
        ProviderRequestKind? requestKind = path switch
        {
            "/v1/chat/completions" => ProviderRequestKind.OpenAIChatCompletions,
            "/v1/responses" => ProviderRequestKind.OpenAIResponses,
            "/v1/responses/compact" => ProviderRequestKind.OpenAIResponses,
            _ => null,
        };

        await ForwardAsync(context, options.Upstreams.OpenAI, path, requestKind);
    }

    public async Task ForwardAnthropicAsync(HttpContext context)
    {
        string path = GetForwardPath(context, "/anthropic");
        ProviderRequestKind? requestKind = path switch
        {
            "/v1/messages" => ProviderRequestKind.AnthropicMessages,
            "/v1/messages/count_tokens" => ProviderRequestKind.AnthropicMessages,
            _ => null,
        };

        await ForwardAsync(context, options.Upstreams.Anthropic, path, requestKind);
    }

    private async Task ForwardAsync(
        HttpContext context,
        string destination,
        string forwardPath,
        ProviderRequestKind? requestKind)
    {
        if (!IsAuthorized(context.Request))
        {
            context.Response.Headers[ResultHeader] = "authentication-required";
            context.Response.Headers.WWWAuthenticate = "PinkUnicornKey";
            await WriteProblemAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "authentication-required",
                "Supply the configured proxy access token in X-Pink-Unicorn-Key.");
            return;
        }

        if (HttpMethods.IsConnect(context.Request.Method)
            || HttpMethods.IsTrace(context.Request.Method)
            || context.WebSockets.IsWebSocketRequest
            || context.Request.Headers.Connection.Any(value =>
                value?.Contains("upgrade", StringComparison.OrdinalIgnoreCase) == true))
        {
            context.Response.Headers[ResultHeader] = "method-not-allowed";
            await WriteProblemAsync(
                context,
                StatusCodes.Status405MethodNotAllowed,
                "method-not-allowed",
                "CONNECT, TRACE, and protocol upgrades are not supported by this HTTP generation proxy.");
            return;
        }

        if (requestKind is not null && !HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.Headers.Allow = "POST";
            context.Response.Headers[ResultHeader] = "method-not-allowed";
            await WriteProblemAsync(
                context,
                StatusCodes.Status405MethodNotAllowed,
                "method-not-allowed",
                "Supported generation endpoints accept POST requests only.");
            return;
        }

        if (requestKind is null && !options.AllowOtherPaths)
        {
            context.Response.Headers[ResultHeader] = "path-not-allowed";
            await WriteProblemAsync(
                context,
                StatusCodes.Status404NotFound,
                "path-not-allowed",
                "The path is outside the default provider endpoint allowlist.");
            return;
        }

        Stream? bufferedBody = null;
        try
        {
            if (requestKind is not null && options.Mode == ProxyMode.Off)
            {
                RegisterOutcomeHeaders(context, RewriteResult.Unchanged([]));
                await ForwardBufferedOrStreamingAsync(context, destination, forwardPath);
                return;
            }

            if (requestKind is not null
                && !HasJsonContentType(context.Request))
            {
                RewriteResult contentTypeResult = RewriteResult.Unchanged(
                    [],
                    RewriteOutcome.UnsupportedContentType);
                RegisterOutcomeHeaders(context, contentTypeResult);
                if (options.Mode == ProxyMode.Rewrite)
                {
                    await WriteRewriteProblemAsync(context, contentTypeResult);
                    return;
                }

                await ForwardBufferedOrStreamingAsync(context, destination, forwardPath);
                return;
            }

            if (requestKind is not null && HasJsonContentType(context.Request))
            {
                if (HasUnsupportedContentEncoding(context.Request))
                {
                    RewriteResult encodingResult = RewriteResult.Unchanged(
                        [],
                        RewriteOutcome.UnsupportedContentEncoding);
                    RegisterOutcomeHeaders(context, encodingResult);
                    if (options.Mode == ProxyMode.Rewrite)
                    {
                        await WriteRewriteProblemAsync(context, encodingResult);
                        return;
                    }

                    await ForwardBufferedOrStreamingAsync(
                        context,
                        destination,
                        forwardPath);
                    return;
                }

                byte[]? requestBody = await ReadBodyAsync(
                    context.Request,
                    options.MaximumRequestBodyBytes,
                    context.RequestAborted);

                if (requestBody is null)
                {
                    context.Response.Headers[ResultHeader] = "request-body-too-large";
                    await WriteProblemAsync(
                        context,
                        StatusCodes.Status413PayloadTooLarge,
                        "request-body-too-large",
                        $"The request exceeds the configured {options.MaximumRequestBodyBytes} byte rewrite limit.");
                    return;
                }

                RewriteResult result = rewriter.Rewrite(requestKind.Value, requestBody);
                if (result.BodyChanged && HasBodyIntegrityProtection(context.Request))
                {
                    result = result with
                    {
                        Body = requestBody,
                        Outcome = RewriteOutcome.SignedBodyConflict,
                        TextEditCount = 0,
                        OpaqueBlockCount = 0,
                    };
                }

                RegisterOutcomeHeaders(context, result);

                if (ShouldReject(result))
                {
                    await WriteRewriteProblemAsync(context, result);
                    return;
                }

                byte[] outboundBody = result.BodyChanged ? result.Body : requestBody;
                bufferedBody = new MemoryStream(outboundBody, writable: false);
                context.Request.Body = bufferedBody;
                context.Request.ContentLength = outboundBody.Length;
                context.Request.Headers.Remove("Transfer-Encoding");
            }
            else
            {
                RegisterOutcomeHeaders(context, RewriteResult.Unchanged([]));
            }

            await ForwardBufferedOrStreamingAsync(context, destination, forwardPath);
        }
        finally
        {
            if (bufferedBody is not null)
            {
                await bufferedBody.DisposeAsync();
            }
        }
    }

    private bool ShouldReject(RewriteResult result)
    {
        return result.Outcome switch
        {
            RewriteOutcome.UnsupportedStatefulContext =>
                options.StatefulResponsesPolicy == StatefulResponsesPolicy.Reject,
            RewriteOutcome.DeferredOpenToolTurn => true,
            RewriteOutcome.ProtectedContentConflict => true,
            RewriteOutcome.UnsupportedContentType => options.Mode == ProxyMode.Rewrite,
            RewriteOutcome.UnsupportedContentEncoding => options.Mode == ProxyMode.Rewrite,
            RewriteOutcome.SignedBodyConflict => true,
            RewriteOutcome.CorrectionLimitExceeded => true,
            _ => false,
        };
    }

    private static bool HasJsonContentType(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue? contentType))
        {
            return false;
        }

        string? mediaType = contentType.MediaType;
        return mediaType is not null
            && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasUnsupportedContentEncoding(HttpRequest request) =>
        request.Headers.ContentEncoding.Count > 0
        && !request.Headers.ContentEncoding.All(value =>
            value?.Equals("identity", StringComparison.OrdinalIgnoreCase) == true);

    private static bool HasBodyIntegrityProtection(HttpRequest request)
    {
        if (BodyIntegrityHeaders.Any(request.Headers.ContainsKey))
        {
            return true;
        }

        string authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Signature ", StringComparison.OrdinalIgnoreCase)
            || authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (accessTokenHash is null)
        {
            return true;
        }

        byte[] suppliedHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(request.Headers["X-Pink-Unicorn-Key"].ToString()));
        return CryptographicOperations.FixedTimeEquals(accessTokenHash, suppliedHash);
    }

    private async Task ForwardBufferedOrStreamingAsync(
        HttpContext context,
        string destination,
        string forwardPath)
    {
        PathString originalPath = context.Request.Path;
        context.Request.Path = new PathString(forwardPath);
        try
        {
            ForwarderError error = await forwarder.SendAsync(
                context,
                destination.TrimEnd('/'),
                httpClient,
                requestConfig,
                ProviderRequestTransformer.Instance);

            if (error != ForwarderError.None
                && !(context.RequestAborted.IsCancellationRequested && IsCancellationError(error)))
            {
                IForwarderErrorFeature? errorFeature = context.GetForwarderErrorFeature();
                LogForwardingError(logger, error, forwardPath, errorFeature?.Exception);
            }
        }
        finally
        {
            context.Request.Path = originalPath;
        }
    }

    private static bool IsCancellationError(ForwarderError error) => error is
        ForwarderError.RequestCanceled
        or ForwarderError.RequestBodyCanceled
        or ForwarderError.ResponseBodyCanceled
        or ForwarderError.UpgradeRequestCanceled
        or ForwarderError.UpgradeResponseCanceled;

    private static async Task<byte[]?> ReadBodyAsync(
        HttpRequest request,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumBytes)
        {
            return null;
        }

        int initialCapacity = request.ContentLength is > 0 and <= int.MaxValue
            ? (int)request.ContentLength.Value
            : 16 * 1024;

        using MemoryStream buffer = new(initialCapacity);
        byte[] rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                int read = await request.Body.ReadAsync(rented, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maximumBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(rented.AsMemory(0, read), cancellationToken);
            }

            return buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void RegisterOutcomeHeaders(HttpContext context, RewriteResult result)
    {
        context.Response.OnStarting(
            static state =>
            {
                (HttpContext httpContext, RewriteResult rewriteResult) =
                    ((HttpContext, RewriteResult))state;
                httpContext.Response.Headers[ResultHeader] = rewriteResult.HeaderValue;

                if (rewriteResult.CorrectionCount > 0)
                {
                    httpContext.Response.Headers[CorrectionCountHeader] =
                        rewriteResult.CorrectionCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                if (rewriteResult.OpaqueBlockCount > 0)
                {
                    httpContext.Response.Headers[OpaqueBlockCountHeader] =
                        rewriteResult.OpaqueBlockCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return Task.CompletedTask;
            },
            (context, result));
    }

    private static Task WriteRewriteProblemAsync(HttpContext context, RewriteResult result)
    {
        return result.Outcome switch
        {
            RewriteOutcome.UnsupportedStatefulContext => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "stateful-context-cannot-be-rewritten",
                "This Responses API request refers to provider-held history. Supply the full input history or configure FreshStart to discard provider-held context."),
            RewriteOutcome.DeferredOpenToolTurn => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "open-tool-turn-is-immutable",
                "The correction intersects an open tool/thinking transaction. Complete or abandon that transaction before rewriting its history."),
            RewriteOutcome.ProtectedContentConflict => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "protected-content-cannot-be-rewritten",
                "The rejected claim occurs in protected code, URL, media, or protocol content. The proxy will not edit that content or claim a forgetting guarantee."),
            RewriteOutcome.UnsupportedContentType => WriteProblemAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "non-json-body-cannot-be-rewritten",
                "Supported generation endpoints must use an application/json or +json Content-Type so the proxy can enforce rewriting."),
            RewriteOutcome.UnsupportedContentEncoding => WriteProblemAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "encoded-body-cannot-be-rewritten",
                "A supported generation request used a non-identity Content-Encoding. Send unencoded JSON so the proxy can enforce rewriting."),
            RewriteOutcome.SignedBodyConflict => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "signed-body-cannot-be-rewritten",
                "The request has body-integrity or HTTP-signature headers that would become invalid after rewriting."),
            RewriteOutcome.CorrectionLimitExceeded => WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "correction-limit-exceeded",
                "The transcript contains more correction events than the configured per-request limit."),
            _ => throw new InvalidOperationException($"No problem response is defined for {result.Outcome}."),
        };
    }

    private static Task WriteProblemAsync(
        HttpContext context,
        int status,
        string code,
        string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            new ProxyProblem(
                $"https://github.com/AdamFrisby/PinkUnicornProxy/blob/main/docs/problems.md#{code}",
                code,
                status,
                detail),
            cancellationToken: context.RequestAborted);
    }

    private static string GetForwardPath(HttpContext context, string routePrefix)
    {
        string requestPath = context.Request.Path.Value ?? string.Empty;
        string path = requestPath.StartsWith(routePrefix, StringComparison.OrdinalIgnoreCase)
            ? requestPath[routePrefix.Length..]
            : requestPath;
        return string.IsNullOrEmpty(path) ? "/" : $"/{path.TrimStart('/')}";
    }

    private sealed record ProxyProblem(string Type, string Title, int Status, string Detail);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Provider forwarding failed with {ForwarderError} for {ProviderPath}.")]
    private static partial void LogForwardingError(
        ILogger logger,
        ForwarderError forwarderError,
        string providerPath,
        Exception? exception);
}
