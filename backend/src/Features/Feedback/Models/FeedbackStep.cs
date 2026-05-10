namespace Hook.Features.Feedback.Models;

public enum FeedbackStep
{
    DidYouFind,
    IdentifyWinner,
    JobCompleted,
    AwaitingEta
}

public enum FeedbackAnswer
{
    Pending,
    Yes,
    No,
    InProgress,
    Skipped,
    WinnerSelected,
    EtaCaptured
}
