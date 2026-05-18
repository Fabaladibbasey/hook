using System.Net;
using System.Threading.RateLimiting;
using Hook.Features.ChatSession;
using Hook.Features.Whatsapp.ReceiveWebhook;

namespace Hook.Features.RateLimiting;

internal static class GlobalRateLimitPartitioner
{
    internal const string BypassPartitionKey = "_bypass";
    internal const int MaxTokenLength = 128;

    public static Func<HttpContext, RateLimitPartition<string>> Build(
        RateLimitOptions opts,
        IReadOnlySet<string> bypassHosts)
    {
        var window = new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromSeconds(opts.GlobalWindowSeconds),
            PermitLimit = opts.GlobalPermitLimit,
            QueueLimit = opts.GlobalQueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        };

        return ctx =>
        {
            var path = ctx.Request.Path;

            // Highest-traffic branches first: Meta webhook + chat hub.
            if (path.StartsWithSegments(ReceiveWebhookEndpoint.Path))
                return RateLimitPartition.GetNoLimiter(BypassPartitionKey);

            if (path.StartsWithSegments(ChatHubConstants.HubPath))
                return RateLimitPartition.GetNoLimiter(BypassPartitionKey);

            // YARP-proxied internal UIs (e.g. Seq): authenticated edge, asset-heavy.
            var host = ctx.Request.Host.Host.TrimEnd('.');
            if (bypassHosts.Contains(host))
                return RateLimitPartition.GetNoLimiter(BypassPartitionKey);

            var key = BuildPartitionKey(ctx.Request.Query["token"].ToString(), ctx.Connection.RemoteIpAddress);
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => window);
        };
    }

    internal static string BuildPartitionKey(string token, IPAddress? remoteIp)
    {
        if (!string.IsNullOrWhiteSpace(token) && token.Length <= MaxTokenLength)
            return "t:" + token;

        return "ip:" + (remoteIp?.ToString() ?? "unknown");
    }
}
