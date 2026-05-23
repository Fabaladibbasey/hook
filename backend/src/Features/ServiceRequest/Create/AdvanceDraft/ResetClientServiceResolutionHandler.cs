using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.ServiceRequest.Create.AdvanceDraft;

public sealed class ResetClientServiceResolutionHandler(
    IClientRequestDraftRepository drafts,
    TimeProvider clock,
    ILogger<ResetClientServiceResolutionHandler> logger)
{
    public async Task Handle(ResetClientServiceResolutionCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var draft = await drafts.GetAsync(cmd.Phone, ct);
        if (draft is null) return;
        if (!PhoneNumber.TryParse(cmd.Phone, out var phone)) return;
        var now = clock.GetUtcNow();

        // Either AI returned no slugs or the call failed. For the StartAsync path
        // we parked the draft in ResolvingService; reset to AwaitingService so the
        // user can retry. For the slug-switch path the draft step was untouched —
        // ack the user (so they're not left silent after "Looking up…") but keep
        // the funnel on its current step.
        if (!cmd.IsSwitch)
        {
            draft.StepTo(ClientRequestStep.AwaitingService, now);
            await drafts.UpsertAsync(draft, ct);
            var prompt = "What service do you need? (e.g. plumber, carpenter, computer repair) "
                + "— or reply REGISTER if you're offering services instead.";
            await bus.PublishAsync(new SendWhatsAppTextCommand(phone, prompt));
            return;
        }

        logger.LogDebug("Slug-switch extract empty for {Phone}; staying in {Step}", phone.Mask(), draft.Step);
        await bus.PublishAsync(new SendWhatsAppTextCommand(phone,
            "Couldn't catch that — continuing with your earlier request."));
    }
}
