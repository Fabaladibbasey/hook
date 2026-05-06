using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

// Verifies dual-role flows: a registered provider may submit a service request
// (for a different service) and a client may register as a provider (for a
// different service than any open request). Same-service overlap is rejected at
// finalization with an explicit "what to do next" message — never silently.
public class DualRoleTests : IClassFixture<DevPipelineFixture>
{
    private readonly DevPipelineFixture _fx;

    public DualRoleTests(DevPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task RegisteredPlumber_RequestingCarpentry_Succeeds_AndIsExcludedFromOwnMatches()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207020001";

        var afterReg = await CompleteProviderRegistrationAsync(client, phone, "I offer plumbing");

        // Now act as a client requesting a different service (carpentry).
        (await client.InjectTextAsync(phone, "I need a carpenter")).EnsureSuccessStatusCode();

        var confirm = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Do you need", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains("carpentry"),
            since: afterReg,
            timeout: StepTimeout);
        confirm.Body.ShouldNotContain("I detected", Case.Insensitive);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var locPrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"), since: confirm.At, timeout: StepTimeout);

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        var descPrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase), since: locPrompt.At, timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "skip")).EnsureSuccessStatusCode();
        var consentPrompt = await client.WaitForOutboundAsync(phone,
            m => m.Body.Contains("share your phone number", StringComparison.OrdinalIgnoreCase),
            since: descPrompt.At, timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "no")).EnsureSuccessStatusCode();

        var lookingFor = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Looking for", StringComparison.OrdinalIgnoreCase),
            since: consentPrompt.At,
            timeout: StepTimeout);

        // Wait for matching to either present results or report none — proves the
        // matching pipeline ran and self-exclusion was applied.
        await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("No providers", StringComparison.OrdinalIgnoreCase),
            since: lookingFor.At,
            timeout: StepTimeout);

        // Request should exist, ProviderAvailability for same phone untouched, and
        // matching results never include the requester's own phone.
        using var scope = _fx.Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var req = await ctx.ServiceRequests
            .Where(r => r.ClientPhone == phone)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync();
        req.ShouldNotBeNull();
        req!.ServiceSlug.ShouldBe("carpentry");
        req.ShownProviderPhones.ShouldNotContain(phone);

        var availability = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
        var stored = await availability.GetAsync(phone);
        stored.ShouldNotBeNull();
        stored!.Services.ShouldContain("plumbing");
    }

    [Fact]
    public async Task RegisteredPlumber_RequestingPlumbing_IsRejectedAtFinalization()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207020002";

        var afterReg = await CompleteProviderRegistrationAsync(client, phone, "I offer plumbing");

        // Try to request the same service we offer.
        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        var confirm = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase), since: afterReg, timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var locPrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"), since: confirm.At, timeout: StepTimeout);

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        var descPrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase), since: locPrompt.At, timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "skip")).EnsureSuccessStatusCode();

        // Expect the explicit reject message, NOT "Looking for nearby providers".
        var reject = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("listed as a plumbing provider", StringComparison.OrdinalIgnoreCase),
            since: descPrompt.At,
            timeout: StepTimeout);
        reject.Body.ShouldContain("LEAVE", Case.Insensitive);

        using var scope = _fx.Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var anyPlumbingRequest = await ctx.ServiceRequests
            .AnyAsync(r => r.ClientPhone == phone && r.ServiceSlug == "plumbing");
        anyPlumbingRequest.ShouldBeFalse();
    }

    [Fact]
    public async Task Client_WithOpenPlumbingRequest_CannotRegisterAsPlumber()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207020003";

        // Submit and finalize an open plumbing request.
        var afterReq = await CompleteClientRequestAsync(client, phone, "I need a plumber");

        // Now try to register as a plumber — same service as the open request.
        (await client.InjectTextAsync(phone, "I offer plumbing")).EnsureSuccessStatusCode();
        var detected = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase),
            since: afterReq,
            timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var locPrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"), since: detected.At, timeout: StepTimeout);

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        var sharePrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("Share your phone", StringComparison.OrdinalIgnoreCase), since: locPrompt.At, timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();

        var reject = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("open request for plumbing", StringComparison.OrdinalIgnoreCase),
            since: sharePrompt.At,
            timeout: StepTimeout);
        reject.Body.ShouldContain("LEAVE", Case.Insensitive);

        using var scope = _fx.Factory.Services.CreateScope();
        var availability = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
        (await availability.GetAsync(phone)).ShouldBeNull();
    }

    [Fact]
    public async Task Client_AfterClosingRequest_CanRegisterAsProvider_DifferentService()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207020004";

        var afterReq = await CompleteClientRequestAsync(client, phone, "I need a carpenter");

        // Close the request directly so the registration finalization sees no open same-service request.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var req = await ctx.ServiceRequests
                .Where(r => r.ClientPhone == phone)
                .OrderByDescending(r => r.CreatedAt)
                .FirstAsync();
            req.Close();
            await ctx.SaveChangesAsync();
        }

        // Register for a different service (plumbing).
        (await client.InjectTextAsync(phone, "I offer plumbing")).EnsureSuccessStatusCode();
        var detected = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase),
            since: afterReq,
            timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var locPrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"), since: detected.At, timeout: StepTimeout);

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        var sharePrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("Share your phone", StringComparison.OrdinalIgnoreCase), since: locPrompt.At, timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase), since: sharePrompt.At, timeout: StepTimeout);

        using var verifyScope = _fx.Factory.Services.CreateScope();
        var availability = verifyScope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
        var stored = await availability.GetAsync(phone);
        stored.ShouldNotBeNull();
        stored!.Services.ShouldContain("plumbing");
    }

    [Fact]
    public async Task ListedProvider_SendingProblemStatement_RoutesToClientFlow_NotHeartbeat()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207020005";

        var afterReg = await CompleteProviderRegistrationAsync(client, phone, "I offer plumbing");

        (await client.InjectTextAsync(phone, "I need a carpenter")).EnsureSuccessStatusCode();

        // Listed provider sending a service-request text must hit the client orchestrator,
        // not silently heartbeat through the registration orchestrator.
        var reply = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Do you need", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains("carpentry"),
            since: afterReg,
            timeout: StepTimeout);

        reply.Body.ShouldNotContain("You are listed", Case.Insensitive);
        reply.Body.ShouldNotContain("I detected", Case.Insensitive);
    }

    // Generous timeouts: when this class runs all five tests in sequence the shared
    // Wolverine bus and Postgres container have backlog from prior tests, so the
    // 10s default occasionally trips even though each step is well under a second
    // in isolation.
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    // Returns the timestamp of the final outbound, so callers can pass it as `since:`
    // to subsequent waits and avoid matching stale messages from earlier in the flow.
    private static async Task<DateTimeOffset> CompleteProviderRegistrationAsync(HttpClient client, string phone, string firstMessage)
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        (await client.InjectTextAsync(phone, firstMessage)).EnsureSuccessStatusCode();
        var detected = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase),
            since: start,
            timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var locPrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"), since: detected.At, timeout: StepTimeout);

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        var sharePrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("Share your phone", StringComparison.OrdinalIgnoreCase), since: locPrompt.At, timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var listed = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase), since: sharePrompt.At, timeout: StepTimeout);
        return listed.At;
    }

    private static async Task<DateTimeOffset> CompleteClientRequestAsync(HttpClient client, string phone, string firstMessage)
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        (await client.InjectTextAsync(phone, firstMessage)).EnsureSuccessStatusCode();
        var confirm = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase), since: start, timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var locPrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"), since: confirm.At, timeout: StepTimeout);

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        var descPrompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase), since: locPrompt.At, timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "skip")).EnsureSuccessStatusCode();
        var consentPrompt = await client.WaitForOutboundAsync(phone,
            m => m.Body.Contains("share your phone number", StringComparison.OrdinalIgnoreCase),
            since: descPrompt.At, timeout: StepTimeout);

        (await client.InjectTextAsync(phone, "no")).EnsureSuccessStatusCode();
        var looking = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Looking for", StringComparison.OrdinalIgnoreCase),
            since: consentPrompt.At,
            timeout: StepTimeout);
        return looking.At;
    }
}
