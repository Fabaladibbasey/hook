using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Feedback.Step1Prompt;

public sealed record Step1PromptDispatchCommand(
    Guid FeedbackId,
    Guid MatchId,
    Guid RequestId,
    PhoneNumber ClientPhone,
    string ServiceSlug,
    string PickedFormatted);
