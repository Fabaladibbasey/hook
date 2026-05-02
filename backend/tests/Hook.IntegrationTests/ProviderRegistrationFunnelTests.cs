using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

public class ProviderRegistrationFunnelTests : IClassFixture<DevPipelineFixture>
{
    private readonly DevPipelineFixture _fx;

    public ProviderRegistrationFunnelTests(DevPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Registration_ServiceCap_TruncatesToFive()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010001";

        (await client.InjectTextAsync(
            phone,
            "I offer plumbing carpentry computer painting electrical mechanic delivery"))
            .EnsureSuccessStatusCode();

        var capped = await client.WaitForOutboundAsync(
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

        (await client.InjectTextAsync(phone, "I do plumbing and plumber repair")).EnsureSuccessStatusCode();

        var detected = await client.WaitForOutboundAsync(
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

        (await client.InjectTextAsync(phone, "I offer plumbing")).EnsureSuccessStatusCode();
        var firstDetected = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));
        firstDetected.Body.ShouldContain("plumbing");
        firstDetected.Body.ShouldNotContain("carpentry");

        (await client.InjectTextAsync(phone, "edit")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("corrected list", StringComparison.OrdinalIgnoreCase),
            since: firstDetected.At);

        (await client.InjectTextAsync(phone, "plumbing and carpentry")).EnsureSuccessStatusCode();
        var secondDetected = await client.WaitForOutboundAsync(
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

        (await client.InjectTextAsync(phone, "I offer plumbing and carpentry")).EnsureSuccessStatusCode();
        var firstDetected = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));
        firstDetected.Body.ShouldContain("plumbing");
        firstDetected.Body.ShouldContain("carpentry");

        (await client.InjectTextAsync(phone, "edit")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("corrected list", StringComparison.OrdinalIgnoreCase),
            since: firstDetected.At);

        (await client.InjectTextAsync(phone, "plumbing only")).EnsureSuccessStatusCode();
        var secondDetected = await client.WaitForOutboundAsync(
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

        (await client.InjectTextAsync(phone, "I offer carpentry")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("location pin", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "1 Market St San Francisco")).EnsureSuccessStatusCode();
        var found = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("Found:", StringComparison.OrdinalIgnoreCase));
        found.Body.ShouldContain("Market St");

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Share your phone", StringComparison.OrdinalIgnoreCase),
            since: found.At);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var listed = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));
        listed.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Registration_ConsentRejected_ListsProviderWithShareContactFalse()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207010006";

        (await client.InjectTextAsync(phone, "I offer carpentry")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin", StringComparison.OrdinalIgnoreCase));

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("Share your phone", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "no")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));

        using var scope = _fx.Factory.Services.CreateScope();
        var availability = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
        var stored = await availability.GetAsync(phone);
        stored.ShouldNotBeNull();
        stored!.ShareContact.ShouldBeFalse();
        stored.Services.ShouldContain("carpentry");
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
