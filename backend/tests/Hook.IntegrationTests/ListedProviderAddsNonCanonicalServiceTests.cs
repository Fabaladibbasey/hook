using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-3")]
public class ListedProviderAddsNonCanonicalServiceTests : PipelineTestBase
{
    public ListedProviderAddsNonCanonicalServiceTests(DevPipelineFixture fx) : base(fx) { }

    // A listed provider proposing a non-canonical profession must be asked to
    // confirm before their listing mutates — symmetric to the new-registration
    // YES/EDIT step. Silent appends erase the user's ability to retract a
    // mis-extracted slug and (per past bug) let bad AI matches land in the DB
    // without warning.
    [Fact]
    public async Task ListedProvider_AddsOpenDomainProfession_PromptsConfirmThenAppends()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010088";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer electrical");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var listed = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));

        await _fx.InjectTextAndAwaitAsync(phone, "I am django developer");

        var detected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase),
            since: listed.At);
        detected.Body.ShouldContain("django-developer", Case.Insensitive);
        detected.Body.ShouldContain("YES", Case.Insensitive);
        detected.Body.ShouldContain("EDIT", Case.Insensitive);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var availability = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
            var pending = await availability.GetAsync(phone);
            pending.ShouldNotBeNull();
            pending!.Services.ShouldBe(new[] { "electrical" });
        }

        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var ack = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Added ", StringComparison.OrdinalIgnoreCase),
            since: detected.At);

        ack.Body.ShouldContain("django-developer", Case.Insensitive);
        ack.Body.ShouldContain("electrical", Case.Insensitive);
        ack.Body.ShouldContain("LEAVE", Case.Insensitive);

        using var verifyScope = _fx.Factory.Services.CreateScope();
        var availabilityRepo = verifyScope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
        var stored = await availabilityRepo.GetAsync(phone);
        stored.ShouldNotBeNull();
        stored!.Services.ShouldContain("electrical");
        stored.Services.ShouldContain("django-developer");

        var drafts = verifyScope.ServiceProvider.GetRequiredService<IRegistrationDraftRepository>();
        (await drafts.GetAsync(phone)).ShouldBeNull();
    }

    // Spelling-normalization path: provider sends a phonetic typo of "analyst".
    // FakeConversationAi's typo map produces `data-analyst`; the confirmation
    // step still gates the append so the user sees the normalized slug before
    // it lands.
    [Fact]
    public async Task ListedProvider_AddsTypoedProfession_NormalizesSpellingInConfirmPrompt()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010089";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer electrical");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var listed = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));

        await _fx.InjectTextAndAwaitAsync(phone, "I am a data analysist");

        var detected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase),
            since: listed.At);
        detected.Body.ShouldContain("data-analyst", Case.Insensitive);
        detected.Body.ShouldNotContain("analysist", Case.Insensitive);

        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var ack = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Added ", StringComparison.OrdinalIgnoreCase),
            since: detected.At);
        ack.Body.ShouldContain("data-analyst", Case.Insensitive);
    }

    // EDIT path: provider rejects the proposal mid-confirm. The pending draft
    // is cleared, services stay as they were, and the bot asks for a fresh
    // corrected list. Mirrors the new-registration EDIT semantics.
    [Fact]
    public async Task ListedProvider_AddsService_EditClearsDraftAndReprompts()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010090";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer electrical");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var listed = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));

        await _fx.InjectTextAndAwaitAsync(phone, "I do django developer");

        var detected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase),
            since: listed.At);

        await _fx.InjectTextAndAwaitAsync(phone, "edit");

        var reprompt = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("corrected list", StringComparison.OrdinalIgnoreCase),
            since: detected.At);
        reprompt.Body.ShouldNotBeNullOrEmpty();

        using var scope = _fx.Factory.Services.CreateScope();
        var availability = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
        var stored = await availability.GetAsync(phone);
        stored.ShouldNotBeNull();
        stored!.Services.ShouldBe(new[] { "electrical" });

        var drafts = scope.ServiceProvider.GetRequiredService<IRegistrationDraftRepository>();
        (await drafts.GetAsync(phone)).ShouldBeNull();
    }
}
