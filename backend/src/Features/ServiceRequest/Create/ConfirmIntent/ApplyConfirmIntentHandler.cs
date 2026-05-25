using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.ServiceRequest.Create.ConfirmIntent;

public sealed class ApplyConfirmIntentHandler(
    IClientRequestDraftRepository drafts,
    TimeProvider clock,
    ILogger<ApplyConfirmIntentHandler> logger)
{
    public async Task Handle(ApplyConfirmIntentCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(cmd.Phone, out var phone)) return;
        var draft = await drafts.GetAsync(cmd.Phone, ct);
        // Staleness guard: a user reply between publish and apply already advanced or
        // reset the draft; CANCEL may have torn it down; or the user is no longer on
        // ConfirmService. Drop silently in all three.
        if (draft is null)
        {
            logger.LogDebug("Apply Confirm: no draft for {Phone}; dropping", phone.Mask());
            return;
        }
        if (draft.Step != ClientRequestStep.ConfirmService)
        {
            logger.LogDebug("Apply Confirm: draft at {Step} (not ConfirmService) for {Phone}; dropping",
                draft.Step, phone.Mask());
            return;
        }
        if (ConfirmDraftStampGuard.IsStale(draft.UpdatedAt, cmd.DraftStampedAt))
        {
            logger.LogDebug("Apply Confirm: stale stamp for {Phone}; dropping", phone.Mask());
            return;
        }

        var now = clock.GetUtcNow();
        logger.LogDebug("Apply Confirm: {Intent} for {Phone}", cmd.Intent, phone.Mask());

        switch (cmd.Intent)
        {
            case ConfirmReplyIntent.Yes:
                if (draft.DraftLatitude is not null && draft.DraftLongitude is not null)
                {
                    draft.StepTo(ClientRequestStep.AwaitingDescription, now);
                    await drafts.UpsertAsync(draft, ct);
                    var text =
                        $"Got it. Using your saved location: {draft.DraftFormattedAddress}. " +
                        "Want to add a description? Send it now or reply SKIP.";
                    await bus.PublishAsync(new SendWhatsAppTextCommand(phone, text));
                    return;
                }
                draft.StepTo(ClientRequestStep.AwaitingLocation, now);
                await drafts.UpsertAsync(draft, ct);
                await bus.PublishAsync(new SendWhatsAppTextCommand(
                    phone,
                    "Send your location pin (or type your address)."));
                return;

            case ConfirmReplyIntent.No:
                draft.SwitchSlug(string.Empty, now);
                draft.StepTo(ClientRequestStep.AwaitingService, now);
                await drafts.UpsertAsync(draft, ct);
                await bus.PublishAsync(new SendWhatsAppTextCommand(phone,
                    "What service do you need? Or reply REGISTER if you're offering services instead."));
                return;

            case ConfirmReplyIntent.Unsure:
                var slug = draft.DraftServiceSlug.Replace('-', ' ');
                var prompt =
                    $"Please reply YES or NO — YES to confirm {slug}, " +
                    "NO to choose another service.";
                await bus.PublishAsync(new SendWhatsAppTextCommand(phone, prompt));
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(cmd), cmd.Intent, "Unhandled ConfirmReplyIntent");
        }
    }
}
