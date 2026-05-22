using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-3")]
public class MatchIterationPipelineTests : PipelineTestBase
{
    public MatchIterationPipelineTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task Next_AfterPresent_AutoExpandsAndPromptsIncrease()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070002001";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);

        await _fx.InjectTextAndAwaitAsync(phone, "next");

        var prompt = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("No more in", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains("INCREASE", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        prompt.Body.ShouldContain("10km");
        prompt.Body.ShouldContain("20km");
    }

    [Fact]
    public async Task Increase_RepeatedlyToMaxRadius_RepliesHardCap()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070002002";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);
        var lastSeen = presented.At;

        for (var i = 0; i < 10; i++)
        {
            await _fx.InjectTextAndAwaitAsync(phone, "increase");
            var step = await client.ExpectOutboundAsync(
                phone,
                m => m.Body.Contains("No more in", StringComparison.OrdinalIgnoreCase) ||
                     m.Body.Contains("No providers found in 100km", StringComparison.OrdinalIgnoreCase),
                since: lastSeen);
            lastSeen = step.At;

            if (step.Body.Contains("No providers found in 100km", StringComparison.OrdinalIgnoreCase))
            {
                step.Body.ShouldContain("check back later");
                return;
            }
        }

        Assert.Fail("Hard cap message never emitted after 10 INCREASE steps.");
    }

    [Fact]
    public async Task Pick_OutOfRange_NoContactShareOrChatRoutingEmitted()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070002003";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);

        await _fx.InjectTextAndAwaitAsync(phone, "PICK 99");

        var outbox = await client.GetOutboxAsync();
        var afterPick = outbox.Where(m => m.At > presented.At && m.To == phone).ToList();
        afterPick.ShouldNotContain(m => m.Body.Contains("provider for ", StringComparison.OrdinalIgnoreCase));
        afterPick.ShouldNotContain(m => m.Body.Contains("private chat", StringComparison.OrdinalIgnoreCase));
    }
}
