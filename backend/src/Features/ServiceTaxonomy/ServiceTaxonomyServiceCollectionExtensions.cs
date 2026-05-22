using Hook.Features.ServiceTaxonomy.JudgeParent;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.ServiceTaxonomy.SeedRoots;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;

namespace Hook.Features.ServiceTaxonomy;

public static class ServiceTaxonomyServiceCollectionExtensions
{
    public static IServiceCollection AddServiceTaxonomy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<ServiceTaxonomyOptions>(configuration);

        services.AddScoped<IServiceRepository, ServiceRepository>();
        services.AddScoped<SlugResolver>();
        services.AddScoped<RootSectorSeeder>();
        services.AddScoped<IJudgeParentDedupGate, JudgeParentDedupGate>();

        return services;
    }
}
