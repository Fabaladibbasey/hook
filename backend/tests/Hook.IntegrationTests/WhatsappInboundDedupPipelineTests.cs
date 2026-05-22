using System.Net;
using System.Text.Json;
using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-2")]
public class WhatsappInboundDedupPipelineTests : PipelineTestBase
{
    public WhatsappInboundDedupPipelineTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task SameMessageId_TwiceInbound_SecondReturns409()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070004001";
        var messageId = $"wamid.dev.test.dedup.{Guid.NewGuid():N}";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber", messageId);

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
        var phone = "+22070004002";
        var messageId = $"wamid.dev.test.dedup.{Guid.NewGuid():N}";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber", messageId);
        await client.ExpectOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        var dup = await client.InjectTextAsync(phone, "I need a plumber", messageId);
        dup.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var outbox = await client.GetOutboxAsync();
        var replies = outbox.Where(m => m.To == phone && m.Body.Contains("YES or NO")).ToList();
        replies.Count.ShouldBe(1);
    }
}
