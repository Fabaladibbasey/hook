using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Dev;
using Hook.Features.ProviderAvailability.Refresh;
using Hook.Features.ProviderAvailability.Register;

namespace Hook.Features.ProviderAvailability;

public static class ProviderAvailabilityServiceCollectionExtensions
{
    public static IServiceCollection AddProviderAvailability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<ProviderAvailabilityOptions>(configuration);
        services.AddValidatedOptions<DevProviderSeedOptions>(configuration);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IProviderAvailabilityRepository, ProviderAvailabilityRepository>();
        services.AddScoped<IRegistrationDraftRepository, RegistrationDraftRepository>();
        services.AddScoped<RegistrationOrchestrator>();
        services.AddScoped<ProviderRefreshScheduler>();
        services.AddScoped<DevProviderSeeder>();

        return services;
    }
}
