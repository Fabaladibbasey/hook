using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.ContactSharing.ExchangePhones;
using Hook.Features.Feedback;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ServiceRequest.Create;
using Hook.Features.ServiceRequest.IterateMatches;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Phone;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed class InboundRouterHandler(
    IClientRequestDraftRepository clientDrafts,
    IRegistrationDraftRepository registrationDrafts,
    IServiceRequestRepository requests,
    IFeedbackRepository feedback,
    IMatchRepository matches,
    IProviderAvailabilityRepository providers,
    IConversationAi ai,
    IWhatsappClient whatsapp,
    ClientRequestOrchestrator clientOrchestrator,
    RegistrationOrchestrator registrationOrchestrator,
    IterationCoordinator iterationCoordinator,
    PhoneExchanger phoneExchanger,
    FeedbackResponseService feedbackService,
    ILogger<InboundRouterHandler> logger)
{
    public async Task Handle(InboundMessageReceived evt, CancellationToken ct)
    {
        var msg = evt.Message;
        var phone = msg.From.Value;
        var text = msg.Text ?? string.Empty;
        var masked = msg.From.Mask();

        logger.LogDebug("Routing inbound {MessageId} from {From} kind={Kind}", msg.MessageId, masked, msg.Kind);

        if (await registrationDrafts.GetAsync(phone, ct) is not null)
        {
            logger.LogDebug("Route → RegistrationOrchestrator (active reg draft) for {Phone}", masked);
            await registrationOrchestrator.HandleAsync(msg, ct);
            return;
        }

        if (await clientDrafts.GetAsync(phone, ct) is not null)
        {
            logger.LogDebug("Route → ClientRequestOrchestrator (active client draft) for {Phone}", masked);
            await clientOrchestrator.HandleAsync(msg, ct);
            return;
        }

        var intent = new LazyIntent(ai, text);

        if (await feedback.GetLatestPendingForClientAsync(phone, ct) is { } pendingFeedback)
        {
            logger.LogDebug("Route → FeedbackResponseService (pending feedback) for {Phone}", masked);
            await feedbackService.HandleAsync(msg, pendingFeedback, intent, ct);
            return;
        }

        var activeRequest = await requests.GetActiveByClientAsync(phone, ct);

        if (activeRequest is not null && PickProviderResolver.PickRegex.IsMatch(text))
        {
            await TryPickAsync(activeRequest, text, masked, ct);
            return;
        }

        var detected = await intent.GetAsync(ct);
        switch (detected.Intent)
        {
            case IntentKind.NextMatches when activeRequest is not null:
                logger.LogDebug("Route → IterationCoordinator.Next for {Phone}", masked);
                await iterationCoordinator.NextAsync(msg.From, ct);
                return;
            case IntentKind.IncreaseRange when activeRequest is not null:
                logger.LogDebug("Route → IterationCoordinator.Increase for {Phone}", masked);
                await iterationCoordinator.IncreaseAsync(msg.From, ct);
                return;
            case IntentKind.MatchSelection when activeRequest is not null:
                await TryPickAsync(activeRequest, text, masked, ct);
                return;
            case IntentKind.ServiceRequest when activeRequest?.Status != ServiceRequestStatus.Open:
                logger.LogDebug("Route → ClientRequestOrchestrator (new request) for {Phone}", masked);
                await clientOrchestrator.HandleAsync(msg, ct);
                return;
            case IntentKind.ProviderRegistration:
                logger.LogDebug("Route → RegistrationOrchestrator (new/heartbeat) for {Phone}", masked);
                await registrationOrchestrator.HandleAsync(msg, ct);
                return;
            case IntentKind.Greeting:
            case IntentKind.Unknown:
                if (await providers.GetAsync(phone, ct) is not null)
                {
                    logger.LogDebug("Silent heartbeat for listed provider {Phone}", masked);
                    await registrationOrchestrator.HandleAsync(msg, ct);
                    return;
                }
                var purpose = detected.Intent == IntentKind.Greeting ? "greeting-reply" : "out-of-scope";
                logger.LogDebug("Cold reply ({Purpose}) for {Phone} intent={Intent}", purpose, masked, detected.Intent);
                await SendColdReplyAsync(msg.From, text, detected, purpose, ct);
                return;
            default:
                logger.LogDebug("No route for inbound from {Phone}, intent={Intent}", masked, detected.Intent);
                return;
        }
    }

    private async Task SendColdReplyAsync(PhoneNumber from, string text, IntentDetectionResult detected, string purpose, CancellationToken ct)
    {
        var ctx = new ReplyContext(
            Purpose: purpose,
            RecentTurns: new[] { new ConversationTurn(TurnRole.User, text) },
            LanguageHint: detected.LanguageCode,
            Facts: new Dictionary<string, string>
            {
                ["intent"] = detected.Intent.ToString()
            });
        var reply = await AiReplyHelper.TryGenerateAsync(ai, ctx, purpose, logger, ct);
        if (reply is null) return;
        await whatsapp.SendTextAsync(from, reply, ct);
    }

    private async Task TryPickAsync(ServiceRequest.RequestAggregate.ServiceRequest request, string text, string maskedPhone, CancellationToken ct)
    {
        var matchOrder = await matches.GetForRequestAsync(request.Id, ct);
        if (matchOrder.Count == 0) return;

        var picked = PickProviderResolver.Resolve(text, matchOrder);
        if (picked is null) return;

        logger.LogDebug("Route → PhoneExchanger.TryExchange match={MatchId} for {Phone}", picked.Id, maskedPhone);
        await phoneExchanger.TryExchangeAsync(picked.Id, ct);
    }
}
