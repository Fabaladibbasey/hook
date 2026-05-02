using Shouldly;

namespace Hook.IntegrationTests;

public class ClientRequestPipelineTests : IClassFixture<DevPipelineFixture>
{
    private readonly DevPipelineFixture _fx;

    public ClientRequestPipelineTests(DevPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Greeting_GetsGreetingBack_NotServicePitch()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551001";

        (await client.InjectTextAsync(phone, "hi")).EnsureSuccessStatusCode();

        var reply = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("greeting-reply", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        reply.Body.ShouldNotBeNullOrEmpty();
        reply.Body.ShouldNotContain("YES or NO", Case.Insensitive);
    }

    [Fact]
    public async Task OutOfScope_GetsRefusal_NotOpenChat()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551009";

        (await client.InjectTextAsync(phone, "what's the weather today")).EnsureSuccessStatusCode();

        var reply = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("out-of-scope", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        reply.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ServiceRequest_AsksToConfirmService()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551002";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();

        var reply = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));
        reply.Body.ShouldContain("plumbing");
    }

    [Fact]
    public async Task ServiceRequest_FullHappyPath_GpsLocation_ReachesMatching()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551003";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"));

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "kitchen sink leak")).EnsureSuccessStatusCode();

        var lookingFor = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Looking for", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(15));

        var presented = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase),
            since: lookingFor.At,
            timeout: TimeSpan.FromSeconds(15));

        presented.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ServiceRequest_PickProvider_SharesContactBothSides()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551006";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"));

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "kitchen sink leak")).EnsureSuccessStatusCode();

        var presented = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(15));

        const string topProviderPhone = "+2203000001";
        (await client.InjectTextAsync(phone, "PICK 1")).EnsureSuccessStatusCode();

        var clientShare = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("Provider for ", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));

        clientShare.Body.ShouldContain("plumbing");
        clientShare.Body.ShouldContain(topProviderPhone);

        var outbox = await client.GetOutboxAsync();
        var providerNotice = outbox.FirstOrDefault(m =>
            m.To == topProviderPhone &&
            m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase));
        providerNotice.ShouldNotBeNull();
        providerNotice!.Body.ShouldContain(phone);
    }

    [Fact]
    public async Task ServiceRequest_AddressText_GetsGeocodedAndConfirmed()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551004";

        (await client.InjectTextAsync(phone, "I need a carpenter")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"));

        (await client.InjectTextAsync(phone, "1 Market St San Francisco")).EnsureSuccessStatusCode();
        var found = await client.WaitForOutboundAsync(phone, m => m.Body.StartsWith("Found:"));
        found.Body.ShouldContain("Market St");
    }

    [Fact]
    public async Task FanOut_NotifiesAllShareTrueProviders_AtPresentTime_NotJustOnPick()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551007";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);

        var first = await client.WaitForOutboundAsync(
            "+2203000001",
            m => m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(10));
        var third = await client.WaitForOutboundAsync(
            "+2203000003",
            m => m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(10));

        first.Body.ShouldContain("plumbing");
        first.Body.ShouldContain(phone);
        third.Body.ShouldContain("plumbing");
        third.Body.ShouldContain(phone);

        var outbox = await client.GetOutboxAsync();
        outbox.Where(m => m.To == "+2203000002" && m.Body.StartsWith("Client wants ")).ShouldBeEmpty();
    }

    [Fact]
    public async Task QuickIntent_LiteralNo_TakesNoAsAnswer_NoLoop()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551008";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        var prompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "no")).EnsureSuccessStatusCode();
        var afterNo = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("What service do you need", StringComparison.OrdinalIgnoreCase),
            since: prompt.At,
            timeout: TimeSpan.FromSeconds(5));

        afterNo.Body.ShouldNotContain("Reply YES or NO", Case.Insensitive);
    }

    [Fact]
    public async Task SecondMessageMidFunnel_ReprompstYesNo_NotRestartFunnel()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551005";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        var first = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();

        var reprompt = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Reply YES or NO", StringComparison.OrdinalIgnoreCase),
            since: first.At);
        reprompt.Body.ShouldNotContain("Do you need", Case.Insensitive);
    }
}
