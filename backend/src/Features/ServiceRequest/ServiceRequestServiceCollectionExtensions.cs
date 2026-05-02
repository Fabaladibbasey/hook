using Hook.Features.ServiceRequest.Create;
using Hook.Features.ServiceRequest.IterateMatches;
using Hook.Features.ServiceRequest.RequestAggregate;

namespace Hook.Features.ServiceRequest;

public static class ServiceRequestServiceCollectionExtensions
{
    public static IServiceCollection AddServiceRequest(this IServiceCollection services)
    {
        services.AddScoped<IServiceRequestRepository, ServiceRequestRepository>();
        services.AddScoped<IClientRequestDraftRepository, ClientRequestDraftRepository>();
        services.AddScoped<ClientRequestOrchestrator>();
        services.AddScoped<IterationCoordinator>();

        return services;
    }
}
