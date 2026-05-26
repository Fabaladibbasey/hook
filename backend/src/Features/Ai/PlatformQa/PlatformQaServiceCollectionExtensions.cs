namespace Hook.Features.Ai.PlatformQa;

public static class PlatformQaServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformQa(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<PlatformKnowledgeBaseOptions>(configuration);
        services.AddValidatedOptions<PlatformAnswerOptions>(configuration);
        services.AddSingleton<PlatformKnowledgeBase>();
        services.AddScoped<PlatformQaDispatcher>();
        services.AddScoped<IPlatformAnswerDedupGate, PlatformAnswerDedupGate>();
        return services;
    }
}
