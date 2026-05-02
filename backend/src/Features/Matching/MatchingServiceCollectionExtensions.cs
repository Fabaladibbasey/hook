using Hook.Features.Matching.Match;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Matching.PresentMatches;

namespace Hook.Features.Matching;

public static class MatchingServiceCollectionExtensions
{
    public static IServiceCollection AddMatching(this IServiceCollection services)
    {
        services.AddScoped<IMatchRepository, MatchRepository>();
        services.AddScoped<IProviderQueryService, PostgresProviderQueryService>();
        services.AddScoped<MatchScorer>();
        services.AddScoped<MatchingService>();
        services.AddScoped<MatchPresenter>();

        return services;
    }
}
