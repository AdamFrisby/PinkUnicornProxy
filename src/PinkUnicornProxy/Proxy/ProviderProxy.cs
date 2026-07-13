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
    private static readonly string[] BodyIntegrityHeaders =
    [
        "Content-Digest",
        "Content-MD5",
        "Digest",
        "Repr-Digest",
        "Signature",
        "Signature-Input",
    ];

    private readonly IHttpForwarder forwarder;
    private readonly HttpMessageInvoker httpClient;
    private readonly ConversationRewriter rewriter;
    private readonly PinkUnicornOptions options;
    private readonly ILogger<ProviderProxy> logger;
    private readonly byte[]? accessTokenHash;
    private readonly ProviderRequestTransformer transformer;

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
        transformer = new ProviderRequestTransformer(accessTokenHash is not null);
    }

    public Task ForwardOpenAIAsync(HttpContext context)
    {
        string path = GetForwardPath(context, "/openai");
        ProviderRequestKind? requestKind = path switch
        {
            "/v1/chat/completions" => ProviderRequestKind.OpenAIChatCompletions,
            "/v1/responses" => ProviderRequestKind.OpenAIResponses,
            "/v1/responses/compact" => ProviderRequestKind.OpenAIResponses,
            _ => null,
        };

        return ForwardAsync(context, options.Upstreams.OpenAI, path, requestKind);
    }

    public Task ForwardAnthropicAsync(HttpContext context)
    {
        string path = GetForwardPath(context, "/anthropic");
        ProviderRequestKind? requestKind = path switch
        {
            "/v1/messages" => ProviderRequestKind.AnthropicMessages,
            "/v1/messages/count_tokens" => ProviderRequestKind.AnthropicMessages,
            _ => null,
        };

        return ForwardAsync(context, options.Upstreams.Anthropic, path, requestKind);
    }

    private async Task ForwardAsync(
        HttpContext context,
        string destination,
        string forwardPath,
        ProviderRequestKind? requestKind)
    {
        if (!IsAuthorized(context.Request))
        {
            context.Response.Headers.WWWAuthenticate = "PinkUnicornKey";
            await WriteProblemAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "authentication-required",
                "Supply the configured proxy access token in X-Pink-Unicorn-Key.");
            return;
        }

        if (context.Request.Headers.ContainsKey(HistoryRewriteOptions.InternalRequestHeaderName))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status508LoopDetected,
                "recursive-history-rewriter-request",
                "A history rewriter request was routed back through Pink Unicorn Proxy.");
            return;
        }

        Stream? replayBody = null;
        Stream? replacementBody = null;
        try
        {
            bool inspectable = requestKind is not null
                && HttpMethods.IsPost(context.Request.Method)
                && options.Mode != ProxyMode.Off
                && HasJsonContentType(context.Request)
                && !HasUnsupportedContentEncoding(context.Request);

            if (inspectable)
            {
                RequestBodyReadResult readResult = await ReadInspectableBodyAsync(
                    context.Request,
                    options.MaximumRequestBodyBytes,
                    context.RequestAborted);
                if (readResult.ReplayBody is not null)
                {
                    replayBody = readResult.ReplayBody;
                    context.Request.Body = replayBody;
                }

                byte[]? originalBody = readResult.InspectableBody;
                if (originalBody is not null)
                {
                    RewriteResult result = await rewriter.RewriteAsync(
                        requestKind!.Value,
                        originalBody,
                        HasBodyIntegrityProtection(context.Request),
                        context.RequestAborted);

                    if (ShouldReject(result))
                    {
                        await WriteRewriteProblemAsync(context, result);
                        return;
                    }

                    if (result.BodyChanged)
                    {
                        replacementBody = new MemoryStream(result.Body, writable: false);
                        context.Request.Body = replacementBody;
                        if (context.Request.ContentLength.HasValue)
                        {
                            context.Request.ContentLength = result.Body.Length;
                        }

                        if (result.CacheHit)
                        {
                            LogCacheReplay(logger, requestKind.Value, result.TriggerCount);
                        }
                        else
                        {
                            LogRewrite(
                                logger,
                                requestKind.Value,
                                result.TriggerCount,
                                result.TextEditCount,
                                result.OpaqueBlockCount);
                        }
                    }
                    else if (result.Outcome == RewriteOutcome.AuditMatch)
                    {
                        LogAuditMatch(logger, requestKind.Value, result.TriggerCount);
                    }
                }
            }

            await ForwardAsync(context, destination, forwardPath);
        }
        finally
        {
            if (replacementBody is not null)
            {
                await replacementBody.DisposeAsync();
            }

            if (replayBody is not null)
            {
                await replayBody.DisposeAsync();
            }
        }
    }

    private static bool ShouldReject(RewriteResult result) => result.Outcome is
        RewriteOutcome.AmbiguousJsonProperties
        or RewriteOutcome.UnsupportedStatefulContext
        or RewriteOutcome.DeferredOpenToolTurn
        or RewriteOutcome.ProtectedContentConflict
        or RewriteOutcome.SignedBodyConflict
        or RewriteOutcome.RewriterNotConfigured
        or RewriteOutcome.RewriterUnavailable
        or RewriteOutcome.CacheUnavailable
        or RewriteOutcome.PlannerInputTooLarge
        or RewriteOutcome.InvalidRewriterResponse
        or RewriteOutcome.UnsafeRewriterOutput
        or RewriteOutcome.EditLimitExceeded;

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

    private async Task ForwardAsync(
        HttpContext context,
        string destination,
        string forwardPath)
    {
        PathString originalPath = context.Request.Path;
        context.Request.Path = new PathString(forwardPath);
        try
        {
            ForwarderRequestConfig requestConfig = new()
            {
                ActivityTimeout = options.ActivityTimeout,
                Version = GetIncomingVersion(context.Request.Protocol),
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            ForwarderError error = await forwarder.SendAsync(
                context,
                destination.TrimEnd('/'),
                httpClient,
                requestConfig,
                transformer);

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

    private static Version GetIncomingVersion(string protocol) => protocol switch
    {
        "HTTP/3" => HttpVersion.Version30,
        "HTTP/2" => HttpVersion.Version20,
        "HTTP/1.0" => HttpVersion.Version10,
        _ => HttpVersion.Version11,
    };

    private static bool IsCancellationError(ForwarderError error) => error is
        ForwarderError.RequestCanceled
        or ForwarderError.RequestBodyCanceled
        or ForwarderError.ResponseBodyCanceled
        or ForwarderError.UpgradeRequestCanceled
        or ForwarderError.UpgradeResponseCanceled;

    private static async Task<RequestBodyReadResult> ReadInspectableBodyAsync(
        HttpRequest request,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumBytes)
        {
            return RequestBodyReadResult.Uninspectable;
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
                    byte[] body = buffer.ToArray();
                    return new RequestBodyReadResult(
                        body,
                        new MemoryStream(body, writable: false));
                }

                await buffer.WriteAsync(rented.AsMemory(0, read), cancellationToken);
                if (buffer.Length > maximumBytes)
                {
                    return new RequestBodyReadResult(
                        null,
                        new PrefixReadStream(buffer.ToArray(), request.Body));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static Task WriteRewriteProblemAsync(HttpContext context, RewriteResult result)
    {
        return result.Outcome switch
        {
            RewriteOutcome.AmbiguousJsonProperties => WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "ambiguous-json-properties",
                "The request contains duplicate JSON property names and cannot be rewritten unambiguously."),
            RewriteOutcome.UnsupportedStatefulContext => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "stateful-context-cannot-be-rewritten",
                "This Responses API request refers to provider-held history. Supply the complete input history in one request."),
            RewriteOutcome.DeferredOpenToolTurn => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "open-tool-turn-is-immutable",
                "The trigger intersects an open tool or thinking transaction. Complete or abandon that transaction before rewriting history."),
            RewriteOutcome.ProtectedContentConflict => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "protected-content-cannot-be-rewritten",
                "A trigger occurs in text with provider-managed citations, annotations, or log probabilities."),
            RewriteOutcome.SignedBodyConflict => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "signed-body-cannot-be-rewritten",
                "The request has body-integrity or HTTP-signature headers that would become invalid after rewriting."),
            RewriteOutcome.RewriterNotConfigured => WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "history-rewriter-not-configured",
                "Configure a HistoryRewrite endpoint and model before using a history rewrite trigger."),
            RewriteOutcome.RewriterUnavailable => WriteProblemAsync(
                context,
                StatusCodes.Status502BadGateway,
                "history-rewriter-unavailable",
                "The auxiliary history rewriter did not return a usable response."),
            RewriteOutcome.CacheUnavailable => WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "history-rewrite-cache-unavailable",
                "The durable history rewrite cache is unavailable; no competing revision was created."),
            RewriteOutcome.PlannerInputTooLarge => WriteProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "history-planner-input-too-large",
                "The complete history planning request exceeds the configured auxiliary request byte limit."),
            RewriteOutcome.InvalidRewriterResponse => WriteProblemAsync(
                context,
                StatusCodes.Status502BadGateway,
                "invalid-history-rewriter-response",
                "The auxiliary history rewriter returned malformed output."),
            RewriteOutcome.UnsafeRewriterOutput => WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "unsafe-history-rewriter-output",
                "The auxiliary history rewriter proposed an incomplete or structurally unsafe edit plan."),
            RewriteOutcome.EditLimitExceeded => WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "history-edit-limit-exceeded",
                "The auxiliary history rewriter proposed more edits than the configured per-request limit."),
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
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        return path[0] == '/' ? path : $"/{path}";
    }

    private sealed record ProxyProblem(string Type, string Title, int Status, string Detail);

    private sealed record RequestBodyReadResult(byte[]? InspectableBody, Stream? ReplayBody)
    {
        public static RequestBodyReadResult Uninspectable { get; } = new(null, null);
    }

    private sealed class PrefixReadStream : Stream
    {
        private readonly byte[] prefix;
        private readonly Stream remainder;
        private int prefixPosition;

        public PrefixReadStream(byte[] prefix, Stream remainder)
        {
            this.prefix = prefix;
            this.remainder = remainder;
        }

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
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int copied = CopyPrefix(buffer);
            return copied > 0 ? copied : remainder.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int copied = CopyPrefix(buffer.Span);
            return copied > 0
                ? copied
                : await remainder.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private int CopyPrefix(Span<byte> destination)
        {
            int available = prefix.Length - prefixPosition;
            int count = Math.Min(available, destination.Length);
            if (count > 0)
            {
                prefix.AsSpan(prefixPosition, count).CopyTo(destination);
                prefixPosition += count;
            }

            return count;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Rewrote {Provider} history for {TriggerCount} trigger fields using {EditCount} text edits and removed {OpaqueBlockCount} opaque blocks.")]
    private static partial void LogRewrite(
        ILogger logger,
        ProviderRequestKind provider,
        int triggerCount,
        int editCount,
        int opaqueBlockCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "History rewrite audit matched {TriggerCount} trigger fields in a {Provider} request.")]
    private static partial void LogAuditMatch(
        ILogger logger,
        ProviderRequestKind provider,
        int triggerCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Replayed a cached {Provider} history rewrite for {TriggerCount} trigger fields.")]
    private static partial void LogCacheReplay(
        ILogger logger,
        ProviderRequestKind provider,
        int triggerCount);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Provider forwarding failed with {ForwarderError} for {ProviderPath}.")]
    private static partial void LogForwardingError(
        ILogger logger,
        ForwarderError forwarderError,
        string providerPath,
        Exception? exception);
}
