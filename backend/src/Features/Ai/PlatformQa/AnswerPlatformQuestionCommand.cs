using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Ai.PlatformQa;

// Wolverine envelope dispatched by PlatformQaDispatcher (cold-path Q&A from the
// router, mid-flow Q&A from the orchestrators). The handler is [NonTransactional];
// this command never mutates any draft — it is "answer the question and reply",
// nothing else.
public sealed record AnswerPlatformQuestionCommand(
    PhoneNumber To,
    string Question,
    string Locale,
    string ReplyContextHint);
