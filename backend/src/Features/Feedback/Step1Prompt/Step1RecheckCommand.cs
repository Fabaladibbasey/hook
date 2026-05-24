namespace Hook.Features.Feedback.Step1Prompt;

// Scheduled by ApplyStep1IntentAsync when the user defers ("still looking",
// "later", or an explicit eta). Handled by Step1RecheckHandler which fires a
// re-prompt subject to MinRecheckGap. Scheduled one-shot — *Check suffix is
// reserved for recurring dispatches per CLAUDE.md naming rules.
public sealed record Step1RecheckCommand(Guid MatchId);
