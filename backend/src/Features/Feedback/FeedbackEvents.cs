namespace Hook.Features.Feedback;

public sealed record Step1FeedbackCheck(Guid MatchId);
public sealed record Step2FeedbackCheck(Guid FeedbackId);
public sealed record ChatEndedDomainEvent(Guid ChatId);
