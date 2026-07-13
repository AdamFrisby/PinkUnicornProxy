namespace PinkUnicornProxy.Configuration;

internal sealed class PinkUnicornOptions
{
    public const string SectionName = "PinkUnicorn";

    public ProxyMode Mode { get; set; } = ProxyMode.Rewrite;

    public long MaximumRequestBodyBytes { get; set; } = 4 * 1024 * 1024;

    public int MaximumEditsPerRequest { get; set; } = 128;

    public int MaximumConcurrentRequests { get; set; } = 32;

    public bool AllowInsecureUpstreams { get; set; }

    public bool AllowPrivateUpstreams { get; set; }

    public string? AccessToken { get; set; }

    public TimeSpan ActivityTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public HistoryRewriteOptions HistoryRewrite { get; set; } = new();

    public UpstreamOptions Upstreams { get; set; } = new();
}

internal sealed class HistoryRewriteOptions
{
    public const string InternalRequestHeaderName = "X-Pink-Unicorn-Rewriter-Request";

    public string[] TriggerTokens { get; set; } = [];

    public HistoryRewriterProtocol Protocol { get; set; } = HistoryRewriterProtocol.OpenAIChatCompletions;

    public string? Endpoint { get; set; }

    public string? Model { get; set; }

    public string? ApiKey { get; set; }

    public string AnthropicVersion { get; set; } = "2023-06-01";

    public int MaximumOutputTokens { get; set; } = 4_096;

    public OpenAIOutputTokenParameter OpenAIOutputTokenParameter { get; set; } =
        OpenAIOutputTokenParameter.MaxCompletionTokens;

    public long MaximumPlannerRequestBytes { get; set; } = 8 * 1024 * 1024;

    public long MaximumResponseBodyBytes { get; set; } = 1024 * 1024;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HistoryRewriteCacheOptions Cache { get; set; } = new();
}

internal sealed class HistoryRewriteCacheOptions
{
    public bool Enabled { get; set; } = true;

    public string DatabasePath { get; set; } = "data/pink-unicorn-cache.db";

    public int Generation { get; set; } = 1;

    public long MaximumBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(10);
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

internal enum HistoryRewriterProtocol
{
    OpenAIChatCompletions,
    AnthropicMessages,
}

internal enum OpenAIOutputTokenParameter
{
    MaxCompletionTokens,
    MaxTokens,
    Omit,
}
