using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.ChatLifecycle.ProductiveSilence;

public sealed class ProductiveSilenceHandler(
    IChatRepository chats,
    IOptions<ChatOptions> options,
    TimeProvider clock,
    ILogger<ProductiveSilenceHandler> logger)
{
    public async Task Handle(ProductiveSilenceCheck evt, IMessageBus bus, CancellationToken ct)
    {
        // Two-layer dedupe: this `snapshot.ProductiveSilenceFiredAt` gate short-circuits
        // before counting messages, and the downstream Step1FeedbackHandler.TryAddPendingAsync
        // collapses the per-match unique-index race when two paths fire concurrently.
        // Status guard (added with ChatRepository.TryMarkProductiveSilenceAsync) prevents
        // the productive-silence mark from racing past End().
        var snapshot = await chats.GetProductiveSilenceSnapshotAsync(evt.ChatId, ct);
        if (snapshot is null) return;
        if (snapshot.Status != ChatSessionStatus.Active) return;
        if (snapshot.ProductiveSilenceFiredAt is not null) return;
        if (snapshot.LastActivityAt > evt.ScheduledForActivityAt)
        {
            logger.LogDebug(
                "ProductiveSilence skip {ChatId} — fresher activity at {Activity}",
                evt.ChatId, snapshot.LastActivityAt);
            return;
        }

        var opts = options.Value;
        var (clientCount, providerCount) = await chats.GetMessageCountByRoleAsync(evt.ChatId, ct);
        var min = Math.Min(clientCount, providerCount);
        if (min < opts.ProductiveSilenceMinMessagesPerSide) return;

        if (!await chats.TryMarkProductiveSilenceAsync(evt.ChatId, clock.GetUtcNow(), ct))
        {
            // Lost race to a concurrent productive-silence handler. Step1 already
            // dispatched by the winner.
            return;
        }

        // Direct publish (not via aggregate) per CLAUDE.md carve-out: the marking
        // happened through ExecuteUpdate, so DomainEventScraper has no tracked entity
        // to drain from. AutoApplyTransactions still owns the outbox commit for this
        // handler tx.
        await bus.PublishAsync(new ChatSessionEndedEvent(evt.ChatId, ChatEndReason.ProductiveSilence));
        logger.LogInformation(
            "ProductiveSilence fired for {ChatId} — {Client}/{Provider} msgs each",
            evt.ChatId, clientCount, providerCount);
    }
}
