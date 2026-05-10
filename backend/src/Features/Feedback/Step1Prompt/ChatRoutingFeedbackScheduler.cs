using Hook.Features.ContactSharing.Events;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Feedback.Step1Prompt;

// Mirrors ContactExchangedHandler so chat-routed picks (one or both parties withheld
// phone consent) still receive the Step1 "did you find a provider?" prompt. The
// post-pick outcome question applies regardless of whether the match resolved via
// direct phone reveal or in-app chat — provider stats are keyed on ProviderPhone
// either way. Renamed from ChatRoutingRequestedHandler: this scheduler lives in the
// Feedback slice rather than ChatPrivacyRouting, so the name now reflects its role.
// [WolverineHandler] is required because the class name doesn't end in "Handler" or
// "Consumer" — without it, Wolverine's default discovery skips the type and chat-
// routed picks never get a Step1 prompt.
[WolverineHandler]
public sealed class ChatRoutingFeedbackScheduler(
    IMessageBus bus,
    IOptions<FeedbackOptions> options,
    ILogger<ChatRoutingFeedbackScheduler> logger)
{
    public async Task Handle(ChatRoutingRequested evt, CancellationToken ct)
    {
        var delay = options.Value.Step1InitialDelay;
        await bus.ScheduleAsync(new Step1FeedbackCheck(evt.MatchId), delay);
        logger.LogInformation(
            "Scheduled Step1 feedback at +{Delay} for chat-routed match {MatchId}",
            delay, evt.MatchId);
    }
}
