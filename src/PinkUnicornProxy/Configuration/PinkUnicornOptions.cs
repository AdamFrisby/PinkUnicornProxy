namespace PinkUnicornProxy.Configuration;

internal sealed class PinkUnicornOptions
{
    public const string SectionName = "PinkUnicorn";

    public ProxyMode Mode { get; set; } = ProxyMode.Rewrite;

    public long MaximumRequestBodyBytes { get; set; } = 4 * 1024 * 1024;

    public int MaximumCorrectionTextCharacters { get; set; } = 16 * 1024;

    public int MaximumCorrectionsPerRequest { get; set; } = 32;

    public int MaximumConcurrentRequests { get; set; } = 32;

    public StatefulResponsesPolicy StatefulResponsesPolicy { get; set; } = StatefulResponsesPolicy.Reject;

    public bool AllowOtherPaths { get; set; }

    public bool AllowInsecureUpstreams { get; set; }

    public bool AllowPrivateUpstreams { get; set; }

    public string? AccessToken { get; set; }

    public TimeSpan ActivityTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public UpstreamOptions Upstreams { get; set; } = new();
}

internal sealed class UpstreamOptions
{
    public string OpenAI { get; set; } = "https://api.openai.com";

    public string Anthropic { get; set; } = "https://api.anthropic.com";
}

internal enum ProxyMode
{
    Off,
    Audit,
    Rewrite,
}

internal enum StatefulResponsesPolicy
{
    PassThrough,
    Reject,
    FreshStart,
}
