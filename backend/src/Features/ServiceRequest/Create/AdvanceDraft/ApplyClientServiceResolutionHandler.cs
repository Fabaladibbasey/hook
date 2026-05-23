using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.ServiceRequest.Create.AdvanceDraft;

public sealed class ApplyClientServiceResolutionHandler(
    IClientRequestDraftRepository drafts,
    TimeProvider clock,
    ILogger<ApplyClientServiceResolutionHandler> logger)
{
    public async Task Handle(ApplyClientServiceResolutionCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var draft = await drafts.GetAsync(cmd.Phone, ct);
        if (draft is null) return;
        if (!PhoneNumber.TryParse(cmd.Phone, out var phone)) return;
        var now = clock.GetUtcNow();

        if (cmd.IsSwitch)
        {
            // Race guard: user may have advanced the draft past the slug-eligible steps
            // while we were running the LLM. Only act if we're still in a step that can
            // accept a slug switch.
            if (draft.Step is not (ClientRequestStep.AwaitingLocation
                                 or ClientRequestStep.ConfirmLocation))
            {
                logger.LogDebug("Stale slug-switch for {Phone}; draft now at {Step}", phone.Mask(), draft.Step);
                return;
            }
            if (string.Equals(cmd.CanonicalSlug, draft.DraftServiceSlug, StringComparison.Ordinal))
            {
                logger.LogDebug(
                    "Slug-switch resolved to same slug {Slug} for {Phone}; no-op",
                    cmd.CanonicalSlug,
                    phone.Mask());
                return;
            }
            draft.SwitchSlug(cmd.CanonicalSlug, now);
            draft.StepTo(ClientRequestStep.ConfirmService, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextCommand(phone,
                $"Switching to {cmd.CanonicalSlug.Replace('-', ' ')}. Reply YES to confirm or NO to choose another."));
            return;
        }

        draft.SwitchSlug(cmd.CanonicalSlug, now);
        draft.StepTo(ClientRequestStep.ConfirmService, now);
        await drafts.UpsertAsync(draft, ct);
        var confirm = $"Do you need {cmd.CanonicalSlug.Replace('-', ' ')}? "
            + "Reply YES or NO — YES to confirm, NO to choose another service.";
        await bus.PublishAsync(new SendWhatsAppTextCommand(phone, confirm));
    }
}
