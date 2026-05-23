using Hook.Features.Matching.Match;
using Hook.Features.ServiceRequest;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Matching.PresentMatches;

public sealed class ServiceRequestCreatedHandler(
    MatchingService matching,
    IServiceRequestRepository requests,
    MatchPresenter presenter,
    ILogger<ServiceRequestCreatedHandler> logger)
{
    public async Task Handle(ServiceRequestCreatedEvent evt, CancellationToken ct)
    {
        var batch = await matching.RunForRequestAsync(evt.RequestId, ct);
        if (batch is null)
        {
            logger.LogWarning("ServiceRequest {Id} not found at match time", evt.RequestId);
            return;
        }

        var request = await requests.GetAsync(evt.RequestId, ct);
        if (request is null) return;

        if (!PhoneNumber.TryParse(request.ClientPhone, out var clientPhone)) return;

        await presenter.PresentAsync(clientPhone, batch, request.ServiceSlug, ct);
    }
}
