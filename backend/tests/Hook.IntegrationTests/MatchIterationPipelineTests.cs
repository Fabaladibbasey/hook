using Shouldly;

namespace Hook.IntegrationTests;

public class MatchIterationPipelineTests : IClassFixture<DevPipelineFixture>
{
    private readonly DevPipelineFixture _fx;

    public MatchIterationPipelineTests(DevPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Next_AfterPresent_AutoExpandsAndPromptsIncrease()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155552001";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);

        (await client.InjectTextAsync(phone, "next")).EnsureSuccessStatusCode();

        var prompt = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("No more in", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains("INCREASE", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(15));

        prompt.Body.ShouldContain("10km");
        prompt.Body.ShouldContain("20km");
    }

    [Fact]
    public async Task Increase_RepeatedlyToMaxRadius_RepliesHardCap()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155552002";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);
        var lastSeen = presented.At;

        for (var i = 0; i < 10; i++)
        {
            (await client.InjectTextAsync(phone, "increase")).EnsureSuccessStatusCode();
            var step = await client.WaitForOutboundAsync(
                phone,
                m => m.Body.Contains("No more in", StringComparison.OrdinalIgnoreCase) ||
                     m.Body.Contains("No providers found in 100km", StringComparison.OrdinalIgnoreCase),
                since: lastSeen,
                timeout: TimeSpan.FromSeconds(15));
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
        var phone = "+14155552003";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);

        (await client.InjectTextAsync(phone, "PICK 99")).EnsureSuccessStatusCode();

        await Task.Delay(800);

        var outbox = await client.GetOutboxAsync();
        var afterPick = outbox.Where(m => m.At > presented.At && m.To == phone).ToList();
        afterPick.ShouldNotContain(m => m.Body.StartsWith("Provider for ", StringComparison.OrdinalIgnoreCase));
        afterPick.ShouldNotContain(m => m.Body.Contains("private chat", StringComparison.OrdinalIgnoreCase));
    }
}
