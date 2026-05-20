using Hook.Features.Geocoding.Models;
using Hook.Features.ProviderAvailability;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ProviderAvailability.Register.AdvanceDraft;
using Hook.Features.ProviderAvailability.Register.ExtractServices;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Wolverine;
using AvailabilityEntity = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability;

namespace Hook.UnitTests.ProviderAvailability;

public class AdvanceRegistrationDraftHandlerTests
{
    private readonly Mock<IRegistrationDraftRepository> _draftsMock = new();
    private readonly Mock<IProviderAvailabilityRepository> _availabilityMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly ProviderAvailabilityOptions _options = new();
    private readonly List<SendWhatsAppTextRequested> _sent = [];
    private readonly List<RegistrationDraft> _upserted = [];

    public AdvanceRegistrationDraftHandlerTests()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _sent.Add((SendWhatsAppTextRequested)m))
            .Returns(ValueTask.CompletedTask);
        _draftsMock.Setup(x => x.UpsertAsync(It.IsAny<RegistrationDraft>(), It.IsAny<CancellationToken>()))
            .Callback<RegistrationDraft, CancellationToken>((d, _) => _upserted.Add(d))
            .Returns(Task.CompletedTask);
    }

    private AdvanceRegistrationDraftHandler Build() =>
        new(_draftsMock.Object, _availabilityMock.Object, Options.Create(_options), _clock,
            NullLogger<AdvanceRegistrationDraftHandler>.Instance);

    private RegistrationDraft SeedDraft(RegistrationStep step, params string[] services)
    {
        var draft = RegistrationDraft.Start("+220300001", _clock.GetUtcNow());
        if (services.Length > 0) draft.SetServices(services, _clock.GetUtcNow());
        draft.StepTo(step, _clock.GetUtcNow());
        _draftsMock.Setup(x => x.GetAsync("+220300001", It.IsAny<CancellationToken>())).ReturnsAsync(draft);
        return draft;
    }

    private AvailabilityEntity SeedListed(params string[] services)
    {
        var listed = AvailabilityEntity.Register(
            "+220300001", services, new Location(13.45, -16.6), "Banjul",
            shareContact: true, TimeSpan.FromHours(24), _clock.GetUtcNow());
        _availabilityMock.Setup(x => x.GetAsync("+220300001", It.IsAny<CancellationToken>())).ReturnsAsync(listed);
        return listed;
    }

    [Fact]
    public async Task Handle_InvalidPhone_NoOp()
    {
        await Build().Handle(
            new AdvanceRegistrationDraft("not-a-phone", ["plumbing"], RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_NewRegistration_EmptyCanonical_PromptsAndResetsStep()
    {
        SeedDraft(RegistrationStep.ResolvingServices);

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", [], RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(RegistrationStep.AwaitingServices);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("Tell me what services you offer");
    }

    [Fact]
    public async Task Handle_NewRegistration_BelowCap_PromotesToConfirm()
    {
        SeedDraft(RegistrationStep.ResolvingServices);

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", ["plumbing", "carpentry"], RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(RegistrationStep.ConfirmServices);
        _upserted[0].DraftServices.ShouldBe(["plumbing", "carpentry"]);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("I detected: plumbing, carpentry");
    }

    [Fact]
    public async Task Handle_NewRegistration_ExceedsCap_TruncatesAndAcksMax()
    {
        SeedDraft(RegistrationStep.ResolvingServices);
        var slugs = Enumerable.Range(0, _options.MaxServicesPerProvider + 2).Select(i => $"svc-{i}").ToList();

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", slugs, RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].DraftServices.Count.ShouldBe(_options.MaxServicesPerProvider);
        _sent[0].Text.ShouldContain($"Max {_options.MaxServicesPerProvider} services per provider.");
    }

    [Fact]
    public async Task Handle_AddToExisting_UnlistedProvider_DropsSilently()
    {
        _availabilityMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AvailabilityEntity?)null);

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", ["carpentry"], RegistrationExtractMode.AddToExisting),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AddToExisting_NoNewSlugs_AcksAlreadyListed()
    {
        SeedListed("plumbing", "carpentry");

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", ["plumbing"], RegistrationExtractMode.AddToExisting),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("already listed");
    }

    [Fact]
    public async Task Handle_AddToExisting_AlreadyAtCap_AcksCap()
    {
        var maxed = Enumerable.Range(0, _options.MaxServicesPerProvider).Select(i => $"svc-{i}").ToArray();
        SeedListed(maxed);

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", ["newone"], RegistrationExtractMode.AddToExisting),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("already at the");
    }

    [Fact]
    public async Task Handle_AddToExisting_NewSlugs_ReservesAddDraft()
    {
        SeedListed("plumbing");

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", ["carpentry"], RegistrationExtractMode.AddToExisting),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(RegistrationStep.ConfirmAddServices);
        _upserted[0].DraftServices.ShouldBe(["carpentry"]);
        _sent[0].Text.ShouldContain("I detected: carpentry");
    }

    [Fact]
    public async Task Handle_AppendToDraft_MergesAndCaps()
    {
        SeedDraft(RegistrationStep.ConfirmServices, "plumbing");

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", ["carpentry"], RegistrationExtractMode.AppendToDraft),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].DraftServices.ShouldBe(["plumbing", "carpentry"]);
        _sent[0].Text.ShouldContain("Updated: plumbing, carpentry");
    }

    [Fact]
    public async Task Handle_AppendToDraft_NoNewSlugs_AcksWithoutUpsert()
    {
        SeedDraft(RegistrationStep.ConfirmServices, "plumbing");

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", ["plumbing"], RegistrationExtractMode.AppendToDraft),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent[0].Text.ShouldContain("Already listed: plumbing");
    }

    [Fact]
    public async Task Handle_AppendToDraft_StaleStep_NoOp()
    {
        SeedDraft(RegistrationStep.AwaitingLocation, "plumbing");

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", ["carpentry"], RegistrationExtractMode.AppendToDraft),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AppendToAddDraft_RespectsRemainingCap()
    {
        // existing already has 4 services; cap is 5; pending add draft has 0; new slugs has 2 → only 1 fits.
        var listed = Enumerable.Range(0, _options.MaxServicesPerProvider - 1).Select(i => $"svc-{i}").ToArray();
        SeedListed(listed);
        SeedDraft(RegistrationStep.ConfirmAddServices);

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", ["new-a", "new-b"], RegistrationExtractMode.AppendToAddDraft),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].DraftServices.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_AppendToAddDraft_UnlistedProvider_DropsSilently()
    {
        _availabilityMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AvailabilityEntity?)null);

        await Build().Handle(
            new AdvanceRegistrationDraft("+220300001", ["carpentry"], RegistrationExtractMode.AppendToAddDraft),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }
}
