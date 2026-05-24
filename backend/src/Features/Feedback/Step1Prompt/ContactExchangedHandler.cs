using Hook.Features.ContactSharing.Events;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.Feedback.Step1Prompt;

public sealed class ContactExchangedHandler(
    IMessageBus bus,
    IOptions<FeedbackOptions> options,
    ILogger<ContactExchangedHandler> logger)
{
    // Direct-contact picks still use wall-clock Step1InitialDelay — chat-routed
    // picks fire on ChatSessionEndedEvent (ChatSessionEndedHandler) instead, since
    // there is no chat lifecycle to observe in the contact-share path.
    public async Task Handle(ContactExchangedEvent evt, CancellationToken ct)
    {
        var delay = options.Value.Step1InitialDelay;
        await bus.ScheduleAsync(new Step1FeedbackCheck(evt.MatchId), delay);
        logger.LogInformation(
            "Scheduled Step1 feedback at +{Delay} for match {MatchId}",
            delay, evt.MatchId);
    }
}
