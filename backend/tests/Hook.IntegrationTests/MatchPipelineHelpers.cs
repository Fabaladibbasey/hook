namespace Hook.IntegrationTests;

internal static class MatchPipelineHelpers
{
    public static async Task<OutboxMessage> ReachInitialPresentAsync(
        DevPipelineFixture fx,
        string phone,
        bool sharePhoneConsent = false,
        CancellationToken ct = default)
    {
        using var client = fx.Factory.CreateClient();

        await fx.InjectTextAndAwaitAsync(phone, "I need a plumber", ct: ct);
        await fx.InjectTextAndAwaitAsync(phone, "yes", ct: ct);
        await fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng, ct: ct);
        await fx.InjectTextAndAwaitAsync(phone, "kitchen sink leak", ct: ct);
        await fx.InjectTextAndAwaitAsync(phone, sharePhoneConsent ? "yes" : "no", ct: ct);

        // Bot ack while matching runs; if we skip this, the present-match assertion below
        // can race against the searching-status reply on a slow shard.
        var lookingFor = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Looking for", StringComparison.OrdinalIgnoreCase),
            ct: ct);

        return await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase),
            since: lookingFor.At,
            ct: ct);
    }
}
