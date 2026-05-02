namespace Hook.Features.ChatSession;

public static class ChatServiceCollectionExtensions
{
    public static IServiceCollection AddChat(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<ChatOptions>(configuration);

        services.AddScoped<IChatRepository, ChatRepository>();
        services.AddScoped<ChatSessionFactory>();

        services.AddSignalR();

        return services;
    }
}
