using Wolverine.Attributes;

namespace Hook.Features.Feedback;

public sealed record Step1FeedbackCheck(Guid MatchId);

// Versioned identity so a future schema change to Step2FeedbackCheck (extra fields)
// can roll out without orphaning durable scheduled envelopes that producers in
// flight already wrote.
[MessageIdentity("Step2FeedbackCheck", Version = 1)]
public sealed record Step2FeedbackCheck(Guid MatchId);
