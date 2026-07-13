using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace PinkUnicornProxy.Configuration;

internal sealed class PinkUnicornOptionsValidator : IValidateOptions<PinkUnicornOptions>
{
    public ValidateOptionsResult Validate(string? name, PinkUnicornOptions options)
    {
        List<string> failures = [];

        if (options.MaximumRequestBodyBytes is < 1024 or > 16 * 1024 * 1024)
        {
            failures.Add("MaximumRequestBodyBytes must be between 1 KiB and 16 MiB.");
        }

        if (options.MaximumEditsPerRequest is < 1 or > 1_000)
        {
            failures.Add("MaximumEditsPerRequest must be between 1 and 1,000.");
        }

        if (options.MaximumConcurrentRequests is < 1 or > 10_000)
        {
            failures.Add("MaximumConcurrentRequests must be between 1 and 10,000.");
        }

        if (!Enum.IsDefined(options.Mode))
        {
            failures.Add("Mode is not a defined ProxyMode value.");
        }

        if (options.AccessToken is { Length: > 0 and < 16 })
        {
            failures.Add("AccessToken must contain at least 16 characters when configured.");
        }

        if (options.ActivityTimeout <= TimeSpan.Zero)
        {
            failures.Add("ActivityTimeout must be positive.");
        }

        if (options.ConnectTimeout <= TimeSpan.Zero)
        {
            failures.Add("ConnectTimeout must be positive.");
        }

        ValidateHistoryRewrite(options, failures);

        ValidateOrigin(nameof(options.Upstreams.OpenAI), options.Upstreams.OpenAI, options, failures);
        ValidateOrigin(nameof(options.Upstreams.Anthropic), options.Upstreams.Anthropic, options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateHistoryRewrite(
        PinkUnicornOptions options,
        List<string> failures)
    {
        HistoryRewriteOptions rewrite = options.HistoryRewrite;
        if (!Enum.IsDefined(rewrite.Protocol))
        {
            failures.Add("HistoryRewrite:Protocol is not a defined value.");
        }

        if (!Enum.IsDefined(rewrite.OpenAIOutputTokenParameter))
        {
            failures.Add("HistoryRewrite:OpenAIOutputTokenParameter is not a defined value.");
        }

        if (rewrite.TriggerTokens is { Length: > 0 })
        {
            HashSet<string> tokens = new(StringComparer.Ordinal);
            foreach (string? token in rewrite.TriggerTokens)
            {
                if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
                {
                    failures.Add("Each history rewrite trigger token must contain 1 to 256 non-whitespace characters.");
                }
                else if (!tokens.Add(token))
                {
                    failures.Add($"HistoryRewrite:TriggerTokens contains the duplicate token '{token}'.");
                }
            }
        }

        bool hasEndpoint = !string.IsNullOrWhiteSpace(rewrite.Endpoint);
        bool hasModel = !string.IsNullOrWhiteSpace(rewrite.Model);
        if (hasEndpoint != hasModel)
        {
            failures.Add("HistoryRewrite:Endpoint and HistoryRewrite:Model must be configured together.");
        }

        if (hasEndpoint)
        {
            if (!Uri.TryCreate(rewrite.Endpoint, UriKind.Absolute, out Uri? endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(endpoint.Fragment))
            {
                failures.Add("HistoryRewrite:Endpoint must be an absolute HTTP(S) URL without a fragment.");
            }
            else
            {
                if (!string.IsNullOrEmpty(endpoint.UserInfo))
                {
                    failures.Add("HistoryRewrite:Endpoint must not contain URI user information.");
                }

                bool loopback = endpoint.IsLoopback
                    || endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || endpoint.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
                if (endpoint.Scheme == Uri.UriSchemeHttp
                    && !loopback
                    && !options.AllowInsecureUpstreams)
                {
                    failures.Add("HistoryRewrite:Endpoint must use HTTPS unless it is loopback or AllowInsecureUpstreams is enabled.");
                }
            }
        }

        if (rewrite.Timeout <= TimeSpan.Zero || rewrite.Timeout > TimeSpan.FromMinutes(10))
        {
            failures.Add("HistoryRewrite:Timeout must be positive and no longer than 10 minutes.");
        }

        if (rewrite.MaximumOutputTokens is < 64 or > 1_000_000)
        {
            failures.Add("HistoryRewrite:MaximumOutputTokens must be between 64 and 1,000,000.");
        }

        if (rewrite.MaximumPlannerRequestBytes is < 16 * 1024 or > 64 * 1024 * 1024)
        {
            failures.Add("HistoryRewrite:MaximumPlannerRequestBytes must be between 16 KiB and 64 MiB.");
        }

        if (rewrite.MaximumResponseBodyBytes is < 1024 or > 16 * 1024 * 1024)
        {
            failures.Add("HistoryRewrite:MaximumResponseBodyBytes must be between 1 KiB and 16 MiB.");
        }

        if (string.IsNullOrWhiteSpace(rewrite.AnthropicVersion)
            || rewrite.AnthropicVersion.Any(char.IsControl))
        {
            failures.Add("HistoryRewrite:AnthropicVersion must be a non-empty header value.");
        }

        ValidateCache(rewrite.Cache, failures);

        string[] forbiddenHeaders =
        [
            "Content-Length",
            "Host",
            "Transfer-Encoding",
            HistoryRewriteOptions.InternalRequestHeaderName,
        ];
        foreach ((string name, string value) in rewrite.Headers)
        {
            if (string.IsNullOrWhiteSpace(name)
                || name.Any(character => !IsHeaderTokenCharacter(character))
                || value.Any(char.IsControl))
            {
                failures.Add("HistoryRewrite:Headers contains an invalid header name or value.");
            }
            else if (forbiddenHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"HistoryRewrite:Headers must not configure {name}.");
            }
        }
    }

    private static void ValidateCache(
        HistoryRewriteCacheOptions cache,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(cache.DatabasePath)
            || cache.DatabasePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            failures.Add("HistoryRewrite:Cache:DatabasePath must be a valid non-empty file path.");
        }

        if (cache.Generation < 1)
        {
            failures.Add("HistoryRewrite:Cache:Generation must be at least 1.");
        }

        if (cache.MaximumBytes is < 64L * 1024 * 1024 or > 1024L * 1024 * 1024 * 1024)
        {
            failures.Add("HistoryRewrite:Cache:MaximumBytes must be between 64 MiB and 1 TiB.");
        }

        if (cache.TimeToLive < TimeSpan.FromSeconds(1)
            || cache.TimeToLive > TimeSpan.FromDays(365))
        {
            failures.Add("HistoryRewrite:Cache:TimeToLive must be between 1 second and 365 days.");
        }

        if (cache.CleanupInterval < TimeSpan.FromSeconds(1)
            || cache.CleanupInterval > TimeSpan.FromDays(1))
        {
            failures.Add("HistoryRewrite:Cache:CleanupInterval must be between 1 second and 1 day.");
        }

        if (cache.BusyTimeout < TimeSpan.FromMilliseconds(100)
            || cache.BusyTimeout > TimeSpan.FromMinutes(1))
        {
            failures.Add("HistoryRewrite:Cache:BusyTimeout must be between 100 milliseconds and 1 minute.");
        }
    }

    private static bool IsHeaderTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
        || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-'
            or '.' or '^' or '_' or '`' or '|' or '~';

    private static void ValidateOrigin(
        string name,
        string value,
        PinkUnicornOptions options,
        List<string> failures)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            failures.Add($"Upstreams:{name} must be an absolute HTTP(S) origin without a query or fragment.");
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            failures.Add($"Upstreams:{name} must not contain URI user information.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !options.AllowInsecureUpstreams)
        {
            failures.Add($"Upstreams:{name} must use HTTPS unless AllowInsecureUpstreams is enabled.");
        }

        string host = uri.IdnHost.Trim('[', ']');
        bool isLocalName = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        bool isPrivateAddress = IPAddress.TryParse(host, out IPAddress? address)
            && IsPrivateOrReserved(address);
        if ((isLocalName || isPrivateAddress) && !options.AllowPrivateUpstreams)
        {
            failures.Add($"Upstreams:{name} resolves literally to a private/reserved target; enable AllowPrivateUpstreams deliberately.");
        }
    }

    private static bool IsPrivateOrReserved(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return bytes[0] switch
        {
            0 or 10 or 127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] is >= 16 and <= 31 => true,
            192 when bytes[1] == 168 => true,
            >= 224 => true,
            _ => false,
        };
    }
}
