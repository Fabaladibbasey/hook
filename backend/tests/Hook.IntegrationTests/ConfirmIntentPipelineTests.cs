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
        _fx.FakeAi.OverrideConfirmIntent("nah I meant something else", ConfirmReplyIntent.No);

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "nah I meant something else");

        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("What service do you need", StringComparison.OrdinalIgnoreCase));
        reply.ShouldNotBeNull();
        _fx.FakeAi.ExtractConfirmIntentCalls.ShouldBe(1);
    }

    [Fact]
    public async Task LlmUnsure_Reprompts()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000203";
        _fx.FakeAi.OverrideConfirmIntent("what?", ConfirmReplyIntent.Unsure);

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "what?");

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
