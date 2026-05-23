using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.ContactSharing.ExchangePhones;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.MetaTemplates;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ServiceRequest.Create;
using Hook.Features.ServiceRequest.IterateMatches;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Features.Whatsapp.ReceiveWebhook.ClassifyInboundIntent;
using Hook.Features.Whatsapp.ReceiveWebhook.ColdReply;
using Hook.Shared.Messaging;
using Hook.Shared.Pipeline.PostCommitSends;
using Npgsql;
using Wolverine;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed class InboundRouterHandler(
    IClientRequestDraftRepository clientDrafts,
    IRegistrationDraftRepository registrationDrafts,
    IAmbiguousIntentDraftRepository ambiguousDrafts,
    IMatchRepository matches,
    IProviderAvailabilityRepository providers,
    InboundPrefetchRepository prefetch,
    IMessageBus bus,
    ClientRequestOrchestrator clientOrchestrator,
    RegistrationOrchestrator registrationOrchestrator,
    IterationCoordinator iterationCoordinator,
    PhoneExchanger phoneExchanger,
    FeedbackResponseService feedbackService,
    IWhatsappContactRepository contacts,
    TimeProvider clock,
    ILogger<InboundRouterHandler> logger)
{
    // Below this confidence we send a disambiguation prompt instead of routing
    // straight to ClientRequestOrchestrator or RegistrationOrchestrator. Tightened
    // from 0.6: post-greeting noise like "I want to be rich" used to pass at 0.6
    // and lock the contact into REQUEST. After the cold greeting, we keep intent
    // unclassified until a stronger signal arrives.
    private const double AmbiguityConfidenceThreshold = 0.75;
    private static readonly TimeSpan AmbiguousDraftTtl = TimeSpan.FromMinutes(5);

    // Vocabulary mirrors the cold greeting ("REQUEST a service or REGISTER as a
    // provider?") so users see one consistent ask. ParseDisambiguation accepts
    // both REQUEST/REGISTER and the legacy HIRE/OFFER tokens.
    internal const string DisambiguationPrompt =
        "Quick check — do you want to REQUEST a service (you need help) " +
        "or REGISTER as a provider (you offer one)? Reply REQUEST or REGISTER.";

    public Task Handle(InboundMessageReceived evt, CancellationToken ct) =>
        RouteAsync(evt.Message, prefetchedIntent: null, ct);

    // Post-classification re-entry: ClassifyInboundIntentHandler runs the LLM
    // outside the user-visible critical path, then bus.InvokeAsync's RouteClassifiedIntent
    // so the switch dispatch happens inside a normal Wolverine handler context.
    // Pre-classification deterministic checks re-run on this path too, in case
    // state changed during the Ollama window.
    public Task Handle(RouteClassifiedIntent evt, CancellationToken ct) =>
        RouteAsync(evt.Message, evt.Detected, ct);

    private async Task RouteAsync(InboundMessage msg, IntentDetectionResult? prefetchedIntent, CancellationToken ct)
    {
        var phone = msg.From.Value;
        var text = msg.Text ?? string.Empty;
        var masked = msg.From.Mask();
        // Re-entry from RouteClassifiedIntent: the original entry already advanced the
        // contact's LastInboundAt and ran CANCEL detection. These are durable side effects
        // and must not repeat — UpsertInboundAsync would push LastInboundAt forward by
        // the Ollama window (60-150s), and re-running CANCEL detection here would race
        // a non-locked Get→Delete against any state that genuinely changed during that
        // window. Deterministic draft/feedback/active-request checks below DO re-run.
        var isReentry = prefetchedIntent is not null;

        logger.LogDebug("Routing inbound {MessageId} from {From} kind={Kind}", msg.MessageId, masked, msg.Kind);

        if (!isReentry)
        {
            if (QuickIntent.Detect(text) == IntentKind.Cancel)
            {
                if (await AbandonAsync(phone, msg.From, ct)) return;
            }

            // Persist contact AFTER the cancel/abandon detection so a CANCEL inbound that
            // tears down a draft does not extend the contact's last-inbound timestamp.
            await contacts.UpsertInboundAsync(phone, clock.GetUtcNow(), ct);
        }

        // Compute hint up front so we can detect cross-flow intent switches before
        // dispatching into an active draft. Hint is deterministic regex; LLM intent
        // detection happens only on the no-active-draft path below.
        var hint = QuickIntent.DetectIntentHint(text);

        // Lazy lookups in the order the router consumes them — first hit short-circuits
        // and avoids the remaining RTTs. Happy-path "active registration draft" is one RTT.
        if (await prefetch.GetRegistrationDraftAsync(phone, ct) is not null)
        {
            // Cross-flow switch: provider mid-registration sends a strong service-request
            // hint ("I need …", "my X is broken", "no power"). Discard the reg draft and
            // route to client funnel. Hint-only by design — ambiguous text continues the
            // current funnel; user can always type CANCEL/END to start over.
            if (hint == IntentKind.ServiceRequest)
            {
                await registrationDrafts.DeleteAsync(phone, ct);
                logger.LogDebug("Cross-flow switch reg→client for {Phone}", masked);
                await clientOrchestrator.HandleAsync(msg, ct);
                return;
            }

            logger.LogDebug("Route → RegistrationOrchestrator (active reg draft) for {Phone}", masked);
            await registrationOrchestrator.HandleAsync(msg, ct);
            return;
        }

        if (await prefetch.GetClientDraftAsync(phone, ct) is not null)
        {
            // Cross-flow switch: client mid-request sends a strong provider-registration
            // hint ("I'm a plumber", "I offer carpentry"). Discard client draft and route
            // to reg funnel.
            if (hint == IntentKind.ProviderRegistration)
            {
                await clientDrafts.DeleteAsync(phone, ct);
                logger.LogDebug("Cross-flow switch client→reg for {Phone}", masked);
                await registrationOrchestrator.HandleAsync(msg, ct);
                return;
            }

            logger.LogDebug("Route → ClientRequestOrchestrator (active client draft) for {Phone}", masked);
            await clientOrchestrator.HandleAsync(msg, ct);
            return;
        }

        var ambiguousDraft = await prefetch.GetAmbiguousDraftAsync(phone, ct);
        if (await TryResolveAmbiguousAsync(msg, ambiguousDraft, text, masked, ct)) return;

        if (await prefetch.GetPendingFeedbackAsync(phone, ct) is { } pendingFeedback)
        {
            logger.LogDebug("Route → FeedbackResponseService (pending feedback) for {Phone}", masked);
            await feedbackService.HandleAsync(msg, pendingFeedback, ct);
            return;
        }

        var activeRequest = await prefetch.GetActiveRequestAsync(phone, ct);

        if (activeRequest is not null && PickProviderResolver.IsPickIntent(text))
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

        // Bare "NEW" advertised by MatchPresenter / IterationCoordinator: close the
        // current request and prompt for a fresh service description. Deterministic
        // bypass keeps the LLM out of it — especially important for users who are
        // also listed providers (would mis-route to the heartbeat path).
        if (activeRequest is not null && QuickIntent.Detect(text) is IntentKind.NewRequest)
        {
            if (activeRequest.Status != ServiceRequestStatus.Closed)
            {
                activeRequest.Close();
            }
            logger.LogDebug("Route → NewRequest (closed {RequestId}, prompting) for {Phone}",
                activeRequest.Id, masked);
            await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From,
                "OK — what service do you need now? Reply 'I need …' to start a new request."));
            return;
        }

        // Deterministic hint short-circuits the LLM intent call entirely; a prefetched
        // intent from ClassifyInboundIntentHandler does the same on the re-entry path.
        // Otherwise we publish a deterministic ack + ClassifyInboundIntentRequested so
        // the 60-150s Ollama window happens off the user-visible critical path.
        var detected = prefetchedIntent ?? (hint is { } h
            ? new IntentDetectionResult(h, 1.0, "en", "hint")
            : null);

        if (detected is null)
        {
            logger.LogDebug("Deferring LLM intent classification for {Phone}", masked);
            await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From,
                "Got your message — one sec…"));
            await bus.PublishAsync(new ClassifyInboundIntentRequested(msg));
            return;
        }

        // One lookup, reused below by the ambiguity guard and the Greeting/Unknown branch.
        // Listed providers shouldn't see the HIRE/OFFER prompt — they're already on the
        // provider side and the orchestrator can disambiguate based on draft state.
        var listedProvider = detected.Intent is IntentKind.ServiceRequest
                                              or IntentKind.ProviderRegistration
                                              or IntentKind.Greeting
                                              or IntentKind.Unknown
            ? await providers.GetAsync(phone, ct)
            : null;

        if (listedProvider is null
            && hint is null
            && detected.Intent is IntentKind.ServiceRequest or IntentKind.ProviderRegistration
            && detected.Confidence < AmbiguityConfidenceThreshold
            && (activeRequest is null || activeRequest.Status != ServiceRequestStatus.Open))
        {
            logger.LogDebug("Low-confidence intent={Intent} conf={Conf:F2} → disambiguating for {Phone}",
                detected.Intent, detected.Confidence, masked);
            await ambiguousDrafts.UpsertAsync(
                AmbiguousIntentDraft.Start(phone, text, clock.GetUtcNow()), ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From, DisambiguationPrompt));
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
            case IntentKind.ServiceRequest when activeRequest is not null:
                // Silent supersede: close the existing request and start a fresh client
                // funnel from the captured text. Replaces the older END/KEEP detour.
                if (activeRequest.Status != ServiceRequestStatus.Closed)
                {
                    activeRequest.Close();
                    logger.LogDebug("Silent supersede: closed {RequestId} for {Phone}", activeRequest.Id, masked);
                }
                await clientOrchestrator.HandleAsync(msg, ct);
                return;
            case IntentKind.ServiceRequest:
                logger.LogDebug("Route → ClientRequestOrchestrator (new request) for {Phone}", masked);
                await clientOrchestrator.HandleAsync(msg, ct);
                return;
            case IntentKind.ProviderRegistration:
                logger.LogDebug("Route → RegistrationOrchestrator (new/heartbeat) for {Phone}", masked);
                await registrationOrchestrator.HandleAsync(msg, ct);
                return;
            case IntentKind.Greeting:
                if (listedProvider is not null)
                {
                    await registrationOrchestrator.HeartbeatSilentAsync(msg.From, clock.GetUtcNow(), ct);
                    var services = string.Join(", ", listedProvider.Services);
                    var ack = $"Hi! You're currently listed as a provider for {services}. " +
                              "Reply 'I need …' to request a different service yourself, or LEAVE to unlist.";
                    await bus.PublishAsync(new SendWhatsAppTextRequested(msg.From, ack));
                    logger.LogDebug("Listed-provider greet-back for {Phone}", masked);
                    return;
                }
                logger.LogDebug("Cold reply (greeting-reply) for {Phone}", masked);
                await SendColdReplyAsync(msg.From, text, detected, "greeting-reply", ct);
                return;
            case IntentKind.Unknown:
                if (listedProvider is not null)
                {
                    // Listed provider sending off-topic text — orchestrator extends TTL
                    // AND sends the visible heartbeat ack ("You're listed for X, extended
                    // for Nh, reply LEAVE to unlist"). Never silent: every inbound gets
                    // a reply so the provider knows the bot is alive and TTL was bumped.
                    logger.LogDebug("Listed-provider heartbeat ack for {Phone}", masked);
                    await registrationOrchestrator.HandleAsync(msg, ct);
                    return;
                }
                logger.LogDebug("Cold reply (out-of-scope) for {Phone}", masked);
                await SendColdReplyAsync(msg.From, text, detected, "out-of-scope", ct);
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
        InboundMessage msg, AmbiguousIntentDraft? draft, string text, string masked, CancellationToken ct)
    {
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
            await bus.PublishAsync(new SendWhatsAppTextRequested(
                msg.From,
                "Reply REQUEST if you need a service, or REGISTER if you provide one."));
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
                or "request" or "hire" or "need service" or "need provider" or "i need" => IntentKind.ServiceRequest,
            "2" or "2." or "provider" or "register" or "offer" or "offering" or "i offer"
                or "list" or "i'm a provider" or "im a provider" => IntentKind.ProviderRegistration,
            _ => null
        };
    }

    private ValueTask SendColdReplyAsync(
        PhoneNumber from,
        string text,
        IntentDetectionResult detected,
        string purpose,
        CancellationToken ct) =>
        bus.PublishAsync(new SendColdReplyRequested(from, text, detected, purpose));

    private async Task TryPickAsync(
        ServiceRequest.RequestAggregate.ServiceRequest request,
        string text,
        string maskedPhone,
        CancellationToken ct)
    {
        var matchOrder = await matches.GetForRequestAsync(request.Id, ct);
        if (matchOrder.Count == 0) return;

        var picked = PickProviderResolver.Resolve(text, matchOrder);
        if (picked.Count == 0) return;

        var freshSuccess = 0;
        var returningSuccess = 0;
        var raceLost = 0;
        var unavailable = 0;
        var invalidData = 0;
        var transientFail = 0;

        foreach (var p in picked)
        {
            try
            {
                logger.LogDebug(
                    "Route → PhoneExchanger.TryExchange match={MatchId} pos={Position} for {Phone}",
                    p.Match.Id,
                    p.Position,
                    maskedPhone);
                var outcome = await phoneExchanger.TryExchangeAsync(p.Match.Id, p.Position, ct);
                switch (outcome)
                {
                    case ExchangeOutcome.Exchanged:
                    case ExchangeOutcome.RoutedToChat:
                        freshSuccess++;
                        break;
                    case ExchangeOutcome.AlreadyShared:
                    case ExchangeOutcome.AlreadyRouted:
                        returningSuccess++;
                        break;
                    case ExchangeOutcome.RaceLost:
                        raceLost++;
                        break;
                    case ExchangeOutcome.ProviderExpired:
                    case ExchangeOutcome.ProviderMissing:
                    case ExchangeOutcome.RequestMissing:
                        unavailable++;
                        break;
                    case ExchangeOutcome.InvalidData:
                        invalidData++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(outcome), outcome, "Unhandled ExchangeOutcome — extend the switch.");
                }
            }
            catch (PostgresException ex) when (TransientPgStates.IsTransient(ex.SqlState))
            {
                logger.LogWarning(
                    ex,
                    "Transient Postgres failure during PhoneExchanger for match {MatchId}",
                    p.Match.Id);
                transientFail++;
            }
        }

        // Re-picks (AlreadyShared/AlreadyRouted) already got a per-match notice from
        // PhoneExchanger, so they should not inflate the partial-success count or
        // appear in the summary's denominator.
        var failedTotal = raceLost + unavailable + invalidData + transientFail;
        var consideredTotal = freshSuccess + failedTotal;
        if (failedTotal == 0) return;

        if (!PhoneNumber.TryParse(request.ClientPhone, out var clientPhone)) return;

        string reply;
        if (consideredTotal == failedTotal)
        {
            if (unavailable == failedTotal)
                reply = "Those providers' listings expired. "
                    + "Reply NEXT for more matches, or INCREASE to search further.";
            else if (raceLost == failedTotal)
                reply = "Another client just picked that provider. Reply NEXT to see more matches.";
            else
                reply = "Temporary hiccup connecting you. Try again in a moment, or reply NEXT for other providers.";
        }
        else
        {
            reply = $"Connected you with {freshSuccess} of {consideredTotal} providers. "
                + "Reply PICK <#> or NEXT for more.";
        }
        await bus.PublishAsync(new SendWhatsAppTextRequested(clientPhone, reply));
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
            await phoneExchanger.TryExchangeAsync(matchOrder[0].Id, 1, ct);
            return;
        }

        await bus.PublishAsync(new SendWhatsAppTextRequested(from,
            $"Which match? Reply 1, 2, or {matchOrder.Count}."));
    }

    private async Task<bool> AbandonAsync(string phone, PhoneNumber from, CancellationToken ct)
    {
        // No locking around Get→Delete — drafts are short-lived and duplicate inbound delivery is rare.
        // Revisit if horizontal scaling is introduced.
        var hadRegDraft = await registrationDrafts.GetAsync(phone, ct) is not null;
        var hadClientDraft = await clientDrafts.GetAsync(phone, ct) is not null;
        var hadAmbiguousDraft = await ambiguousDrafts.GetAsync(phone, ct) is not null;
        if (!hadRegDraft && !hadClientDraft && !hadAmbiguousDraft) return false;

        await bus.PublishAsync(new SendWhatsAppTextRequested(from, "Session ended. Send a new message to start over."));

        if (hadRegDraft) await registrationDrafts.DeleteAsync(phone, ct);
        if (hadClientDraft) await clientDrafts.DeleteAsync(phone, ct);
        if (hadAmbiguousDraft) await ambiguousDrafts.DeleteAsync(phone, ct);

        logger.LogDebug("Abandoned active draft for {Phone} (reg={Reg}, client={Client}, ambig={Ambig})",
            from.Mask(), hadRegDraft, hadClientDraft, hadAmbiguousDraft);
        return true;
    }
}
