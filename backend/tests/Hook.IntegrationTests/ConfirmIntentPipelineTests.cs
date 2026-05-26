using Hook.Features.ServiceRequest.Create.ConfirmIntent;
using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-1")]
public class ConfirmIntentPipelineTests : PipelineTestBase
{
    public ConfirmIntentPipelineTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task LlmYes_NoSavedLocation_AdvancesToAwaitingLocation()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000201";
        _fx.FakeAi.OverrideConfirmIntent("yeah that sounds about right", ConfirmReplyIntent.Yes);

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "yeah that sounds about right");

        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Send your location", StringComparison.OrdinalIgnoreCase));
        reply.ShouldNotBeNull();
        _fx.FakeAi.ExtractConfirmIntentCalls.ShouldBe(1);
    }

    [Fact]
    public async Task LlmNo_ResetsToAwaitingService()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000202";
        _fx.FakeAi.OverrideConfirmIntent("that's not what I meant", ConfirmReplyIntent.No);

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "that's not what I meant");

        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("What service do you need", StringComparison.OrdinalIgnoreCase));
        reply.ShouldNotBeNull();
        _fx.FakeAi.ExtractConfirmIntentCalls.ShouldBe(1);
    }

    [Fact]
    public async Task MidFlow_Question_RoutesToQaDispatcher_AndReprompts()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000203";
        // "what?" is a platform-shaped question. The mid-flow Q&A detector wins
        // over the ExtractConfirmIntent stage — assert the AI Q&A reply lands
        // AND the canonical reprompt arrives. ExtractConfirmIntent must NOT be
        // called on this path.

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "what?");

        var qa = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("[stub-qa]", StringComparison.Ordinal));
        qa.Body.ShouldContain("what?");

        var reprompt = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Please reply YES or NO", StringComparison.OrdinalIgnoreCase));
        reprompt.ShouldNotBeNull();

        _fx.FakeAi.ExtractConfirmIntentCalls.ShouldBe(0);
    }

    [Fact]
    public async Task LlmUnsure_Reprompts_OnAmbiguousNonQuestion()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000213";
        // "maybe" is not a question, not in QuickIntent's literal/phrase/fuzzy tiers —
        // lands in ExtractConfirmIntent. Override stays the LLM-Unsure path.
        _fx.FakeAi.OverrideConfirmIntent("maybe", ConfirmReplyIntent.Unsure);

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "maybe");

        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Please reply YES or NO", StringComparison.OrdinalIgnoreCase));
        reply.ShouldNotBeNull();
        _fx.FakeAi.ExtractConfirmIntentCalls.ShouldBe(1);
    }

    [Fact]
    public async Task PreLlm_AckSent()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000204";
        _fx.FakeAi.OverrideConfirmIntent("hmm not sure", ConfirmReplyIntent.Unsure);

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "hmm not sure");

        var ack = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("One moment", StringComparison.OrdinalIgnoreCase));
        ack.ShouldNotBeNull();
    }
}
