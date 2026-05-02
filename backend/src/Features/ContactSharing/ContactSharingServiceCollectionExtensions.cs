using Hook.Features.ContactSharing.ExchangePhones;

namespace Hook.Features.ContactSharing;

public static class ContactSharingServiceCollectionExtensions
{
    public static IServiceCollection AddContactSharing(this IServiceCollection services)
    {
        services.AddScoped<PhoneExchanger>();
        return services;
    }
}
