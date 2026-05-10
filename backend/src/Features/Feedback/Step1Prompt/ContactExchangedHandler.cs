using Hook.Features.ContactSharing.Events;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.Feedback.Step1Prompt;

public sealed class ContactExchangedHandler(
    IMessageBus bus,
    IOptions<FeedbackOptions> options,
    ILogger<ContactExchangedHandler> logger)
{
    public async Task Handle(ContactExchanged evt, CancellationToken ct)
    {
        var delay = options.Value.Step1InitialDelay;
        await bus.ScheduleAsync(new Step1FeedbackCheck(evt.MatchId), delay);
        logger.LogInformation(
            "Scheduled Step1 feedback at +{Delay} for match {MatchId}",
            delay, evt.MatchId);
    }
}
