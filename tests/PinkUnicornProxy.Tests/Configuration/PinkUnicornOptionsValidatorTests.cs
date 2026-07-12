using Microsoft.Extensions.Options;
using PinkUnicornProxy.Configuration;

namespace PinkUnicornProxy.Tests.Configuration;

public sealed class PinkUnicornOptionsValidatorTests
{
    private readonly PinkUnicornOptionsValidator validator = new();

    [Fact]
    public void DefaultsAreValid()
    {
        Assert.True(validator.Validate(null, new PinkUnicornOptions()).Succeeded);
    }

    [Fact]
    public void UndefinedEnumsAreRejected()
    {
        PinkUnicornOptions options = new()
        {
            Mode = (ProxyMode)999,
            StatefulResponsesPolicy = (StatefulResponsesPolicy)999,
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Mode", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("StatefulResponsesPolicy", StringComparison.Ordinal));
    }

    [Fact]
    public void InsecurePrivateUpstreamRequiresBothExplicitFlags()
    {
        PinkUnicornOptions options = new();
        options.Upstreams.OpenAI = "http://127.0.0.1:8080";

        Assert.True(validator.Validate(null, options).Failed);

        options.AllowInsecureUpstreams = true;
        options.AllowPrivateUpstreams = true;

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void UriUserInformationIsRejected()
    {
        PinkUnicornOptions options = new();
        options.Upstreams.Anthropic = "https://user:password@api.anthropic.com";

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("user information", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://[::1]")]
    [InlineData("https://[fc00::1]")]
    [InlineData("https://localhost")]
    public void PrivateIpv6AndLocalNamesAreRejected(string origin)
    {
        PinkUnicornOptions options = new();
        options.Upstreams.OpenAI = origin;

        Assert.True(validator.Validate(null, options).Failed);
    }
}
