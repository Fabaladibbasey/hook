using System.Text.RegularExpressions;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

public class ChatPrivacyRoutingPipelineTests : IClassFixture<DevPipelineFixture>
{
    private const string ShareDisabledProviderPhone = "+2203000002";

    private static readonly Regex ChatUrlRegex = new(
        @"/c/(?<chatId>[0-9a-f]{32})/(?<token>[A-Za-z0-9_-]{43})\b",
        RegexOptions.Compiled);

    private readonly DevPipelineFixture _fx;

    public ChatPrivacyRoutingPipelineTests(DevPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Pick_ProviderWithoutConsent_EmitsChatLinksToBothSides()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155553001";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);

        (await client.InjectTextAsync(phone, "PICK 2")).EnsureSuccessStatusCode();

        var clientLink = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("private chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));
        var providerLink = await client.WaitForOutboundAsync(
            ShareDisabledProviderPhone,
            m => m.Body.Contains("wants to chat", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));

        var clientMatch = ChatUrlRegex.Match(clientLink.Body);
        var providerMatch = ChatUrlRegex.Match(providerLink.Body);
        clientMatch.Success.ShouldBeTrue($"client message missing /c/<chatId>/<token>: {clientLink.Body}");
        providerMatch.Success.ShouldBeTrue($"provider message missing /c/<chatId>/<token>: {providerLink.Body}");
    }

    [Fact]
    public async Task ChatLinks_TokensAreUniqueBase64Url43Chars_ChatIdIsShared()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155553002";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);
        (await client.InjectTextAsync(phone, "PICK 2")).EnsureSuccessStatusCode();

        var clientLink = await client.WaitForOutboundAsync(
            phone,
            m => ChatUrlRegex.IsMatch(m.Body),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));
        var providerLink = await client.WaitForOutboundAsync(
            ShareDisabledProviderPhone,
            m => ChatUrlRegex.IsMatch(m.Body),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));

        var clientMatch = ChatUrlRegex.Match(clientLink.Body);
        var providerMatch = ChatUrlRegex.Match(providerLink.Body);

        clientMatch.Groups["chatId"].Value.ShouldBe(providerMatch.Groups["chatId"].Value);
        clientMatch.Groups["token"].Value.ShouldNotBe(providerMatch.Groups["token"].Value);
        clientMatch.Groups["token"].Value.Length.ShouldBe(43);
        providerMatch.Groups["token"].Value.Length.ShouldBe(43);
    }

    [Fact]
    public async Task ChatLinks_PersistChatSessionAndTwoParticipants()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155553003";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);
        (await client.InjectTextAsync(phone, "PICK 2")).EnsureSuccessStatusCode();

        var clientLink = await client.WaitForOutboundAsync(
            phone,
            m => ChatUrlRegex.IsMatch(m.Body),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));

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
            new[] { ChatParticipantRole.Client, ChatParticipantRole.Provider },
            ignoreOrder: true);
        participants.Single(p => p.Role == ChatParticipantRole.Client).Phone.ShouldBe(phone);
        participants.Single(p => p.Role == ChatParticipantRole.Provider).Phone.ShouldBe(ShareDisabledProviderPhone);
    }
}
