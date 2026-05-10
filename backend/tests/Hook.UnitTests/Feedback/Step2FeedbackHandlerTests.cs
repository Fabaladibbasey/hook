using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.Step2Prompt;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.Feedback;

public class Step2FeedbackHandlerTests
{
    private readonly Dictionary<Guid, MatchEntity> _matches = new();
    private readonly Dictionary<Guid, ServiceRequestEntity> _requests = new();
    private readonly List<MatchFeedback> _added = new();
    private readonly List<Guid> _deleted = new();
    private readonly List<(PhoneNumber To, string Body)> _sent = new();
    private readonly List<string> _callOrder = new();

    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IMatchRepository> _matchesMock = new();
    private readonly Mock<IServiceRequestRepository> _requestsMock = new();
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IWhatsappClient> _whatsappMock = new();

    private bool _tryAddResult = true;
    private MatchFeedback? _latestJobCompleted;
    private bool _aiReturnBlank;
    private int _aiGenerateCalls;
    private Exception? _whatsappThrowOnSend;

    public Step2FeedbackHandlerTests()
    {
        _feedbackMock.Setup(x => x.TryAddPendingAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MatchFeedback f, CancellationToken _) =>
            {
                _callOrder.Add("TryAdd");
                if (_tryAddResult) _added.Add(f);
                return _tryAddResult;
            });
        _feedbackMock.Setup(x => x.DeletePendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((id, _) => _deleted.Add(id))
            .ReturnsAsync(true);
        _feedbackMock.Setup(x => x.GetLatestByMatchAndStepAsync(It.IsAny<Guid>(), It.IsAny<FeedbackStep>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, FeedbackStep step, CancellationToken _) =>
                step == FeedbackStep.JobCompleted ? _latestJobCompleted : null);

        _matchesMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _matches.TryGetValue(id, out var m) ? m : null);

        _requestsMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _requests.TryGetValue(id, out var r) ? r : null);

        _aiMock.Setup(x => x.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                _aiGenerateCalls++;
                return _aiReturnBlank ? string.Empty : "ok";
            });

        _whatsappMock.Setup(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((PhoneNumber to, string body, CancellationToken _) =>
            {
                _callOrder.Add("Send");
                if (_whatsappThrowOnSend is not null) throw _whatsappThrowOnSend;
                _sent.Add((to, body));
                return Task.FromResult("msg-1");
            });
    }

    private Step2FeedbackHandler Build() =>
        new(_feedbackMock.Object, _matchesMock.Object, _requestsMock.Object,
            _aiMock.Object, _whatsappMock.Object,
            NullLogger<Step2FeedbackHandler>.Instance);

    private MatchEntity SeedMatch()
    {
        var match = new MatchEntity
        {
            RequestId = Guid.NewGuid(),
            ProviderPhone = "+2203331234",
            ServiceSlug = "plumbing"
        };
        _matches[match.Id] = match;

        _requests[match.RequestId] = ServiceRequestEntity.Create(
            "+2203339999", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);
        return match;
    }

    [Fact]
    public async Task Handle_MatchMissing_NoOp()
    {
        await Build().Handle(new Step2FeedbackCheck(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handle_RequestMissing_NoOp()
    {
        var match = SeedMatch();
        _requests.Remove(match.RequestId);

        await Build().Handle(new Step2FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handle_BadClientPhone_NoOp()
    {
        var match = SeedMatch();
        _requests[match.RequestId] = ServiceRequestEntity.Create(
            "not-a-phone", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);

        await Build().Handle(new Step2FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handle_HappyPath_ReservesBeforeSending()
    {
        var match = SeedMatch();

        await Build().Handle(new Step2FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Single(_added);
        Assert.Single(_sent);
        // Reserve must precede send: the recorded order should be [TryAdd, Send].
        Assert.Equal(new[] { "TryAdd", "Send" }, _callOrder);
    }

    [Fact]
    public async Task Handle_TryAddPendingFails_DoesNotSendOrCallAi()
    {
        var match = SeedMatch();
        _tryAddResult = false;

        await Build().Handle(new Step2FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Equal(0, _aiGenerateCalls);
        // After the failed reserve, nothing else should run.
        Assert.Equal(new[] { "TryAdd" }, _callOrder);
    }

    [Fact]
    public async Task Handle_AiReturnsNull_DeletesPendingRow()
    {
        var match = SeedMatch();
        _aiReturnBlank = true;

        await Build().Handle(new Step2FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Single(_added);
        Assert.Single(_deleted);
        Assert.Equal(_added[0].Id, _deleted[0]);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Handle_WhatsappSendThrows_DeletesPendingAndRethrows()
    {
        var match = SeedMatch();
        _whatsappThrowOnSend = new InvalidOperationException("transport down");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build().Handle(new Step2FeedbackCheck(match.Id), CancellationToken.None));

        Assert.Single(_added);
        Assert.Single(_deleted);
        Assert.Equal(_added[0].Id, _deleted[0]);
    }

    [Fact]
    public async Task Handle_AlreadyCompletedYes_DoesNotRePrompt()
    {
        // A stale Step2FeedbackCheck arriving after the user already replied YES to
        // JobCompleted must not insert a fresh Pending row and re-prompt. The partial
        // unique index only blocks concurrent Pending fires; the latest-answer guard
        // catches the stale-recheck case.
        var match = SeedMatch();
        _latestJobCompleted = new MatchFeedback
        {
            MatchId = match.Id,
            Step = FeedbackStep.JobCompleted,
            Answer = FeedbackAnswer.Yes
        };

        await Build().Handle(new Step2FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Empty(_added);
        Assert.Empty(_callOrder);
    }

    [Fact]
    public async Task Handle_AlreadyCompletedNo_DoesNotRePrompt()
    {
        var match = SeedMatch();
        _latestJobCompleted = new MatchFeedback
        {
            MatchId = match.Id,
            Step = FeedbackStep.JobCompleted,
            Answer = FeedbackAnswer.No
        };

        await Build().Handle(new Step2FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handle_PreviousJobCompletedInProgress_StillPrompts()
    {
        // An earlier JobCompleted answered "InProgress" is NOT terminal — the user
        // is still working, and this recheck is intentional. Only Yes/No should
        // suppress the re-prompt.
        var match = SeedMatch();
        _latestJobCompleted = new MatchFeedback
        {
            MatchId = match.Id,
            Step = FeedbackStep.JobCompleted,
            Answer = FeedbackAnswer.InProgress
        };

        await Build().Handle(new Step2FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Single(_sent);
        Assert.Single(_added);
    }
}
