using Hook.Features.ServiceRequest.Create;
using Hook.Features.ServiceRequest.Create.AdvanceDraft;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.ServiceRequest;

public class ClientServiceResolutionHandlerTests
{
    private readonly Mock<IClientRequestDraftRepository> _draftsMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly List<SendWhatsAppTextCommand> _sent = [];
    private readonly List<ClientRequestDraft> _upserted = [];

    public ClientServiceResolutionHandlerTests()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _sent.Add((SendWhatsAppTextCommand)m))
            .Returns(ValueTask.CompletedTask);
        _draftsMock.Setup(x => x.UpsertAsync(It.IsAny<ClientRequestDraft>(), It.IsAny<CancellationToken>()))
            .Callback<ClientRequestDraft, CancellationToken>((d, _) => _upserted.Add(d))
            .Returns(Task.CompletedTask);
    }

    private ApplyClientServiceResolutionHandler BuildApply() =>
        new(_draftsMock.Object, _clock, NullLogger<ApplyClientServiceResolutionHandler>.Instance);

    private ResetClientServiceResolutionHandler BuildReset() =>
        new(_draftsMock.Object, _clock, NullLogger<ResetClientServiceResolutionHandler>.Instance);

    private ClientRequestDraft SeedDraft(ClientRequestStep step) => SeedDraft(step, "+220300001");

    private ClientRequestDraft SeedDraft(ClientRequestStep step, string phone)
    {
        var draft = ClientRequestDraft.Start(phone, _clock.GetUtcNow());
        draft.StepTo(step, _clock.GetUtcNow());
        _draftsMock.Setup(x => x.GetAsync(phone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);
        return draft;
    }

    [Fact]
    public async Task ApplyHandle_DraftNotFound_NoOp()
    {
        _draftsMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClientRequestDraft?)null);

        await BuildApply().Handle(
            new ApplyClientServiceResolutionCommand("+220300001", "plumbing", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyHandle_InvalidPhone_NoOp()
    {
        // Seed against the invalid phone so the draft IS found and the
        // phone-parse branch is the one that returns.
        SeedDraft(ClientRequestStep.ResolvingService, "not-a-phone");

        await BuildApply().Handle(
            new ApplyClientServiceResolutionCommand("not-a-phone", "plumbing", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResetHandle_DraftNotFound_NoOp()
    {
        _draftsMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClientRequestDraft?)null);

        await BuildReset().Handle(
            new ResetClientServiceResolutionCommand("+220300001", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResetHandle_InvalidPhone_NoOp()
    {
        SeedDraft(ClientRequestStep.AwaitingService, "not-a-phone");

        await BuildReset().Handle(
            new ResetClientServiceResolutionCommand("not-a-phone", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResetHandle_StartPath_ResetsToAwaitingServiceAndPrompts()
    {
        SeedDraft(ClientRequestStep.ResolvingService);

        await BuildReset().Handle(
            new ResetClientServiceResolutionCommand("+220300001", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(ClientRequestStep.AwaitingService);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("What service do you need?");
    }

    [Fact]
    public async Task ResetHandle_SwitchPath_StaysAndAcksUser()
    {
        // Switch-path race: user keeps moving the funnel while LLM runs.
        // The handler must not interrupt the funnel but should acknowledge.
        SeedDraft(ClientRequestStep.AwaitingDescription);

        await BuildReset().Handle(
            new ResetClientServiceResolutionCommand("+220300001", IsSwitch: true),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("Couldn't catch that");
    }

    [Fact]
    public async Task ApplyHandle_StartPath_PromotesToConfirmService()
    {
        SeedDraft(ClientRequestStep.ResolvingService);

        await BuildApply().Handle(
            new ApplyClientServiceResolutionCommand("+220300001", "plumbing", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(ClientRequestStep.ConfirmService);
        _upserted[0].DraftServiceSlug.ShouldBe("plumbing");
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("Do you need plumbing?");
    }

    [Fact]
    public async Task ApplyHandle_SwitchPath_StaleStep_DoesNothing()
    {
        // User advanced past the location steps while LLM ran.
        SeedDraft(ClientRequestStep.AwaitingPhoneShareConsent);

        await BuildApply().Handle(
            new ApplyClientServiceResolutionCommand("+220300001", "carpentry", IsSwitch: true),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyHandle_SwitchPath_SameSlug_NoOp()
    {
        var draft = SeedDraft(ClientRequestStep.AwaitingLocation);
        draft.SwitchSlug("plumbing", _clock.GetUtcNow());

        await BuildApply().Handle(
            new ApplyClientServiceResolutionCommand("+220300001", "plumbing", IsSwitch: true),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyHandle_SwitchPath_NewSlug_SwitchesAndAsksConfirm()
    {
        var draft = SeedDraft(ClientRequestStep.AwaitingLocation);
        draft.SwitchSlug("plumbing", _clock.GetUtcNow());

        await BuildApply().Handle(
            new ApplyClientServiceResolutionCommand("+220300001", "carpentry", IsSwitch: true),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(ClientRequestStep.ConfirmService);
        _upserted[0].DraftServiceSlug.ShouldBe("carpentry");
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("Switching to carpentry");
    }
}
