using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;

namespace Hook.Features.ChatSession;

public static class ChatServiceCollectionExtensions
{
    public static IServiceCollection AddChat(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<ChatOptions>(configuration);

        services.AddScoped<IChatRepository, ChatRepository>();
        services.AddScoped<ChatSessionFactory>();

        services.AddSingleton<ChatHubExceptionFilter>();
        services.AddSingleton<ChatHubMessageLimiter>();
        services.AddSingleton<Hook.Features.RateLimiting.ISweepableLimiter>(
            sp => sp.GetRequiredService<ChatHubMessageLimiter>());

        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaximumReceiveMessageSize = 32 * 1024;
            options.AddFilter<ChatHubExceptionFilter>();
        })
        .AddJsonProtocol(opts =>
            opts.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        return services;
    }
}
