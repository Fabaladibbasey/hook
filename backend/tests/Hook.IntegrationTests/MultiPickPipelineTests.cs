using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

public class MultiPickPipelineTests : IClassFixture<DevPipelineFixture>
{
    // Seed providers (see DevProviderSeedSet.All):
    //   #1 +2203000001 — ShareContact = true
    //   #2 +2203000002 — ShareContact = false
    //   #3 +2203000003 — ShareContact = true
    private const string ShareTrueProvider1 = "+2203000001";
    private const string ShareFalseProvider2 = "+2203000002";
    private const string ShareTrueProvider3 = "+2203000003";

    private readonly DevPipelineFixture _fx;

    public MultiPickPipelineTests(DevPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task PickAll_WithConsent_PhonesRevealedForOptInProvidersChatRoutedForOthers()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155556001";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            client, phone, sharePhoneConsent: true);

        (await client.InjectTextAsync(phone, "PICK ALL")).EnsureSuccessStatusCode();

        // Provider 1 + Provider 3 both consented — they receive direct phone notice.
        var notice1 = await client.WaitForOutboundAsync(
            ShareTrueProvider1,
            m => m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains(phone, StringComparison.Ordinal),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));
        var notice3 = await client.WaitForOutboundAsync(
            ShareTrueProvider3,
            m => m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains(phone, StringComparison.Ordinal),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));

        // Provider 2 declined to share — they receive the chat link instead.
        var providerChatLink = await client.WaitForOutboundAsync(
            ShareFalseProvider2,
            m => m.Body.Contains("wants to chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));

        notice1.Body.ShouldContain("plumbing");
        notice3.Body.ShouldContain("plumbing");
        providerChatLink.Body.ShouldNotBeNullOrEmpty();

        await AssertAllMatchesPickedAsync(phone);
    }

    [Fact]
    public async Task PickAll_WithoutConsent_AllRouteToChat_NoPhoneRevealAnywhere()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155556002";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            client, phone, sharePhoneConsent: false);

        (await client.InjectTextAsync(phone, "PICK ALL")).EnsureSuccessStatusCode();

        // Each picked provider should receive a chat link (the no-consent path).
        var p1Link = await client.WaitForOutboundAsync(
            ShareTrueProvider1,
            m => m.Body.Contains("wants to chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));
        var p2Link = await client.WaitForOutboundAsync(
            ShareFalseProvider2,
            m => m.Body.Contains("wants to chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));
        var p3Link = await client.WaitForOutboundAsync(
            ShareTrueProvider3,
            m => m.Body.Contains("wants to chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));

        p1Link.Body.ShouldNotBeNullOrEmpty();
        p2Link.Body.ShouldNotBeNullOrEmpty();
        p3Link.Body.ShouldNotBeNullOrEmpty();

        // No raw phone-reveal notices should have been sent — those are
        // bilateral-consent only.
        var outbox = await client.GetOutboxAsync();
        var phoneRevealNotices = outbox
            .Where(m => m.At >= presented.At
                        && (m.To == ShareTrueProvider1 || m.To == ShareTrueProvider3)
                        && m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase)
                        && m.Body.Contains(phone, StringComparison.Ordinal))
            .ToList();
        phoneRevealNotices.ShouldBeEmpty();

        await AssertAllMatchesPickedAsync(phone);
    }

    [Fact]
    public async Task PickSubset_UnpickedProvidersReceiveZeroMessages()
    {
        // Privacy invariant: providers the requester does not pick must remain
        // unaware of the request. No proactive broadcast at present time, no
        // notification at pick time — nothing.
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155556003";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            client, phone, sharePhoneConsent: true);

        (await client.InjectTextAsync(phone, "PICK 1")).EnsureSuccessStatusCode();

        // Let the bus settle.
        await client.WaitForOutboundAsync(
            ShareTrueProvider1,
            m => m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains(phone, StringComparison.Ordinal),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));

        await Task.Delay(TimeSpan.FromSeconds(1));

        var outbox = await client.GetOutboxAsync();
        var unpickedSpam = outbox
            .Where(m => m.At >= presented.At &&
                        (m.To == ShareFalseProvider2 || m.To == ShareTrueProvider3) &&
                        m.Body.Contains(phone, StringComparison.Ordinal))
            .ToList();
        unpickedSpam.ShouldBeEmpty();

        await AssertOnlyMatchPickedAsync(phone, ShareTrueProvider1);
    }

    private async Task AssertAllMatchesPickedAsync(string clientPhone)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var request = await db.ServiceRequests
            .Where(r => r.ClientPhone == clientPhone)
            .OrderByDescending(r => r.CreatedAt)
            .FirstAsync();

        // Wait briefly for PickedAt writes to land.
        for (var i = 0; i < 20; i++)
        {
            var unpicked = await db.Matches
                .CountAsync(m => m.RequestId == request.Id && m.PickedAt == null);
            if (unpicked == 0) return;
            await Task.Delay(200);
            db.ChangeTracker.Clear();
        }

        var stillUnpicked = await db.Matches
            .CountAsync(m => m.RequestId == request.Id && m.PickedAt == null);
        stillUnpicked.ShouldBe(0, "every match should have been marked PickedAt after PICK ALL");
    }

    private async Task AssertOnlyMatchPickedAsync(string clientPhone, string pickedProviderPhone)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var request = await db.ServiceRequests
            .Where(r => r.ClientPhone == clientPhone)
            .OrderByDescending(r => r.CreatedAt)
            .FirstAsync();

        var matches = await db.Matches
            .Where(m => m.RequestId == request.Id)
            .OrderBy(m => m.Score)
            .ToListAsync();
        matches.Single(m => m.ProviderPhone == pickedProviderPhone).PickedAt.ShouldNotBeNull();
        matches
            .Where(m => m.ProviderPhone != pickedProviderPhone)
            .ToList()
            .ForEach(m => m.PickedAt.ShouldBeNull());
    }
}
