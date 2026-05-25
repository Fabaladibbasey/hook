using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.Step1Prompt;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Wolverine;

namespace Hook.UnitTests.Feedback;

public class Step1RecheckHandlerTests
{
    private readonly Mock<IFeedbackRepository> _feedbackMock = new();
    private readonly Mock<IMessageBus> _busMock = new();

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FeedbackOptions _options = new();
    private readonly List<(Guid Id, FeedbackAnswer Answer)> _claimed = [];
    private readonly List<Step1PromptDispatchCommand> _published = [];

    private MatchFeedback? _pending;

    public Step1RecheckHandlerTests()
    {
        _feedbackMock.Setup(x => x.GetPendingAsync(It.IsAny<Guid>(), FeedbackStep.DidYouFind, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _pending);
        _feedbackMock.Setup(x => x.TrySaveAsync(It.IsAny<MatchFeedback>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MatchFeedback agg, CancellationToken _) =>
            {
                if (agg.Answer is not FeedbackAnswer.Pending)
                    _claimed.Add((agg.Id, agg.Answer));
                return true;
            });

        _busMock.Setup(x => x.PublishAsync(It.IsAny<Step1PromptDispatchCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _published.Add((Step1PromptDispatchCommand)msg))
            .Returns(ValueTask.CompletedTask);
    }

    private Step1RecheckHandler Build() =>
        new(_feedbackMock.Object, Options.Create(_options), _clock,
            NullLogger<Step1RecheckHandler>.Instance);

    private static Step1RecheckCommand Cmd(Guid matchId) =>
        new(matchId, PhoneNumber.Parse("+2203339999"), "plumbing", string.Empty);

    private DateTimeOffset PastEnough => _clock.GetUtcNow() - (_options.MinRecheckGap + TimeSpan.FromMinutes(1));

    [Fact]
    public async Task Handle_PendingNull_NoAction()
    {
        _pending = null;

        await Build().Handle(Cmd(Guid.NewGuid()), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
        Assert.Empty(_claimed);
    }

    [Fact]
    public async Task Handle_RepromptGateFalse_NoDispatch()
    {
        // PromptedAt = now → Reprompt(now, minGap) returns false because the gap has
        // not yet elapsed.
        _pending = MatchFeedback.CreatePending(Guid.NewGuid(), Guid.NewGuid(), FeedbackStep.DidYouFind, _clock.GetUtcNow());

        await Build().Handle(Cmd(_pending.MatchId), _busMock.Object, CancellationToken.None);

        Assert.Empty(_published);
    }

    [Fact]
    public async Task Handle_HappyPath_PublishesPromptDispatchUsingCommandData()
    {
        // No match/request/picked lookups — data carried on the command.
        var matchId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        _pending = MatchFeedback.CreatePending(matchId, requestId, FeedbackStep.DidYouFind, PastEnough);

        await Build().Handle(Cmd(matchId), _busMock.Object, CancellationToken.None);

        var dispatch = Assert.Single(_published);
        Assert.Equal(_pending!.Id, dispatch.FeedbackId);
        Assert.Equal(matchId, dispatch.MatchId);
        Assert.Equal(requestId, dispatch.RequestId);
        Assert.Equal("plumbing", dispatch.ServiceSlug);
        Assert.Equal(string.Empty, dispatch.PickedFormatted);
    }

    [Fact]
    public async Task Handle_CapExceeded_ClaimsSkippedAndExits()
    {
        // Step1MaxRechecks = 0 — realistic "no rechecks allowed" configuration.
        // Any pending row with RecheckCount > 0 exceeds the cap. Reschedule once
        // first to drive the count above the cap.
        var matchId = Guid.NewGuid();
        _pending = MatchFeedback.CreatePending(matchId, Guid.NewGuid(), FeedbackStep.DidYouFind, _clock.GetUtcNow());
        _pending.Reschedule(_clock.GetUtcNow());

        var capped = new Step1RecheckHandler(
            _feedbackMock.Object,
            Options.Create(new FeedbackOptions { Step1MaxRechecks = 0 }),
            _clock,
            NullLogger<Step1RecheckHandler>.Instance);

        await capped.Handle(Cmd(matchId), _busMock.Object, CancellationToken.None);

        Assert.Single(_claimed);
        Assert.Equal(FeedbackAnswer.Skipped, _claimed[0].Answer);
        Assert.Empty(_published);
    }
}
