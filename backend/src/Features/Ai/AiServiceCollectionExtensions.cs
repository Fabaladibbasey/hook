using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hook.Features.Ai;

public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddConversationAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<OllamaOptions>(configuration);

        services.AddHttpClient<IConversationAi, OllamaConversationAi>(OllamaConversationAi.HttpClientName, (sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        })
        .AddStandardResilienceHandler(o =>
        {
            // The standard handler defaults (10s per attempt, 30s total) preempt the configured
            // HttpClient timeout, which causes cold-start CPU inference to be cancelled before
            // the model finishes generating. Scale the resilience timeouts off TimeoutSeconds so
            // configuration drives the real cap.
            var timeoutSeconds = configuration
                .GetSection(OllamaOptions.SectionName)
                .GetValue<int?>(nameof(OllamaOptions.TimeoutSeconds)) ?? 120;
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(timeoutSeconds * 3);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(timeoutSeconds * 2);
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AiReadinessProbe>();

        return services;
    }
}
