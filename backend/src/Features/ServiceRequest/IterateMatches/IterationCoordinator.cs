using Hook.Features.Matching;
using Hook.Features.Matching.Match;
using Hook.Features.Matching.PresentMatches;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Options;

namespace Hook.Features.ServiceRequest.IterateMatches;

public sealed class IterationCoordinator(
    MatchingService matching,
    IServiceRequestRepository requests,
    MatchPresenter presenter,
    IWhatsappClient whatsapp,
    IOptions<MatchingOptions> options,
    ILogger<IterationCoordinator> logger)
{
    public async Task NextAsync(PhoneNumber clientPhone, CancellationToken ct = default)
    {
        var request = await requests.GetActiveByClientAsync(clientPhone.Value, ct);
        if (request is null)
        {
            await whatsapp.SendTextAsync(clientPhone,
                "No active request. Reply with what service you need (e.g. 'I need a plumber') to start a new one.", ct);
            return;
        }

        var batch = await matching.RunForRequestAsync(request.Id, ct);
        if (batch is null) return;

        if (batch.Scored.Count > 0)
        {
            await presenter.PresentAsync(clientPhone, batch, request.ServiceSlug, ct);
            return;
        }

        await HandleExhaustionAsync(clientPhone, request, ct);
    }

    public async Task IncreaseAsync(PhoneNumber clientPhone, CancellationToken ct = default)
    {
        var request = await requests.GetActiveByClientAsync(clientPhone.Value, ct);
        if (request is null) return;

        var opts = options.Value;
        if (request.CurrentRadiusKm >= opts.MaxRadiusKm)
        {
            await whatsapp.SendTextAsync(clientPhone,
                $"No providers found in {opts.MaxRadiusKm}km. Try a different service or check back later.", ct);
            return;
        }

        request.CurrentRadiusKm = Math.Min(request.CurrentRadiusKm * opts.RadiusExpansionFactor, opts.MaxRadiusKm);

        var batch = await matching.RunForRequestAsync(request.Id, ct);
        if (batch is null || batch.Scored.Count == 0)
        {
            await HandleExhaustionAsync(clientPhone, request, ct);
            return;
        }

        await whatsapp.SendTextAsync(clientPhone, $"Showing matches within {request.CurrentRadiusKm:F0}km (wider range):", ct);
        await presenter.PresentAsync(clientPhone, batch, request.ServiceSlug, ct);
    }

    private async Task HandleExhaustionAsync(PhoneNumber clientPhone, ServiceRequest.RequestAggregate.ServiceRequest request, CancellationToken ct)
    {
        var opts = options.Value;
        if (request.CurrentRadiusKm >= opts.MaxRadiusKm)
        {
            await whatsapp.SendTextAsync(clientPhone,
                $"No providers found in {opts.MaxRadiusKm}km. Try a different service or check back later.", ct);
            return;
        }

        if (!request.AutoExpandedOnce)
        {
            var nextRadius = Math.Min(request.CurrentRadiusKm * opts.RadiusExpansionFactor, opts.MaxRadiusKm);
            request.CurrentRadiusKm = nextRadius;
            request.AutoExpandedOnce = true;

            var second = await matching.RunForRequestAsync(request.Id, ct);
            if (second is { Scored.Count: > 0 })
            {
                await whatsapp.SendTextAsync(clientPhone, $"Showing matches within {nextRadius:F0}km (wider range):", ct);
                await presenter.PresentAsync(clientPhone, second, request.ServiceSlug, ct);
                return;
            }
        }

        var followingRadius = Math.Min(request.CurrentRadiusKm * opts.RadiusExpansionFactor, opts.MaxRadiusKm);
        await whatsapp.SendTextAsync(clientPhone,
            $"No more in {request.CurrentRadiusKm:F0}km. Reply INCREASE for {followingRadius:F0}km, or NEW for different service.", ct);
        logger.LogDebug("Iteration exhausted for {Phone} at {Radius}km", clientPhone.Mask(), request.CurrentRadiusKm);
    }
}
