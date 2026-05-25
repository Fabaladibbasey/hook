using Hook.Features.ServiceRequest.Create;
using Hook.Features.ServiceRequest.Create.ConfirmIntent;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.ServiceRequest.Create.ConfirmIntent;

public class ApplyConfirmIntentHandlerTests
{
    private readonly Mock<IClientRequestDraftRepository> _draftsMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly List<object> _published = [];
    private readonly List<ClientRequestDraft> _upserted = [];

    public ApplyConfirmIntentHandlerTests()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<object>(), It.IsAny<DeliveryOptions?>()))
            .Callback<object, DeliveryOptions?>((m, _) => _published.Add(m))
            .Returns(ValueTask.CompletedTask);
        _draftsMock.Setup(x => x.UpsertAsync(It.IsAny<ClientRequestDraft>(), It.IsAny<CancellationToken>()))
            .Callback<ClientRequestDraft, CancellationToken>((d, _) => _upserted.Add(d))
            .Returns(Task.CompletedTask);
    }

    private ApplyConfirmIntentHandler Build() =>
        new(_draftsMock.Object, _clock, NullLogger<ApplyConfirmIntentHandler>.Instance);

    private ClientRequestDraft SeedDraft(
        string phone,
        ClientRequestStep step,
        string slug = "plumbing",
        (double Lat, double Lon, string Address)? location = null)
    {
        var draft = ClientRequestDraft.Start(phone, _clock.GetUtcNow());
        if (!string.IsNullOrEmpty(slug)) draft.SwitchSlug(slug, _clock.GetUtcNow());
        if (location is { } loc)
            draft.CaptureLocation(loc.Lat, loc.Lon, loc.Address, _clock.GetUtcNow());
        draft.StepTo(step, _clock.GetUtcNow());
        _draftsMock.Setup(x => x.GetAsync(phone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);
        return draft;
    }

    [Fact]
    public async Task Yes_NoLocation_AdvancesToAwaitingLocation()
    {
        const string phone = "+220700001001";
        var draft = SeedDraft(phone, ClientRequestStep.ConfirmService);

        await Build().Handle(
            new ApplyConfirmIntentCommand(phone, ConfirmReplyIntent.Yes, draft.UpdatedAt),
            _busMock.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.AwaitingLocation);
        _upserted.ShouldHaveSingleItem();
        var sent = _published.OfType<SendWhatsAppTextCommand>().ShouldHaveSingleItem();
        sent.Text.ShouldContain("location pin", Case.Insensitive);
    }

    [Fact]
    public async Task Yes_WithLocation_AdvancesToAwaitingDescription()
    {
        const string phone = "+220700001002";
        var draft = SeedDraft(phone, ClientRequestStep.ConfirmService,
            location: (13.4549, -16.5790, "Banjul"));

        await Build().Handle(
            new ApplyConfirmIntentCommand(phone, ConfirmReplyIntent.Yes, draft.UpdatedAt),
            _busMock.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.AwaitingDescription);
        var sent = _published.OfType<SendWhatsAppTextCommand>().ShouldHaveSingleItem();
        sent.Text.ShouldContain("description", Case.Insensitive);
        sent.Text.ShouldContain("Banjul");
    }

    [Fact]
    public async Task No_ResetsToAwaitingService()
    {
        const string phone = "+220700001003";
        var draft = SeedDraft(phone, ClientRequestStep.ConfirmService);

        await Build().Handle(
            new ApplyConfirmIntentCommand(phone, ConfirmReplyIntent.No, draft.UpdatedAt),
            _busMock.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.AwaitingService);
        draft.DraftServiceSlug.ShouldBe(string.Empty);
        var sent = _published.OfType<SendWhatsAppTextCommand>().ShouldHaveSingleItem();
        sent.Text.ShouldContain("What service", Case.Insensitive);
    }

    [Fact]
    public async Task Unsure_Reprompts_WithoutMutatingDraft()
    {
        const string phone = "+220700001004";
        var draft = SeedDraft(phone, ClientRequestStep.ConfirmService);
        var preStamp = draft.UpdatedAt;

        await Build().Handle(
            new ApplyConfirmIntentCommand(phone, ConfirmReplyIntent.Unsure, draft.UpdatedAt),
            _busMock.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.ConfirmService);
        draft.UpdatedAt.ShouldBe(preStamp);
        _upserted.ShouldBeEmpty();
        var sent = _published.OfType<SendWhatsAppTextCommand>().ShouldHaveSingleItem();
        sent.Text.ShouldContain("YES or NO", Case.Insensitive);
        sent.Text.ShouldContain("plumbing");
    }

    [Fact]
    public async Task SubMicrosecondSkew_NotTreatedAsStale()
    {
        const string phone = "+220700001008";
        var draft = SeedDraft(phone, ClientRequestStep.ConfirmService);
        // Simulate Postgres timestamptz truncation: envelope captured 7 ticks (700 ns)
        // earlier than the round-tripped draft.UpdatedAt.
        var envelopeStamp = draft.UpdatedAt - TimeSpan.FromTicks(7);

        await Build().Handle(
            new ApplyConfirmIntentCommand(phone, ConfirmReplyIntent.Yes, envelopeStamp),
            _busMock.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.AwaitingLocation);
        _upserted.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task StaleStamp_NoOp()
    {
        const string phone = "+220700001005";
        var draft = SeedDraft(phone, ClientRequestStep.ConfirmService);
        var stale = draft.UpdatedAt - TimeSpan.FromMinutes(5);

        await Build().Handle(
            new ApplyConfirmIntentCommand(phone, ConfirmReplyIntent.Yes, stale),
            _busMock.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.ConfirmService);
        _upserted.ShouldBeEmpty();
        _published.ShouldBeEmpty();
    }

    [Fact]
    public async Task DraftDeleted_NoOp()
    {
        const string phone = "+220700001006";
        _draftsMock.Setup(x => x.GetAsync(phone, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClientRequestDraft?)null);

        await Build().Handle(
            new ApplyConfirmIntentCommand(phone, ConfirmReplyIntent.Yes, _clock.GetUtcNow()),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _published.ShouldBeEmpty();
    }

    [Fact]
    public async Task WrongStep_NoOp()
    {
        const string phone = "+220700001007";
        var draft = SeedDraft(phone, ClientRequestStep.AwaitingLocation);

        await Build().Handle(
            new ApplyConfirmIntentCommand(phone, ConfirmReplyIntent.Yes, draft.UpdatedAt),
            _busMock.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.AwaitingLocation);
        _upserted.ShouldBeEmpty();
        _published.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnknownIntent_ThrowsArgumentOutOfRange()
    {
        const string phone = "+220700001009";
        var draft = SeedDraft(phone, ClientRequestStep.ConfirmService);
        var unknown = (ConfirmReplyIntent)99;

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            Build().Handle(
                new ApplyConfirmIntentCommand(phone, unknown, draft.UpdatedAt),
                _busMock.Object, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentSecondInbound_AdvancesStamp_LateApplyDrops()
    {
        const string phone = "+220700001010";
        var draft = SeedDraft(phone, ClientRequestStep.ConfirmService);
        var staleStamp = draft.UpdatedAt;

        // Second inbound landed 50ms later (well outside the 10-tick tolerance)
        // and bumped UpdatedAt — the original envelope must now drop.
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        draft.Touch(_clock.GetUtcNow());

        await Build().Handle(
            new ApplyConfirmIntentCommand(phone, ConfirmReplyIntent.Yes, staleStamp),
            _busMock.Object, CancellationToken.None);

        draft.Step.ShouldBe(ClientRequestStep.ConfirmService);
        _upserted.ShouldBeEmpty();
        _published.ShouldBeEmpty();
    }
}
