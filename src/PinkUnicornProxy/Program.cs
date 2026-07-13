using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PinkUnicornProxy.Configuration;
using PinkUnicornProxy.Proxy;
using PinkUnicornProxy.Rewriting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
int maximumConcurrentRequests = Math.Clamp(
    builder.Configuration.GetValue<int?>(
        $"{PinkUnicornOptions.SectionName}:MaximumConcurrentRequests") ?? 32,
    1,
    10_000);

builder.Services
    .AddOptions<PinkUnicornOptions>()
    .Bind(builder.Configuration.GetSection(PinkUnicornOptions.SectionName))
    .PostConfigure(options =>
    {
        if (options.HistoryRewrite.TriggerTokens.Length == 0)
        {
            options.HistoryRewrite.TriggerTokens = ["!!NO!!"];
        }
    })
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PinkUnicornOptions>, PinkUnicornOptionsValidator>();
builder.Services.AddHttpForwarder();
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    rateLimiter.AddConcurrencyLimiter("provider-proxy", limiter =>
    {
        limiter.PermitLimit = maximumConcurrentRequests;
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});
builder.Services.AddHttpClient("history-rewriter", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
    AutomaticDecompression = DecompressionMethods.None,
    UseCookies = false,
});
builder.Services.AddSingleton<IHistoryEditPlanner, LlmHistoryEditPlanner>();
builder.Services.AddSingleton<HistoryRewriteCache>();
builder.Services.AddHostedService(static serviceProvider =>
    serviceProvider.GetRequiredService<HistoryRewriteCache>());
builder.Services.AddSingleton<ConversationRewriter>();
builder.Services.AddSingleton<ProviderProxy>();
builder.Services.AddSingleton(static serviceProvider =>
{
    PinkUnicornOptions options = serviceProvider.GetRequiredService<IOptions<PinkUnicornOptions>>().Value;
    return new HttpMessageInvoker(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.ConnectTimeout,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            UseCookies = false,
        },
        disposeHandler: true);
});

WebApplication app = builder.Build();
app.UseRateLimiter();

app.MapGet("/", () => Results.Ok(new
{
    name = "Pink Unicorn Proxy",
    openAIBaseUrl = "/openai/v1",
    anthropicBaseUrl = "/anthropic",
    health = "/health/live",
}));

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", (HistoryRewriteCache cache) =>
    cache.IsHealthy
        ? Results.Json(new { status = "ready" })
        : Results.Json(
            new { status = "degraded", reason = "history-rewrite-cache" },
            statusCode: StatusCodes.Status503ServiceUnavailable));

app.Map("/openai/{**path}", static context =>
    context.RequestServices.GetRequiredService<ProviderProxy>().ForwardOpenAIAsync(context))
    .RequireRateLimiting("provider-proxy");
app.Map("/anthropic/{**path}", static context =>
    context.RequestServices.GetRequiredService<ProviderProxy>().ForwardAnthropicAsync(context))
    .RequireRateLimiting("provider-proxy");

app.Run();

public partial class Program;
