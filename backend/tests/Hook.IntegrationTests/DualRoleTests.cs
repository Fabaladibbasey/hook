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
[Collection("Pipeline-2")]
public class DualRoleTests : PipelineTestBase
{
    public DualRoleTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task RegisteredPlumber_RequestingCarpentry_Succeeds_AndIsExcludedFromOwnMatches()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207020001";

        var afterReg = await CompleteProviderRegistrationAsync(_fx, client, phone, "I offer plumbing");

        // Now act as a client requesting a different service (carpentry).
        await _fx.InjectTextAndAwaitAsync(phone, "I need a carpenter");
        var confirm = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Do you need", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains("carpentry"),
            since: afterReg);
        confirm.Body.ShouldNotContain("I detected", Case.Insensitive);

        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "skip");
        await _fx.InjectTextAndAwaitAsync(phone, "no");

        // Wait for matching to either present results or report none — proves the
        // matching pipeline ran and self-exclusion was applied.
        await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("No providers", StringComparison.OrdinalIgnoreCase),
            since: confirm.At);

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

        var afterReg = await CompleteProviderRegistrationAsync(_fx, client, phone, "I offer plumbing");

        // Try to request the same service we offer.
        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "skip");

        // Expect the explicit reject message, NOT "Looking for nearby providers".
        var reject = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("already listed to provide", StringComparison.OrdinalIgnoreCase)
                 && m.Body.Contains("plumbing", StringComparison.OrdinalIgnoreCase),
            since: afterReg);
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
        var afterReq = await CompleteClientRequestAsync(_fx, client, phone, "I need a plumber");

        // Now try to register as a plumber — same service as the open request.
        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var reject = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("open request for plumbing", StringComparison.OrdinalIgnoreCase),
            since: afterReq);
        reject.Body.ShouldContain("CANCEL", Case.Insensitive);

        using var scope = _fx.Factory.Services.CreateScope();
        var availability = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
        (await availability.GetAsync(phone)).ShouldBeNull();
    }

    [Fact]
    public async Task Client_AfterClosingRequest_CanRegisterAsProvider_DifferentService()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207020004";

        var afterReq = await CompleteClientRequestAsync(_fx, client, phone, "I need a carpenter");

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
        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase),
            since: afterReq);

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

        var afterReg = await CompleteProviderRegistrationAsync(_fx, client, phone, "I offer plumbing");

        await _fx.InjectTextAndAwaitAsync(phone, "I need a carpenter");

        // Listed provider sending a service-request text must hit the client orchestrator,
        // not silently heartbeat through the registration orchestrator.
        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Do you need", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains("carpentry"),
            since: afterReg);

        reply.Body.ShouldNotContain("You are listed", Case.Insensitive);
        reply.Body.ShouldNotContain("I detected", Case.Insensitive);
    }

    private static async Task<DateTimeOffset> CompleteProviderRegistrationAsync(
        DevPipelineFixture fx, HttpClient client, string phone, string firstMessage)
    {
        await fx.InjectTextAndAwaitAsync(phone, firstMessage);
        await fx.InjectTextAndAwaitAsync(phone, "yes");
        await fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await fx.InjectTextAndAwaitAsync(phone, "yes");
        var listed = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));
        return listed.At;
    }

    private static async Task<DateTimeOffset> CompleteClientRequestAsync(
        DevPipelineFixture fx, HttpClient client, string phone, string firstMessage)
    {
        await fx.InjectTextAndAwaitAsync(phone, firstMessage);
        await fx.InjectTextAndAwaitAsync(phone, "yes");
        await fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await fx.InjectTextAndAwaitAsync(phone, "skip");
        await fx.InjectTextAndAwaitAsync(phone, "no");
        var looking = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("Looking for", StringComparison.OrdinalIgnoreCase));
        return looking.At;
    }
}
