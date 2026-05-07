using Hook.Features.ContactSharing.ExchangePhones;
using Hook.Shared.Core;

namespace Hook.Features.ContactSharing;

public static class ContactSharingServiceCollectionExtensions
{
    public static IServiceCollection AddContactSharing(this IServiceCollection services)
    {
        services.AddScoped<PhoneExchanger>();
        services.AddScoped<IEventPublisher, WolverineEventPublisher>();
        return services;
    }
}
