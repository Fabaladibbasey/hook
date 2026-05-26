using Hook.Features.Ai.PlatformQa;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;

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
        // Ollama is local + single-tenant; slow CPU inference is not transient, so retries
        // just multiply the wait before AiReplyHelper drops the message. Strip retry/circuit-
        // breaker layers and keep a single timeout matching OllamaOptions.TimeoutSeconds.
        .AddResilienceHandler("ai.ollama", (builder, ctx) =>
        {
            var timeoutSeconds = ctx.ServiceProvider
                .GetRequiredService<IOptions<OllamaOptions>>().Value.TimeoutSeconds;
            builder.AddTimeout(TimeSpan.FromSeconds(timeoutSeconds));
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AiReadinessProbe>();

        services.AddPlatformQa(configuration);

        return services;
    }
}
