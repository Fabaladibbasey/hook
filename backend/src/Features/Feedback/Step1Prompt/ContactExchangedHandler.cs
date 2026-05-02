using Hook.Features.ContactSharing.Events;
using Wolverine;

namespace Hook.Features.Feedback.Step1Prompt;

public sealed class ContactExchangedHandler(IMessageBus bus, ILogger<ContactExchangedHandler> logger)
{
    public async Task Handle(ContactExchanged evt, CancellationToken ct)
    {
        await bus.ScheduleAsync(new Step1FeedbackCheck(evt.MatchId), TimeSpan.FromHours(4));
        logger.LogInformation("Scheduled Step1 feedback at +4h for match {MatchId}", evt.MatchId);
    }
}
