namespace Hook.IntegrationTests;

internal static class MatchPipelineHelpers
{
    public static async Task<OutboxMessage> ReachInitialPresentAsync(
        HttpClient client,
        string phone,
        bool sharePhoneConsent = false,
        CancellationToken ct = default)
    {
        (await client.InjectTextAsync(phone, "I need a plumber", ct)).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"), ct: ct);

        (await client.InjectTextAsync(phone, "yes", ct)).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"), ct: ct);

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng, ct))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase),
            ct: ct);

        (await client.InjectTextAsync(phone, "kitchen sink leak", ct)).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("share your phone number", StringComparison.OrdinalIgnoreCase),
            ct: ct);

        (await client.InjectTextAsync(phone, sharePhoneConsent ? "yes" : "no", ct))
            .EnsureSuccessStatusCode();

        var lookingFor = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Looking for", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(15),
            ct: ct);

        return await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase),
            since: lookingFor.At,
            timeout: TimeSpan.FromSeconds(15),
            ct: ct);
    }
}
