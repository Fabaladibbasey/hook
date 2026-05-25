using System.Threading.RateLimiting;

namespace Hook.Features.RateLimiting;

public static class RateLimitingServiceCollectionExtensions
{
    public const string WebhookConcurrencyPolicy = "webhook-concurrency";

    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<RateLimitOptions>(configuration);

        services.AddSingleton<PerPhoneLimiter>();
        services.AddSingleton<ISweepableLimiter>(sp => sp.GetRequiredService<PerPhoneLimiter>());
        services.AddHostedService<LimiterEvictionHostedService>();
        return services;
    }

    public static IServiceCollection AddGlobalRateLimiter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var rateLimitOpts = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>()
            ?? new RateLimitOptions();

        var bypassHosts = rateLimitOpts.BypassHosts
            .Select(h => h.TrimEnd('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        services.AddRateLimiter(opts =>
        {
            opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            opts.GlobalLimiter = PartitionedRateLimiter.Create(
                GlobalRateLimitPartitioner.Build(rateLimitOpts, bypassHosts));

            opts.AddPolicy(WebhookConcurrencyPolicy, _ =>
                RateLimitPartition.GetConcurrencyLimiter(
                    GlobalRateLimitPartitioner.BypassPartitionKey,
                    _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = rateLimitOpts.WebhookConcurrencyLimit,
                        QueueLimit = rateLimitOpts.WebhookQueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
        });

        return services;
    }
}
