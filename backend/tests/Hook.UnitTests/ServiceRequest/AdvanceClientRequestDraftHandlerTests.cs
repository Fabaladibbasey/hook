using Hook.Features.ServiceRequest.Create;
using Hook.Features.ServiceRequest.Create.AdvanceDraft;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.ServiceRequest;

public class AdvanceClientRequestDraftHandlerTests
{
    private readonly Mock<IClientRequestDraftRepository> _draftsMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly List<SendWhatsAppTextRequested> _sent = [];
    private readonly List<ClientRequestDraft> _upserted = [];

    public AdvanceClientRequestDraftHandlerTests()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _sent.Add((SendWhatsAppTextRequested)m))
            .Returns(ValueTask.CompletedTask);
        _draftsMock.Setup(x => x.UpsertAsync(It.IsAny<ClientRequestDraft>(), It.IsAny<CancellationToken>()))
            .Callback<ClientRequestDraft, CancellationToken>((d, _) => _upserted.Add(d))
            .Returns(Task.CompletedTask);
    }

    private AdvanceClientRequestDraftHandler Build() =>
        new(_draftsMock.Object, _clock, NullLogger<AdvanceClientRequestDraftHandler>.Instance);

    private ClientRequestDraft SeedDraft(ClientRequestStep step)
    {
        var draft = ClientRequestDraft.Start("+220300001", _clock.GetUtcNow());
        draft.StepTo(step, _clock.GetUtcNow());
        _draftsMock.Setup(x => x.GetAsync("+220300001", It.IsAny<CancellationToken>())).ReturnsAsync(draft);
        return draft;
    }

    [Fact]
    public async Task Handle_DraftNotFound_NoOp()
    {
        _draftsMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClientRequestDraft?)null);

        await Build().Handle(
            new AdvanceClientRequestDraft("+220300001", "plumbing", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_InvalidPhone_NoOp()
    {
        SeedDraft(ClientRequestStep.ResolvingService);

        await Build().Handle(
            new AdvanceClientRequestDraft("not-a-phone", "plumbing", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_StartPath_EmptyCanonical_ResetsToAwaitingServiceAndPrompts()
    {
        SeedDraft(ClientRequestStep.ResolvingService);

        await Build().Handle(
            new AdvanceClientRequestDraft("+220300001", string.Empty, IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(ClientRequestStep.AwaitingService);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("What service do you need?");
    }

    [Fact]
    public async Task Handle_SwitchPath_EmptyCanonical_StaysAndAcksUser()
    {
        // Switch-path race: user keeps moving the funnel while LLM runs.
        // The handler must not interrupt the funnel but should acknowledge.
        SeedDraft(ClientRequestStep.AwaitingDescription);

        await Build().Handle(
            new AdvanceClientRequestDraft("+220300001", string.Empty, IsSwitch: true),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("Couldn't catch that");
    }

    [Fact]
    public async Task Handle_StartPath_WithSlug_PromotesToConfirmService()
    {
        SeedDraft(ClientRequestStep.ResolvingService);

        await Build().Handle(
            new AdvanceClientRequestDraft("+220300001", "plumbing", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(ClientRequestStep.ConfirmService);
        _upserted[0].DraftServiceSlug.ShouldBe("plumbing");
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("Do you need plumbing?");
    }

    [Fact]
    public async Task Handle_SwitchPath_StaleStep_DoesNothing()
    {
        // User advanced past the location steps while LLM ran.
        SeedDraft(ClientRequestStep.AwaitingPhoneShareConsent);

        await Build().Handle(
            new AdvanceClientRequestDraft("+220300001", "carpentry", IsSwitch: true),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_SwitchPath_SameSlug_NoOp()
    {
        var draft = SeedDraft(ClientRequestStep.AwaitingLocation);
        draft.SwitchSlug("plumbing", _clock.GetUtcNow());

        await Build().Handle(
            new AdvanceClientRequestDraft("+220300001", "plumbing", IsSwitch: true),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_SwitchPath_NewSlug_SwitchesAndAsksConfirm()
    {
        var draft = SeedDraft(ClientRequestStep.AwaitingLocation);
        draft.SwitchSlug("plumbing", _clock.GetUtcNow());

        await Build().Handle(
            new AdvanceClientRequestDraft("+220300001", "carpentry", IsSwitch: true),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(ClientRequestStep.ConfirmService);
        _upserted[0].DraftServiceSlug.ShouldBe("carpentry");
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("Switching to carpentry");
    }
}
