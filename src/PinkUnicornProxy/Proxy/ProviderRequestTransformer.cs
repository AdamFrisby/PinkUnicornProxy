using Yarp.ReverseProxy.Forwarder;

namespace PinkUnicornProxy.Proxy;

internal sealed class ProviderRequestTransformer : HttpTransformer
{
    private readonly bool removeAccessTokenHeader;

    public ProviderRequestTransformer(bool removeAccessTokenHeader)
    {
        this.removeAccessTokenHeader = removeAccessTokenHeader;
    }

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

        // The incoming Host names the proxy. Clearing it lets HttpClient emit the fixed
        // destination authority; every other copied end-to-end header is left alone.
        proxyRequest.Headers.Host = null;

        if (removeAccessTokenHeader)
        {
            proxyRequest.Headers.Remove("X-Pink-Unicorn-Key");
        }
    }
}
