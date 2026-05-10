using Hook.Features.ContactSharing.ExchangePhones;
using Hook.Shared.Core;

namespace Hook.Features.ContactSharing;

public static class ContactSharingServiceCollectionExtensions
{
    public static IServiceCollection AddContactSharing(this IServiceCollection services)
    {
        services.AddScoped<PhoneExchanger>();
        services.AddScoped<WolverineEventPublisher>();
        services.AddScoped<IEventPublisher>(sp => sp.GetRequiredService<WolverineEventPublisher>());
        services.AddScoped<IEventBus>(sp => sp.GetRequiredService<WolverineEventPublisher>());
        return services;
    }
}
