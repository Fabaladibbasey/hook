using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-3")]
public class ProviderRegistrationFunnelTests : PipelineTestBase
{
    public ProviderRegistrationFunnelTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task Registration_ServiceCap_TruncatesToFive()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010001";

        await _fx.InjectTextAndAwaitAsync(
            phone,
            "I offer plumbing carpentry computer painting electrical mechanic delivery");

        var capped = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Max 5 services per provider", StringComparison.OrdinalIgnoreCase));

        capped.Body.ShouldContain("Keeping:");
        capped.Body.ShouldContain("plumbing");
        capped.Body.ShouldContain("carpentry");
        capped.Body.ShouldContain("computer-repair");
        capped.Body.ShouldContain("delivery");
        capped.Body.ShouldContain("painting");
        capped.Body.ShouldNotContain("electrical");
        capped.Body.ShouldNotContain("auto-repair");
    }

    [Fact]
    public async Task Registration_DuplicateSlugs_AreDeduped()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010002";

        await _fx.InjectTextAndAwaitAsync(phone, "I do plumbing and plumber repair");

        var detected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));

        var occurrences = CountOccurrences(detected.Body, "plumbing");
        occurrences.ShouldBe(1);
    }

    [Fact]
    public async Task Registration_EditAddsService_ReprompstWithLargerList()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010003";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        var firstDetected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));
        firstDetected.Body.ShouldContain("plumbing");
        firstDetected.Body.ShouldNotContain("carpentry");

        await _fx.InjectTextAndAwaitAsync(phone, "edit");
        await _fx.InjectTextAndAwaitAsync(phone, "plumbing and carpentry");

        var secondDetected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase),
            since: firstDetected.At);

        secondDetected.Body.ShouldContain("plumbing");
        secondDetected.Body.ShouldContain("carpentry");
    }

    [Fact]
    public async Task Registration_EditRemovesService_ReprompstWithSmallerList()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010004";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing and carpentry");
        var firstDetected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));
        firstDetected.Body.ShouldContain("plumbing");
        firstDetected.Body.ShouldContain("carpentry");

        await _fx.InjectTextAndAwaitAsync(phone, "edit");
        await _fx.InjectTextAndAwaitAsync(phone, "plumbing only");

        var secondDetected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase),
            since: firstDetected.At);

        secondDetected.Body.ShouldContain("plumbing");
        secondDetected.Body.ShouldNotContain("carpentry");
    }

    [Fact]
    public async Task Registration_TextAddress_GeocodedAndConfirmedThenListed()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010005";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer carpentry");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectTextAndAwaitAsync(phone, "1 Market St San Francisco");

        var found = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Found:", StringComparison.OrdinalIgnoreCase));
        found.Body.ShouldContain("Market St");

        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var listed = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));
        listed.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Registration_ConsentRejected_ListsProviderWithShareContactFalse()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010006";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer carpentry");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "no");

        await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));

        using var scope = _fx.Factory.Services.CreateScope();
        var availability = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
        var stored = await availability.GetAsync(phone);
        stored.ShouldNotBeNull();
        stored!.ShareContact.ShouldBeFalse();
        stored.Services.ShouldContain("carpentry");
    }

    [Fact]
    public async Task EndsSession_ProviderFunnel_DeletesDraftAndReplies()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000093";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        await _fx.InjectTextAndAwaitAsync(phone, "leave");

        var ended = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Session ended", StringComparison.OrdinalIgnoreCase));
        ended.ShouldNotBeNull();

        using var scope = _fx.Factory.Services.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<IRegistrationDraftRepository>();
        (await drafts.GetAsync(phone, default)).ShouldBeNull();
    }

    [Fact]
    public async Task FuzzyConfirmation_TypoYse_AdvancesProviderFunnel()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000094";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        await _fx.InjectTextAndAwaitAsync(phone, "yse");

        var advanced = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Send your location", StringComparison.OrdinalIgnoreCase));
        advanced.ShouldNotBeNull();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
