using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hook.Shared.Retention;

public static class RetentionServiceCollectionExtensions
{
    public static IServiceCollection AddRetentionSweeper(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddValidatedOptions<RetentionOptions>(configuration);
        services.AddSingleton<IValidateOptions<RetentionOptions>, RetentionOptionsValidator>();
        services.AddScoped<IRetentionSweeper, RetentionSweeper>();
        services.AddHostedService<RetentionHostedService>();
        services.AddHostedService<WolverineDlqIndexBootstrap>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
