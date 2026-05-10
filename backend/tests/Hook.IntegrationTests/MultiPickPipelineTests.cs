using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-2")]
public class MultiPickPipelineTests : PipelineTestBase
{
    // Seed providers (see DevProviderSeedSet.All):
    //   #1 +2203000001 — ShareContact = true
    //   #2 +2203000002 — ShareContact = false
    //   #3 +2203000003 — ShareContact = true
    private const string ShareTrueProvider1 = "+2203000001";
    private const string ShareFalseProvider2 = "+2203000002";
    private const string ShareTrueProvider3 = "+2203000003";

    public MultiPickPipelineTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task PickAll_WithConsent_PhonesRevealedForOptInProvidersChatRoutedForOthers()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155556001";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, phone, sharePhoneConsent: true);

        await _fx.InjectTextAndAwaitAsync(phone, "PICK ALL", timeout: TimeSpan.FromSeconds(20));

        // Provider 1 + Provider 3 both consented — they receive direct phone notice.
        var notice1 = await client.ExpectOutboundAsync(
            ShareTrueProvider1,
            m => m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains(phone, StringComparison.Ordinal),
            since: presented.At);
        var notice3 = await client.ExpectOutboundAsync(
            ShareTrueProvider3,
            m => m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains(phone, StringComparison.Ordinal),
            since: presented.At);

        // Provider 2 declined to share — they receive the chat link instead.
        var providerChatLink = await client.ExpectOutboundAsync(
            ShareFalseProvider2,
            m => m.Body.Contains("wants to chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

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
            _fx, phone, sharePhoneConsent: false);

        await _fx.InjectTextAndAwaitAsync(phone, "PICK ALL", timeout: TimeSpan.FromSeconds(20));

        // Provider body varies by their own consent: consent=true -> "prefers a private
        // chat"; consent=false -> "wants to chat". Assert per-provider so a regression
        // can't slip past with a generic chat-link body match.
        var p1Link = await client.ExpectOutboundAsync(
            ShareTrueProvider1,
            m => m.Body.Contains("prefers a private chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At);
        var p2Link = await client.ExpectOutboundAsync(
            ShareFalseProvider2,
            m => m.Body.Contains("wants to chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At);
        var p3Link = await client.ExpectOutboundAsync(
            ShareTrueProvider3,
            m => m.Body.Contains("prefers a private chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        p1Link.Body.ShouldNotBeNullOrEmpty();
        p2Link.Body.ShouldNotBeNullOrEmpty();
        p3Link.Body.ShouldNotBeNullOrEmpty();

        // Client receives one chat-link per match, each prefixed with its 1-based
        // position from the original presented list and the masked provider phone.
        // sharePhoneConsent=false → all three say "your private chat is ready".
        await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Match #1 (+220***01): your private chat is ready.", StringComparison.Ordinal),
            since: presented.At);
        await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Match #2 (+220***02): your private chat is ready.", StringComparison.Ordinal),
            since: presented.At);
        await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Match #3 (+220***03): your private chat is ready.", StringComparison.Ordinal),
            since: presented.At);

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
            _fx, phone, sharePhoneConsent: true);

        await _fx.InjectTextAndAwaitAsync(phone, "PICK 1", timeout: TimeSpan.FromSeconds(20));

        await client.ExpectOutboundAsync(
            ShareTrueProvider1,
            m => m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains(phone, StringComparison.Ordinal),
            since: presented.At);

        // Client-side phone-reveal notice prefixed with position from the original list.
        await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Match #1: provider for plumbing: +2203000001.", StringComparison.Ordinal),
            since: presented.At);

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

        var unpicked = await db.Matches
            .CountAsync(m => m.RequestId == request.Id && m.PickedAt == null);
        unpicked.ShouldBe(0, "every match should have been marked PickedAt after PICK ALL");
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
