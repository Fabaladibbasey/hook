namespace Hook.Features.Feedback.Models;

public enum FeedbackStep
{
    DidYouFind = 0,
    IdentifyWinner = 1,
    JobCompleted = 2,
    AwaitingEta = 3,
    CaptureNoReason = 4
}

public enum FeedbackAnswer
{
    Pending = 0,
    Yes = 1,
    No = 2,
    InProgress = 3,
    Skipped = 4,
    WinnerSelected = 5,
    EtaCaptured = 6,
    NoReasonCaptured = 7
}
