using System.Diagnostics;
using Shouldly;

namespace Hook.IntegrationTests.Geocoding;

[Collection("Pipeline-1")]
public class DeferredGeocodeFlowTests : PipelineTestBase
{
    public DeferredGeocodeFlowTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task TextAddress_ClientFlow_DefersGeocodeOffCriticalPath()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000401";

        // Advance the client draft to AwaitingLocation: service + confirm.
        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        // Raw inject (no Wolverine cascade wait): measure the 200-ACK latency.
        // After the deferral, the orchestrator publishes a GeocodeAddressRequested
        // envelope and returns immediately; the geocoding HTTP runs in the dispatch
        // handler off the critical path.
        var sw = Stopwatch.StartNew();
        var resp = await client.InjectTextAsync(phone, "Bakau Newtown");
        sw.Stop();
        resp.EnsureSuccessStatusCode();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1500),
            $"Inbound 200-ACK took {sw.ElapsedMilliseconds}ms — geocode may still be inline.");

        // The "looking up" interstitial fires before the envelope is dispatched.
        var lookingUp = await client.WaitForOutboundAsync(phone,
            m => m.Body.Contains("Looking up that address", StringComparison.OrdinalIgnoreCase));
        lookingUp.ShouldNotBeNull();

        // The dispatch handler eventually delivers the Found prompt via the outbox.
        var found = await client.WaitForOutboundAsync(phone,
            m => m.Body.StartsWith("Found:", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(10));
        found.Body.ShouldContain("Reply YES to confirm");
        found.Body.ShouldContain("Bakau Newtown");
    }
}
