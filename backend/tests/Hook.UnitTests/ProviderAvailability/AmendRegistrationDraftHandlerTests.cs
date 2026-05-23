using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ProviderAvailability.Register.AdvanceDraft;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Hook.UnitTests.ProviderAvailability;

public class AmendRegistrationDraftHandlerTests : RegistrationHandlerTestBase
{
    private AmendRegistrationDraftHandler Build() =>
        new(_draftsMock.Object, Options.Create(_options), _clock,
            NullLogger<AmendRegistrationDraftHandler>.Instance);

    private void SeedDraft(RegistrationStep step, params string[] services)
    {
        var draft = RegistrationDraft.Start(TestPhone, _clock.GetUtcNow());
        if (services.Length > 0) draft.SetServices(services, _clock.GetUtcNow());
        draft.StepTo(step, _clock.GetUtcNow());
        _draftsMock.Setup(x => x.GetAsync(TestPhone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);
    }

    [Fact]
    public async Task Handle_MergesAndCaps()
    {
        SeedDraft(RegistrationStep.ConfirmServices, "plumbing");

        await Build().Handle(
            new AmendRegistrationDraftCommand(TestPhone, ["carpentry"]),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].DraftServices.ShouldBe(["plumbing", "carpentry"]);
        _sent[0].Text.ShouldContain("Updated: plumbing, carpentry");
    }

    [Fact]
    public async Task Handle_NoNewSlugs_AcksWithoutUpsert()
    {
        SeedDraft(RegistrationStep.ConfirmServices, "plumbing");

        await Build().Handle(
            new AmendRegistrationDraftCommand(TestPhone, ["plumbing"]),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent[0].Text.ShouldContain("Already listed: plumbing");
    }

    [Fact]
    public async Task Handle_StaleStep_NoOp()
    {
        SeedDraft(RegistrationStep.AwaitingLocation, "plumbing");

        await Build().Handle(
            new AmendRegistrationDraftCommand(TestPhone, ["carpentry"]),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldBeEmpty();
        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_InvalidPhone_NoOp()
    {
        await Build().Handle(
            new AmendRegistrationDraftCommand("not-a-phone", ["plumbing"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_DraftNull_NoOp()
    {
        // No SeedDraft — drafts.GetAsync returns null. Mirrors
        // AmendAddServicesDraftHandlerTests.Handle_DraftNull_DropsSilently so the three
        // Amend* handlers cover identical guard surfaces.
        await Build().Handle(
            new AmendRegistrationDraftCommand(TestPhone, ["plumbing"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AppendBeyondCap_ClipsAtMaxServices()
    {
        var max = _options.MaxServicesPerProvider;
        var existing = Enumerable.Range(1, max - 1).Select(i => $"svc-{i}").ToArray();
        SeedDraft(RegistrationStep.ConfirmServices, existing);

        await Build().Handle(
            new AmendRegistrationDraftCommand(TestPhone, ["new-a", "new-b", "new-c"]),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].DraftServices.Count.ShouldBe(max);
    }
}
