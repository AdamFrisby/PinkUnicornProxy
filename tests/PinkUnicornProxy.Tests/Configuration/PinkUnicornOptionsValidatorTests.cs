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
            HistoryRewrite = new HistoryRewriteOptions
            {
                Protocol = (HistoryRewriterProtocol)999,
                OpenAIOutputTokenParameter = (OpenAIOutputTokenParameter)999,
            },
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Mode", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("HistoryRewrite:Protocol", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("OpenAIOutputTokenParameter", StringComparison.Ordinal));
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

    [Fact]
    public void EmptyDuplicateAndWhitespaceTriggerTokensAreRejected()
    {
        PinkUnicornOptions options = new();
        options.HistoryRewrite.TriggerTokens = ["!!NO!!", " ", "!!NO!!"];

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("non-whitespace", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void RewriterEndpointAndModelMustBeConfiguredTogether()
    {
        PinkUnicornOptions options = new();
        options.HistoryRewrite.Endpoint = "https://example.test/v1/chat/completions";

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("configured together", StringComparison.Ordinal));
    }

    [Fact]
    public void LoopbackHttpRewriterIsAllowedForLocalModels()
    {
        PinkUnicornOptions options = new();
        options.HistoryRewrite.Endpoint = "http://127.0.0.1:11434/v1/chat/completions";
        options.HistoryRewrite.Model = "local-model";

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void RemoteHttpRewriterRequiresExplicitInsecureFlag()
    {
        PinkUnicornOptions options = new();
        options.HistoryRewrite.Endpoint = "http://rewriter.example.test/v1/chat/completions";
        options.HistoryRewrite.Model = "small-model";

        Assert.True(validator.Validate(null, options).Failed);

        options.AllowInsecureUpstreams = true;

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void InvalidOrReservedRewriterHeadersAreRejected()
    {
        PinkUnicornOptions options = new();
        options.HistoryRewrite.Headers["Bad:Header"] = "value";
        options.HistoryRewrite.Headers[HistoryRewriteOptions.InternalRequestHeaderName] = "override";

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("invalid header", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains(
            HistoryRewriteOptions.InternalRequestHeaderName,
            StringComparison.Ordinal));
    }

    [Fact]
    public void CacheBoundsAreValidatedEvenWhenCacheIsDisabled()
    {
        PinkUnicornOptions options = new();
        options.HistoryRewrite.Cache.Enabled = false;
        options.HistoryRewrite.Cache.DatabasePath = " ";
        options.HistoryRewrite.Cache.Generation = 0;
        options.HistoryRewrite.Cache.MaximumBytes = 10;
        options.HistoryRewrite.Cache.TimeToLive = TimeSpan.FromMilliseconds(100);
        options.HistoryRewrite.Cache.CleanupInterval = TimeSpan.Zero;
        options.HistoryRewrite.Cache.BusyTimeout = TimeSpan.Zero;

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("DatabasePath", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("Generation", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("MaximumBytes", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("TimeToLive", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("CleanupInterval", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("BusyTimeout", StringComparison.Ordinal));
    }
}
