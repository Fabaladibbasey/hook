namespace Hook.Features.Feedback.Models;

public enum FeedbackStep
{
    DidYouFind,
    IdentifyWinner,
    JobCompleted,
    AwaitingEta,
    CaptureNoReason
}

public enum FeedbackAnswer
{
    Pending,
    Yes,
    No,
    InProgress,
    Skipped,
    WinnerSelected,
    EtaCaptured,
    NoReasonCaptured
}
