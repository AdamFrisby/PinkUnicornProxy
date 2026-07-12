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

        if (options.MaximumCorrectionTextCharacters is < 128 or > 1024 * 1024)
        {
            failures.Add("MaximumCorrectionTextCharacters must be between 128 and 1,048,576.");
        }

        if (options.MaximumCorrectionsPerRequest is < 1 or > 1_000)
        {
            failures.Add("MaximumCorrectionsPerRequest must be between 1 and 1,000.");
        }

        if (options.MaximumConcurrentRequests is < 1 or > 10_000)
        {
            failures.Add("MaximumConcurrentRequests must be between 1 and 10,000.");
        }

        if (!Enum.IsDefined(options.Mode))
        {
            failures.Add("Mode is not a defined ProxyMode value.");
        }

        if (!Enum.IsDefined(options.StatefulResponsesPolicy))
        {
            failures.Add("StatefulResponsesPolicy is not a defined value.");
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

        ValidateOrigin(nameof(options.Upstreams.OpenAI), options.Upstreams.OpenAI, options, failures);
        ValidateOrigin(nameof(options.Upstreams.Anthropic), options.Upstreams.Anthropic, options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

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
