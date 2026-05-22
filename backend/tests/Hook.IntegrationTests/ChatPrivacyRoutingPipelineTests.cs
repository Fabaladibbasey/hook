using System.Text.RegularExpressions;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-4")]
public class ChatPrivacyRoutingPipelineTests : PipelineTestBase
{
    private const string ShareDisabledProviderPhone = "+2203000002";

    private static readonly Regex ChatUrlRegex = new(
        @"/c/(?<chatId>[0-9a-f]{32})/(?<token>[A-Za-z0-9_-]{43})\b",
        RegexOptions.Compiled);

    public ChatPrivacyRoutingPipelineTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task Pick_ProviderWithoutConsent_EmitsChatLinksToBothSides()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070003001";

        // Default sharePhoneConsent: false — both parties hid consent in this scenario.
        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);

        await _fx.InjectTextAndAwaitAsync(phone, "PICK 2");

        var clientLink = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("private chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At);
        var providerLink = await client.ExpectOutboundAsync(
            ShareDisabledProviderPhone,
            m => m.Body.Contains("wants to chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        var clientMatch = ChatUrlRegex.Match(clientLink.Body);
        var providerMatch = ChatUrlRegex.Match(providerLink.Body);
        clientMatch.Success.ShouldBeTrue($"client message missing /c/<chatId>/<token>: {clientLink.Body}");
        providerMatch.Success.ShouldBeTrue($"provider message missing /c/<chatId>/<token>: {providerLink.Body}");

        clientLink.Body.ShouldStartWith("Match #2 (+220***02): your private chat is ready.");
        clientLink.Body.ShouldNotContain("prefers");

        var expectedMapsUrl = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "https://maps.google.com/?q={0},{1}",
            DevPipelineFixture.SeedRefLat,
            DevPipelineFixture.SeedRefLng);
        providerLink.Body.ShouldContain(expectedMapsUrl);
        providerLink.Body.ShouldNotContain("prefers");
    }

    [Fact]
    public async Task ChatLinks_TokensAreUniqueBase64Url43Chars_ChatIdIsShared()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070003002";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);
        await _fx.InjectTextAndAwaitAsync(phone, "PICK 2");

        var clientLink = await client.ExpectOutboundAsync(
            phone,
            m => ChatUrlRegex.IsMatch(m.Body),
            since: presented.At);
        var providerLink = await client.ExpectOutboundAsync(
            ShareDisabledProviderPhone,
            m => ChatUrlRegex.IsMatch(m.Body),
            since: presented.At);

        var clientMatch = ChatUrlRegex.Match(clientLink.Body);
        var providerMatch = ChatUrlRegex.Match(providerLink.Body);

        clientMatch.Groups["chatId"].Value.ShouldBe(providerMatch.Groups["chatId"].Value);
        clientMatch.Groups["token"].Value.ShouldNotBe(providerMatch.Groups["token"].Value);
        clientMatch.Groups["token"].Value.Length.ShouldBe(43);
        providerMatch.Groups["token"].Value.Length.ShouldBe(43);
    }

    [Fact]
    public async Task Pick_ProviderWithoutConsent_ProviderBodyIncludesForwardedClientDescription()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207030004";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);
        await _fx.InjectTextAndAwaitAsync(phone, "PICK 2");

        var providerLink = await client.ExpectOutboundAsync(
            ShareDisabledProviderPhone,
            m => m.Body.Contains("wants to chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        providerLink.Body.ShouldContain("— client message (forwarded, not verified) —");
        providerLink.Body.ShouldContain("kitchen sink leak");
        providerLink.Body.ShouldContain("— end client message —");
    }

    [Fact]
    public async Task ChatLinks_PersistChatSessionAndTwoParticipants()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070003003";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);
        await _fx.InjectTextAndAwaitAsync(phone, "PICK 2");

        var clientLink = await client.ExpectOutboundAsync(
            phone,
            m => ChatUrlRegex.IsMatch(m.Body),
            since: presented.At);

        var chatIdHex = ChatUrlRegex.Match(clientLink.Body).Groups["chatId"].Value;
        var chatId = Guid.ParseExact(chatIdHex, "N");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        var session = await db.ChatSessions.SingleOrDefaultAsync(s => s.Id == chatId);
        session.ShouldNotBeNull();

        var participants = await db.ChatParticipants
            .Where(p => p.ChatId == chatId)
            .ToListAsync();
        participants.Count.ShouldBe(2);
        participants.Select(p => p.Token).Distinct().Count().ShouldBe(2);
        participants.Select(p => p.Role).ShouldBe(
            [ChatParticipantRole.Client, ChatParticipantRole.Provider],
            ignoreOrder: true);
        participants.Single(p => p.Role == ChatParticipantRole.Client).Phone.ShouldBe(phone);
        participants.Single(p => p.Role == ChatParticipantRole.Provider).Phone.ShouldBe(ShareDisabledProviderPhone);
    }
}
