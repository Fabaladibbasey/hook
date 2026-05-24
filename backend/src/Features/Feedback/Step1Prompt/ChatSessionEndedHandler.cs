using Hook.Features.ChatSession.SessionAggregate;
using Wolverine;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;

namespace Hook.Features.Feedback.Step1Prompt;

// Opportunistic Step1 trigger. Every reason that lands here means "this chat
// looks done, ask feedback now" — User click, Idle auto-end, 24h hard expiry,
// and ProductiveSilence-while-still-active. Per-match Step1FeedbackCheck
// publishes converge on Step1FeedbackHandler whose existing unique-index dedupe
// (FeedbackConstants.RequestStep1UniqueIndexName) handles races against the
// no-chat 30-min timer and other simultaneous triggers.
public sealed class ChatSessionEndedHandler(
    IMatchRepository matches,
    ILogger<ChatSessionEndedHandler> logger)
{
    public async Task Handle(ChatSessionEndedEvent evt, IMessageBus bus, CancellationToken ct)
    {
        var matchIds = await matches.GetMatchIdsByChatIdAsync(evt.ChatId, ct);
        foreach (var matchId in matchIds)
        {
            await bus.PublishAsync(new Step1FeedbackCheck(matchId));
        }
        if (matchIds.Count > 0)
            logger.LogDebug(
                "Fanned out Step1 for {Count} matches on chat {ChatId}",
                matchIds.Count, evt.ChatId);
    }
}
