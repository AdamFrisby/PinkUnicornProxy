using Yarp.ReverseProxy.Forwarder;

namespace PinkUnicornProxy.Proxy;

internal sealed class ProviderRequestTransformer : HttpTransformer
{
    private static readonly string[] RemovedHeaders =
    [
        "Cookie",
        "Forwarded",
        "Proxy-Authorization",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Prefix",
        "X-Forwarded-Proto",
        "X-Pink-Unicorn-Key",
        "X-Real-IP",
    ];

    public static ProviderRequestTransformer Instance { get; } = new();

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(
            httpContext,
            proxyRequest,
            destinationPrefix,
            cancellationToken);

        foreach (string header in RemovedHeaders)
        {
            proxyRequest.Headers.Remove(header);
        }

        foreach ((string name, _) in proxyRequest.Headers.ToArray())
        {
            if (name.StartsWith("X-Pink-Unicorn-", StringComparison.OrdinalIgnoreCase))
            {
                proxyRequest.Headers.Remove(name);
            }
        }
    }
}
