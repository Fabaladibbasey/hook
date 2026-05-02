using System.Net;
using System.Text.Json;
using Shouldly;

namespace Hook.IntegrationTests;

public class WhatsappInboundDedupPipelineTests : IClassFixture<DevPipelineFixture>
{
    private readonly DevPipelineFixture _fx;

    public WhatsappInboundDedupPipelineTests(DevPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task SameMessageId_TwiceInbound_SecondReturns409()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155554001";
        var messageId = $"wamid.dev.test.dedup.{Guid.NewGuid():N}";

        var first = await client.InjectTextAsync(phone, "I need a plumber", messageId);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await client.InjectTextAsync(phone, "I need a plumber", messageId);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var body = await second.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("duplicate");
    }

    [Fact]
    public async Task SameMessageId_TwiceInbound_OnlyOneOutboundReply()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155554002";
        var messageId = $"wamid.dev.test.dedup.{Guid.NewGuid():N}";

        (await client.InjectTextAsync(phone, "I need a plumber", messageId))
            .EnsureSuccessStatusCode();

        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        var dup = await client.InjectTextAsync(phone, "I need a plumber", messageId);
        dup.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await Task.Delay(800);

        var outbox = await client.GetOutboxAsync();
        var replies = outbox.Where(m => m.To == phone && m.Body.Contains("YES or NO")).ToList();
        replies.Count.ShouldBe(1);
    }
}
