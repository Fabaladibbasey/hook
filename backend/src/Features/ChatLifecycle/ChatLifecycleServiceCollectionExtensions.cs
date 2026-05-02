namespace Hook.Features.ChatLifecycle;

public static class ChatLifecycleServiceCollectionExtensions
{
    public static IServiceCollection AddChatLifecycle(this IServiceCollection services)
    {
        services.AddScoped<ChatScheduler>();
        return services;
    }
}
