using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Feedback.Step1Prompt;

// Scheduled by ApplyStep1IntentAsync when the user defers ("still looking",
// "later", or an explicit eta). Handled by Step1RecheckHandler which fires a
// re-prompt subject to MinRecheckGap. Scheduled one-shot — *Check suffix is
// reserved for recurring dispatches per CLAUDE.md naming rules.
//
// Prompt-format fields (ClientPhone/ServiceSlug/PickedFormatted) are denormalised
// onto the command at schedule time so Step1RecheckHandler can publish the
// downstream Step1PromptDispatchCommand without re-loading match/request/picked.
// The picked-list captured at schedule time wins over the picked list at recheck
// time — the recheck is asking the *original* question.
public sealed record Step1RecheckCommand(
    Guid MatchId,
    PhoneNumber ClientPhone,
    string ServiceSlug,
    string PickedFormatted);
