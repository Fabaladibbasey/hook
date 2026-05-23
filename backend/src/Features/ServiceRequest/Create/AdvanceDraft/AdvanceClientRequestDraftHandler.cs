using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.ServiceRequest.Create.AdvanceDraft;

public sealed class AdvanceClientRequestDraftHandler(
    IClientRequestDraftRepository drafts,
    TimeProvider clock,
    ILogger<AdvanceClientRequestDraftHandler> logger)
{
    public async Task Handle(AdvanceClientRequestDraft evt, IMessageBus bus, CancellationToken ct)
    {
        var draft = await drafts.GetAsync(evt.Phone, ct);
        if (draft is null) return;

        if (!PhoneNumber.TryParse(evt.Phone, out var phone)) return;
        var now = clock.GetUtcNow();

        if (string.IsNullOrEmpty(evt.CanonicalSlug))
        {
            // Either AI returned no slugs or the call failed. For the StartAsync path
            // we parked the draft in ResolvingService; reset to AwaitingService so the
            // user can retry. For the slug-switch path the draft step was untouched —
            // ack the user (so they're not left silent after "Looking up…") but keep
            // the funnel on its current step.
            if (!evt.IsSwitch)
            {
                draft.StepTo(ClientRequestStep.AwaitingService, now);
                await drafts.UpsertAsync(draft, ct);
                var prompt = "What service do you need? (e.g. plumber, carpenter, computer repair) "
                    + "— or reply REGISTER if you're offering services instead.";
                await bus.PublishAsync(new SendWhatsAppTextRequested(phone, prompt));
            }
            else
            {
                logger.LogDebug("Slug-switch extract empty for {Phone}; staying in {Step}", phone.Mask(), draft.Step);
                await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                    "Couldn't catch that — continuing with your earlier request."));
            }
            return;
        }

        if (evt.IsSwitch)
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
            if (string.Equals(evt.CanonicalSlug, draft.DraftServiceSlug, StringComparison.Ordinal))
            {
                logger.LogDebug(
                    "Slug-switch resolved to same slug {Slug} for {Phone}; no-op",
                    evt.CanonicalSlug,
                    phone.Mask());
                return;
            }
            draft.SwitchSlug(evt.CanonicalSlug, now);
            draft.StepTo(ClientRequestStep.ConfirmService, now);
            await drafts.UpsertAsync(draft, ct);
            await bus.PublishAsync(new SendWhatsAppTextRequested(phone,
                $"Switching to {evt.CanonicalSlug.Replace('-', ' ')}. Reply YES to confirm or NO to choose another."));
            return;
        }

        draft.SwitchSlug(evt.CanonicalSlug, now);
        draft.StepTo(ClientRequestStep.ConfirmService, now);
        await drafts.UpsertAsync(draft, ct);
        var confirm = $"Do you need {evt.CanonicalSlug.Replace('-', ' ')}? "
            + "Reply YES or NO — YES to confirm, NO to choose another service.";
        await bus.PublishAsync(new SendWhatsAppTextRequested(phone, confirm));
    }
}
