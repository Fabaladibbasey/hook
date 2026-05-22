using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

// Verifies the "always confirm when not sure" guardrail: if the LLM returns
// ServiceRequest or ProviderRegistration with sub-threshold confidence, the
// router asks the user to disambiguate (REQUEST = client, REGISTER = provider)
// instead of silently committing them to the wrong orchestrator. Numeric 1/2
// and the legacy HIRE/OFFER tokens stay accepted for back-compat.
[Collection("Pipeline-4")]
public class AmbiguousIntentTests : PipelineTestBase
{
    public AmbiguousIntentTests(DevPipelineFixture fx) : base(fx) { }

    private FakeConversationAi GetFakeAi()
    {
        var ai = _fx.Factory.Services.GetRequiredService<IConversationAi>();
        var fake = ai as FakeConversationAi;
        fake.ShouldNotBeNull("Test fixture must register FakeConversationAi.");
        return fake!;
    }

    [Fact]
    public async Task LowConfidenceIntent_SendsDisambiguationPrompt()
    {
        const string text = "ambig-prompt-only";
        const string phone = "+22070003001";
        var ai = GetFakeAi();
        ai.OverrideIntent(text,
            new IntentDetectionResult(IntentKind.ServiceRequest, 0.4, "en", "test-low-conf"));
        try
        {
            using var client = _fx.Factory.CreateClient();
            await _fx.InjectTextAndAwaitAsync(phone, text);

            var disambig = await client.ExpectOutboundAsync(
                phone,
                m => m.Body.Contains("REQUEST or REGISTER", StringComparison.OrdinalIgnoreCase));

            disambig.Body.ShouldContain("REQUEST", Case.Sensitive);
            disambig.Body.ShouldContain("REGISTER", Case.Sensitive);
        }
        finally
        {
            ai.ResetOverrides();
        }
    }

    [Fact]
    public async Task DisambiguateWith1_ReplaysIntoClientRequestOrchestrator()
    {
        const string text = "ambig-route-1";
        const string phone = "+22070003002";
        var ai = GetFakeAi();
        // Original message gets a fake low-confidence ServiceRequest classification.
        ai.OverrideIntent(text,
            new IntentDetectionResult(IntentKind.ServiceRequest, 0.3, "en", "test-low-conf"));
        try
        {
            using var client = _fx.Factory.CreateClient();
            await _fx.InjectTextAndAwaitAsync(phone, text);
            var disambig = await client.ExpectOutboundAsync(
                phone,
                m => m.Body.Contains("REQUEST or REGISTER", StringComparison.OrdinalIgnoreCase));

            await _fx.InjectTextAndAwaitAsync(phone, "REQUEST");

            // After REQUEST, the router replays the original text into the ClientRequest
            // orchestrator. The fake AI doesn't extract any service from the dummy
            // text so we expect the "What service do you need?" reply.
            var clientReply = await client.ExpectOutboundAsync(
                phone,
                m => m.Body.Contains("What service do you need", StringComparison.OrdinalIgnoreCase),
                since: disambig.At);

            clientReply.Body.ShouldNotContain("I detected", Case.Insensitive);
        }
        finally
        {
            ai.ResetOverrides();
        }
    }

    [Fact]
    public async Task DisambiguateWith2_ReplaysIntoRegistrationOrchestrator()
    {
        const string text = "ambig-route-2";
        const string phone = "+22070003003";
        var ai = GetFakeAi();
        ai.OverrideIntent(text,
            new IntentDetectionResult(IntentKind.ProviderRegistration, 0.3, "en", "test-low-conf"));
        try
        {
            using var client = _fx.Factory.CreateClient();
            await _fx.InjectTextAndAwaitAsync(phone, text);
            var disambig = await client.ExpectOutboundAsync(
                phone,
                m => m.Body.Contains("REQUEST or REGISTER", StringComparison.OrdinalIgnoreCase));

            await _fx.InjectTextAndAwaitAsync(phone, "REGISTER");

            // After REGISTER, the router replays the original text into the Registration
            // orchestrator. The fake AI extracts no services from the dummy text so
            // we expect the "Tell me what services you offer" reply.
            var providerReply = await client.ExpectOutboundAsync(
                phone,
                m => m.Body.Contains("services you offer", StringComparison.OrdinalIgnoreCase),
                since: disambig.At);

            providerReply.Body.ShouldNotContain("Do you need", Case.Insensitive);
        }
        finally
        {
            ai.ResetOverrides();
        }
    }

    [Fact]
    public async Task UnrecognisedDisambigReply_RePromptsAndKeepsDraft()
    {
        const string text = "ambig-bad-reply";
        const string phone = "+22070003004";
        var ai = GetFakeAi();
        ai.OverrideIntent(text,
            new IntentDetectionResult(IntentKind.ServiceRequest, 0.4, "en", "test-low-conf"));
        try
        {
            using var client = _fx.Factory.CreateClient();
            await _fx.InjectTextAndAwaitAsync(phone, text);
            var disambig = await client.ExpectOutboundAsync(
                phone,
                m => m.Body.Contains("REQUEST or REGISTER", StringComparison.OrdinalIgnoreCase));

            await _fx.InjectTextAndAwaitAsync(phone, "maybe");

            var reprompt = await client.ExpectOutboundAsync(
                phone,
                m => m.Body.Contains("Reply REQUEST", StringComparison.OrdinalIgnoreCase),
                since: disambig.At);

            reprompt.Body.ShouldContain("REQUEST", Case.Sensitive);
            reprompt.Body.ShouldContain("REGISTER", Case.Sensitive);
        }
        finally
        {
            ai.ResetOverrides();
        }
    }
}
