using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ProviderAvailability.Register.AdvanceDraft;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using AvailabilityEntity = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability;

namespace Hook.UnitTests.ProviderAvailability;

public class ExtendProviderListingHandlerTests : RegistrationHandlerTestBase
{
    private ExtendProviderListingHandler Build() =>
        new(_draftsMock.Object, _availabilityMock.Object, Options.Create(_options), _clock,
            NullLogger<ExtendProviderListingHandler>.Instance);

    [Fact]
    public async Task Handle_UnlistedProvider_DropsSilently()
    {
        _availabilityMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AvailabilityEntity?)null);

        await Build().Handle(
            new ExtendProviderListingCommand(TestPhone, ["carpentry"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_NoNewSlugs_AcksAlreadyListed()
    {
        SeedListed("plumbing", "carpentry");

        await Build().Handle(
            new ExtendProviderListingCommand(TestPhone, ["plumbing"]),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("already listed");
    }

    [Fact]
    public async Task Handle_AlreadyAtCap_AcksCap()
    {
        var maxed = Enumerable.Range(0, _options.MaxServicesPerProvider).Select(i => $"svc-{i}").ToArray();
        SeedListed(maxed);

        await Build().Handle(
            new ExtendProviderListingCommand(TestPhone, ["newone"]),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("already at the");
    }

    [Fact]
    public async Task Handle_NewSlugs_ReservesAddDraft()
    {
        SeedListed("plumbing");

        await Build().Handle(
            new ExtendProviderListingCommand(TestPhone, ["carpentry"]),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(RegistrationStep.ConfirmAddServices);
        _upserted[0].DraftServices.ShouldBe(["carpentry"]);
        _sent[0].Text.ShouldContain("I detected: carpentry");
    }

    [Fact]
    public async Task Handle_InvalidPhone_NoOp()
    {
        await Build().Handle(
            new ExtendProviderListingCommand("not-a-phone", ["plumbing"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }
}
