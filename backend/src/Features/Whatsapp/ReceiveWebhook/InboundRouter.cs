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
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed class InboundRouterHandler(
    IClientRequestDraftRepository clientDrafts,
    IRegistrationDraftRepository registrationDrafts,
    IAmbiguousIntentDraftRepository ambiguousDrafts,
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
    TimeProvider clock,
    ILogger<InboundRouterHandler> logger)
{
    // Below this confidence we send a disambiguation prompt instead of routing
    // straight to ClientRequestOrchestrator or RegistrationOrchestrator.
    private const double AmbiguityConfidenceThreshold = 0.6;
    private static readonly TimeSpan AmbiguousDraftTtl = TimeSpan.FromMinutes(5);

    internal const string DisambiguationPrompt =
        "Quick check: do you want to HIRE someone (you need a service) or OFFER your services (you provide one)? Reply HIRE or OFFER.";

    public async Task Handle(InboundMessageReceived evt, CancellationToken ct)
    {
        var msg = evt.Message;
        var phone = msg.From.Value;
        var text = msg.Text ?? string.Empty;
        var masked = msg.From.Mask();

        logger.LogDebug("Routing inbound {MessageId} from {From} kind={Kind}", msg.MessageId, masked, msg.Kind);

        if (QuickIntent.Detect(text) == IntentKind.Cancel)
        {
            if (await AbandonAsync(phone, msg.From, ct)) return;
        }

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

        if (await TryResolveAmbiguousAsync(msg, text, masked, ct)) return;

        var hint = QuickIntent.DetectIntentHint(text);
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

        if (activeRequest is not null && QuickIntent.Detect(text) is IntentKind.Confirmation)
        {
            var quick = new IntentDetectionResult(IntentKind.Confirmation, 1.0, "en", "quick");
            logger.LogDebug("Route → ShareTopOrAskAsync (yes after present) for {Phone}", masked);
            await ShareTopOrAskAsync(activeRequest, msg.From, text, quick, masked, ct);
            return;
        }

        // Deterministic hint short-circuits the LLM intent call entirely.
        var detected = hint is { } h
            ? new IntentDetectionResult(h, 1.0, "en", "hint")
            : await intent.GetAsync(ct);

        if (hint is null
            && detected.Intent is IntentKind.ServiceRequest or IntentKind.ProviderRegistration
            && detected.Confidence < AmbiguityConfidenceThreshold
            && (activeRequest is null || activeRequest.Status != ServiceRequestStatus.Open))
        {
            logger.LogDebug("Low-confidence intent={Intent} conf={Conf:F2} → disambiguating for {Phone}",
                detected.Intent, detected.Confidence, masked);
            await ambiguousDrafts.UpsertAsync(
                AmbiguousIntentDraft.Start(phone, text, clock.GetUtcNow()), ct);
            await whatsapp.SendTextAsync(msg.From, DisambiguationPrompt, ct);
            return;
        }

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
            case IntentKind.ShareContact when activeRequest is not null:
                await ShareTopOrAskAsync(activeRequest, msg.From, text, detected, masked, ct);
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
            case IntentKind.Cancel:
                await AbandonAsync(phone, msg.From, ct);
                return;
            default:
                logger.LogDebug("No route for inbound from {Phone}, intent={Intent}", masked, detected.Intent);
                return;
        }
    }

    /// <summary>
    /// If a pending AmbiguousIntentDraft exists for this phone, treat the current
    /// message as the user's disambiguation answer ("1"/"2") and replay the original
    /// message into the chosen orchestrator. Stale drafts (older than TTL) are
    /// dropped and treated as no-op so normal classification proceeds.
    /// Returns true if the message was consumed (caller must stop processing).
    /// </summary>
    private async Task<bool> TryResolveAmbiguousAsync(
        InboundMessage msg, string text, string masked, CancellationToken ct)
    {
        var draft = await ambiguousDrafts.GetAsync(msg.From.Value, ct);
        if (draft is null) return false;

        if (clock.GetUtcNow() - draft.CreatedAt > AmbiguousDraftTtl)
        {
            logger.LogDebug("Stale ambiguous draft for {Phone}, dropping", masked);
            await ambiguousDrafts.DeleteAsync(msg.From.Value, ct);
            return false;
        }

        var choice = ParseDisambiguation(text);
        if (choice is null)
        {
            logger.LogDebug("Unrecognised disambiguation reply '{Text}' from {Phone}", text, masked);
            await whatsapp.SendTextAsync(msg.From, "Reply HIRE if you need a service, or OFFER if you provide one.", ct);
            return true;
        }

        await ambiguousDrafts.DeleteAsync(msg.From.Value, ct);
        var replay = msg with { Text = draft.OriginalText };

        if (choice == IntentKind.ServiceRequest)
        {
            logger.LogDebug("Disambiguated → ClientRequestOrchestrator for {Phone}", masked);
            await clientOrchestrator.HandleAsync(replay, ct);
        }
        else
        {
            logger.LogDebug("Disambiguated → RegistrationOrchestrator for {Phone}", masked);
            await registrationOrchestrator.HandleAsync(replay, ct);
        }
        return true;
    }

    private static IntentKind? ParseDisambiguation(string text)
    {
        var t = text.Trim().ToLowerInvariant().TrimEnd('.', '!', '?');
        return t switch
        {
            "1" or "1." or "client" or "need help" or "help" or "looking"
                or "hire" or "need service" or "need provider" or "i need" => IntentKind.ServiceRequest,
            "2" or "2." or "provider" or "offer" or "offering" or "i offer"
                or "list" or "register" or "i'm a provider" or "im a provider" => IntentKind.ProviderRegistration,
            _ => null
        };
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
        var fallback = purpose == "greeting-reply"
            ? "Hi! I connect people with local service providers. Reply 'I need …' to hire someone, or 'I offer …' to list a service."
            : "I help connect people who need services with providers. Reply 'I need …' if you need help, or 'I offer …' to list a service.";
        var reply = await AiReplyHelper.TryGenerateOrFallbackAsync(ai, ctx, purpose, fallback, logger, ct);
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

    private async Task ShareTopOrAskAsync(
        ServiceRequest.RequestAggregate.ServiceRequest request,
        PhoneNumber from,
        string text,
        IntentDetectionResult detected,
        string maskedPhone,
        CancellationToken ct)
    {
        var matchOrder = await matches.GetForRequestAsync(request.Id, ct);
        if (matchOrder.Count == 0)
        {
            logger.LogDebug("ShareContact with no matches for {Phone}; falling back to cold reply", maskedPhone);
            await SendColdReplyAsync(from, text, detected, "out-of-scope", ct);
            return;
        }

        if (matchOrder.Count == 1)
        {
            logger.LogDebug("Route → PhoneExchanger.TryExchange (free-text share) match={MatchId} for {Phone}",
                matchOrder[0].Id, maskedPhone);
            await phoneExchanger.TryExchangeAsync(matchOrder[0].Id, ct);
            return;
        }

        await whatsapp.SendTextAsync(from,
            $"Which match? Reply 1, 2, or {matchOrder.Count}.", ct);
    }

    private async Task<bool> AbandonAsync(string phone, PhoneNumber from, CancellationToken ct)
    {
        // No locking around Get→Delete — drafts are short-lived and duplicate inbound delivery is rare.
        // Revisit if horizontal scaling is introduced.
        var hadRegDraft       = await registrationDrafts.GetAsync(phone, ct) is not null;
        var hadClientDraft    = await clientDrafts.GetAsync(phone, ct) is not null;
        var hadAmbiguousDraft = await ambiguousDrafts.GetAsync(phone, ct) is not null;
        if (!hadRegDraft && !hadClientDraft && !hadAmbiguousDraft) return false;

        await whatsapp.SendTextAsync(from, "Session ended. Send a new message to start over.", ct);

        if (hadRegDraft)       await registrationDrafts.DeleteAsync(phone, ct);
        if (hadClientDraft)    await clientDrafts.DeleteAsync(phone, ct);
        if (hadAmbiguousDraft) await ambiguousDrafts.DeleteAsync(phone, ct);

        logger.LogDebug("Abandoned active draft for {Phone} (reg={Reg}, client={Client}, ambig={Ambig})",
            from.Mask(), hadRegDraft, hadClientDraft, hadAmbiguousDraft);
        return true;
    }
}
