using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ProviderAvailability.Register.AdvanceDraft;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using AvailabilityEntity = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability;

namespace Hook.UnitTests.ProviderAvailability;

public class AmendAddServicesDraftHandlerTests : RegistrationHandlerTestBase
{
    private AmendAddServicesDraftHandler Build() =>
        new(_draftsMock.Object, _availabilityMock.Object, Options.Create(_options), _clock,
            NullLogger<AmendAddServicesDraftHandler>.Instance);

    private void SeedDraft(RegistrationStep step, params string[] services)
    {
        var draft = RegistrationDraft.Start(TestPhone, _clock.GetUtcNow());
        if (services.Length > 0) draft.SetServices(services, _clock.GetUtcNow());
        draft.StepTo(step, _clock.GetUtcNow());
        _draftsMock.Setup(x => x.GetAsync(TestPhone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);
    }

    [Fact]
    public async Task Handle_RespectsRemainingCap()
    {
        // existing already has (max-1) services; pending add draft has 0; new slugs has 2 → only 1 fits.
        var listed = Enumerable.Range(0, _options.MaxServicesPerProvider - 1).Select(i => $"svc-{i}").ToArray();
        SeedListed(listed);
        SeedDraft(RegistrationStep.ConfirmAddServices);

        await Build().Handle(
            new AmendAddServicesDraftCommand(TestPhone, ["new-a", "new-b"]),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].DraftServices.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_UnlistedProvider_DropsSilently()
    {
        _availabilityMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AvailabilityEntity?)null);

        await Build().Handle(
            new AmendAddServicesDraftCommand(TestPhone, ["carpentry"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_InvalidPhone_NoOp()
    {
        await Build().Handle(
            new AmendAddServicesDraftCommand("not-a-phone", ["plumbing"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_DraftNull_DropsSilently()
    {
        SeedListed("plumbing");
        // no SeedDraft — drafts.GetAsync returns null

        await Build().Handle(
            new AmendAddServicesDraftCommand(TestPhone, ["carpentry"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_StaleStep_DropsSilently()
    {
        SeedListed("plumbing");
        SeedDraft(RegistrationStep.AwaitingServices);

        await Build().Handle(
            new AmendAddServicesDraftCommand(TestPhone, ["carpentry"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_NoNewSlugs_AcksWithoutUpsert()
    {
        SeedListed("plumbing");
        SeedDraft(RegistrationStep.ConfirmAddServices, "carpentry");

        await Build().Handle(
            new AmendAddServicesDraftCommand(TestPhone, ["carpentry"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldHaveSingleItem().Text.ShouldContain("Pending add");
        _upserted.ShouldBeEmpty();
    }
}
