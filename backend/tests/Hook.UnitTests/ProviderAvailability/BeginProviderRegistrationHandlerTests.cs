using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ProviderAvailability.Register.AdvanceDraft;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Hook.UnitTests.ProviderAvailability;

public class BeginProviderRegistrationHandlerTests : RegistrationHandlerTestBase
{
    private BeginProviderRegistrationHandler Build() =>
        new(_draftsMock.Object, Options.Create(_options), _clock, NullLogger<BeginProviderRegistrationHandler>.Instance);

    private void SeedDraft(RegistrationStep step)
    {
        var draft = RegistrationDraft.Start(TestPhone, _clock.GetUtcNow());
        draft.StepTo(step, _clock.GetUtcNow());
        _draftsMock.Setup(x => x.GetAsync(TestPhone, It.IsAny<CancellationToken>())).ReturnsAsync(draft);
    }

    [Fact]
    public async Task Handle_InvalidPhone_NoOp()
    {
        await Build().Handle(
            new BeginProviderRegistrationCommand("not-a-phone", ["plumbing"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_EmptyCanonical_PromptsAndResetsStep()
    {
        SeedDraft(RegistrationStep.ResolvingServices);

        await Build().Handle(
            new BeginProviderRegistrationCommand(TestPhone, []),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(RegistrationStep.AwaitingServices);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("Tell me what services you offer");
    }

    [Fact]
    public async Task Handle_BelowCap_PromotesToConfirm()
    {
        SeedDraft(RegistrationStep.ResolvingServices);

        await Build().Handle(
            new BeginProviderRegistrationCommand(TestPhone, ["plumbing", "carpentry"]),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].Step.ShouldBe(RegistrationStep.ConfirmServices);
        _upserted[0].DraftServices.ShouldBe(["plumbing", "carpentry"]);
        _sent.ShouldHaveSingleItem();
        _sent[0].Text.ShouldContain("I detected: plumbing, carpentry");
    }

    [Fact]
    public async Task Handle_ExceedsCap_TruncatesAndAcksMax()
    {
        SeedDraft(RegistrationStep.ResolvingServices);
        var slugs = Enumerable.Range(0, _options.MaxServicesPerProvider + 2).Select(i => $"svc-{i}").ToList();

        await Build().Handle(
            new BeginProviderRegistrationCommand(TestPhone, slugs),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].DraftServices.Count.ShouldBe(_options.MaxServicesPerProvider);
        _sent[0].Text.ShouldContain($"Max {_options.MaxServicesPerProvider} services per provider.");
    }

    [Fact]
    public async Task Handle_DuplicatesCollapseUnderCap_AcksDetectedNotMax()
    {
        SeedDraft(RegistrationStep.ResolvingServices);
        // 6 raw slugs, only 4 distinct
        var slugs = new[] { "a", "b", "c", "d", "a", "b" };

        await Build().Handle(
            new BeginProviderRegistrationCommand(TestPhone, slugs),
            _busMock.Object, CancellationToken.None);

        _upserted.ShouldHaveSingleItem();
        _upserted[0].DraftServices.Count.ShouldBe(4);
        _sent[0].Text.ShouldNotContain("Max");
        _sent[0].Text.ShouldContain("I detected");
    }

    [Fact]
    public async Task Handle_DraftNull_NoOp()
    {
        // no SeedDraft — drafts.GetAsync returns null
        await Build().Handle(
            new BeginProviderRegistrationCommand(TestPhone, ["plumbing"]),
            _busMock.Object, CancellationToken.None);

        _sent.ShouldBeEmpty();
        _upserted.ShouldBeEmpty();
    }
}
