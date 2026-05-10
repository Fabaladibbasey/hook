using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.Step1Prompt;
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

public class Step1FeedbackHandlerTests
{
    private readonly Dictionary<Guid, MatchEntity> _matches = new();
    private readonly Dictionary<Guid, IReadOnlyList<MatchEntity>> _requestMatches = new();
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
    private bool _anyByRequestStepResult;
    private int _anyByRequestStepCalled;
    private IReadOnlyDictionary<string, string>? _lastFacts;
    private bool _aiReturnBlank;
    private Exception? _whatsappThrowOnSend;

    public Step1FeedbackHandlerTests()
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
        _feedbackMock.Setup(x => x.AnyByRequestStepAsync(It.IsAny<Guid>(), It.IsAny<FeedbackStep>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                _anyByRequestStepCalled++;
                return _anyByRequestStepResult;
            });

        _matchesMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _matches.TryGetValue(id, out var m) ? m : null);
        _matchesMock.Setup(x => x.GetForRequestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid requestId, CancellationToken _) =>
            {
                // Explicit-only: tests must seed _requestMatches in production order.
                if (!_requestMatches.TryGetValue(requestId, out var list))
                    throw new InvalidOperationException(
                        $"FakeMatchRepository.RequestMatches missing seed for {requestId}");
                return list;
            });

        _requestsMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _requests.TryGetValue(id, out var r) ? r : null);

        _aiMock.Setup(x => x.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReplyContext ctx, CancellationToken _) =>
            {
                _lastFacts = ctx.Facts;
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

    private Step1FeedbackHandler Build() =>
        new(_matchesMock.Object, _requestsMock.Object, _feedbackMock.Object,
            _aiMock.Object, _whatsappMock.Object,
            NullLogger<Step1FeedbackHandler>.Instance);

    private MatchEntity SeedMatch()
    {
        var requestId = Guid.NewGuid();
        var match = new MatchEntity
        {
            RequestId = requestId,
            ProviderPhone = "+2203331234",
            ServiceSlug = "plumbing"
        };
        _matches[match.Id] = match;
        _requestMatches[requestId] = new[] { match };

        _requests[requestId] = ServiceRequestEntity.Create(
            "+2203339999", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);
        return match;
    }

    [Fact]
    public async Task Handle_HappyPath_AddsPendingThenSends()
    {
        var match = SeedMatch();

        await Build().Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Single(_added);
        Assert.Single(_sent);
        Assert.Equal(new[] { "TryAdd", "Send" }, _callOrder);
        Assert.Empty(_deleted);
    }

    [Fact]
    public async Task Handle_ExistingPendingRowRacesViaUniqueIndex_NoOp()
    {
        // Simulates the 23505 catch path inside TryAddPendingAsync — i.e., a previous
        // Step1 prompt for this match left a Pending row, so the partial unique index
        // rejects this insert and the handler must exit before sending.
        var match = SeedMatch();
        _tryAddResult = false;

        await Build().Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(_added);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Handle_TryAddPendingFails_NoSend()
    {
        var match = SeedMatch();
        _tryAddResult = false;

        await Build().Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Equal(new[] { "TryAdd" }, _callOrder);
    }

    [Fact]
    public async Task Handle_AiReturnsNull_DeletesPendingRow()
    {
        // AiReplyHelper.TryGenerateAsync surfaces Ollama failures as null. The handler
        // must release the just-reserved pending row, otherwise the partial unique index
        // would lock out future Step1 prompts for this match forever.
        var match = SeedMatch();
        _aiReturnBlank = true;

        await Build().Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

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
            Build().Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None));

        Assert.Single(_added);
        Assert.Single(_deleted);
        Assert.Equal(_added[0].Id, _deleted[0]);
    }

    [Fact]
    public async Task Handle_PerRequestDedupe_SkipsWhenSiblingExists()
    {
        // Multi-PICK: a sibling match in the same request already has a Step1 row
        // (Pending or answered), so Step1FeedbackHandler must exit silently — no
        // TryAddPending, no AI, no send. Dedupe goal: one Step1 prompt per request.
        var match = SeedMatch();
        _anyByRequestStepResult = true;

        await Build().Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(_added);
        Assert.Empty(_sent);
        Assert.Empty(_callOrder);
        // Dedupe must actually call the repository — guards against accidentally
        // dropping the gate behind a refactor that exits before the check.
        Assert.Equal(1, _anyByRequestStepCalled);
    }

    [Fact]
    public async Task Handle_MatchMissing_Silent()
    {
        await Build().Handle(new Step1FeedbackCheck(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(_added);
        Assert.Empty(_sent);
        Assert.Empty(_callOrder);
    }

    [Fact]
    public async Task Handle_RequestMissing_Silent()
    {
        var match = SeedMatch();
        _requests.Remove(match.RequestId);

        await Build().Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(_added);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Handle_BadClientPhone_Silent()
    {
        var match = SeedMatch();
        var requestId = match.RequestId;
        _requests[requestId] = ServiceRequestEntity.Create(
            "not-a-phone", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);

        await Build().Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(_added);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Handle_MultiPick_PreservesPresenterOrdering()
    {
        // Two picked siblings — sibling has the higher Score, so MatchRepository's
        // production order (Score DESC) lists it FIRST regardless of insertion time.
        // The bot's "pickedProviders" fact must use that order so a positional reply
        // ("2") later resolves to the actual second-listed match.
        var requestId = Guid.NewGuid();
        var match = new MatchEntity
        {
            RequestId = requestId,
            ProviderPhone = "+2203331234",
            ServiceSlug = "plumbing",
            Score = 0.5,
            PickedAt = DateTimeOffset.UtcNow
        };
        _matches[match.Id] = match;
        _requests[requestId] = ServiceRequestEntity.Create(
            "+2203339999", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);

        var sibling = new MatchEntity
        {
            RequestId = match.RequestId,
            ProviderPhone = "+2204445678",
            ServiceSlug = "plumbing",
            CreatedAt = match.CreatedAt.AddSeconds(-1),
            PickedAt = DateTimeOffset.UtcNow,
            Score = 0.9
        };
        _matches[sibling.Id] = sibling;
        // Production order: Score DESC -> sibling first, anchor match second.
        _requestMatches[match.RequestId] = new[] { sibling, match };

        await Build().Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Single(_sent);
        Assert.NotNull(_lastFacts);
        Assert.True(_lastFacts!.ContainsKey("pickedProviders"));
        var rendered = _lastFacts["pickedProviders"];
        var firstSlot = rendered.IndexOf("1)", StringComparison.Ordinal);
        var secondSlot = rendered.IndexOf("2)", StringComparison.Ordinal);
        Assert.True(firstSlot >= 0 && secondSlot > firstSlot);
        // PhoneNumber.Mask leaves last 2 digits visible: "+220***78" for sibling
        // (+2204445678), "+220***34" for anchor (+2203331234). Assert sibling
        // (higher Score) lands in slot 1, anchor in slot 2.
        var slot1 = rendered.Substring(firstSlot, secondSlot - firstSlot);
        var slot2 = rendered[secondSlot..];
        Assert.Contains("78", slot1, StringComparison.Ordinal);
        Assert.Contains("34", slot2, StringComparison.Ordinal);
        // instruction must reflect multi-pick branch (mentions "which one").
        Assert.Contains("which one", _lastFacts["instruction"], StringComparison.OrdinalIgnoreCase);
    }
}
