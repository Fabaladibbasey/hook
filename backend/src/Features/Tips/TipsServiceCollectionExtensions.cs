namespace Hook.Features.Tips;

public static class TipsServiceCollectionExtensions
{
    public static IServiceCollection AddTips(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<TipOptions>(configuration);
        // Scoped because TipPicker depends on the scoped IWhatsappContactRepository
        // (which holds the scoped HookDbContext). Wolverine handlers resolve from a
        // per-envelope scope so this matches the SendWhatsAppTextHandler lifetime.
        services.AddScoped<ITipPicker, TipPicker>();
        return services;
    }
}
