using Hook.Features.ChatPrivacyRouting.RouteMatch;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Feedback.Step1Prompt;

// Mirrors ContactExchangedHandler so chat-routed picks (one or both parties withheld
// phone consent) still receive the Step1 "did you find a provider?" prompt. The
// post-pick outcome question applies regardless of whether the match resolved via
// direct phone reveal or in-app chat — provider stats are keyed on ProviderPhone
// either way.
// Lives in the Feedback slice and reacts to RouteMatchToChatCommand
// by scheduling Step1Prompt. Distinct from RouteMatchToChatHandler (in
// ChatPrivacyRouting/RouteMatch/) which creates the chat session itself.
// Class name keeps the *Scheduler suffix; [WolverineHandler] is required
// because the name does not end in Handler/Consumer.
[WolverineHandler]
public sealed class ChatRoutingFeedbackScheduler(
    IMessageBus bus,
    IOptions<FeedbackOptions> options,
    ILogger<ChatRoutingFeedbackScheduler> logger)
{
    public async Task Handle(RouteMatchToChatCommand cmd, CancellationToken ct)
    {
        var delay = options.Value.Step1InitialDelay;
        await bus.ScheduleAsync(new Step1FeedbackCheck(cmd.MatchId), delay);
        logger.LogInformation(
            "Scheduled Step1 feedback at +{Delay} for chat-routed match {MatchId}",
            delay, cmd.MatchId);
    }
}
