using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Feedback.Step2Prompt;

public sealed record Step2PromptDispatchCommand(
    Guid FeedbackId,
    Guid MatchId,
    PhoneNumber ClientPhone,
    string ServiceSlug);
