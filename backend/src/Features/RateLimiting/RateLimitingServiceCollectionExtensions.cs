namespace Hook.Features.RateLimiting;

public static class RateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<RateLimitOptions>(configuration);

        services.AddSingleton<PerPhoneLimiter>();
        return services;
    }
}
