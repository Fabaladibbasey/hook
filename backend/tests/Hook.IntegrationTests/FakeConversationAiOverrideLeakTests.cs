using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-Migration")]
public sealed class FakeConversationAiOverrideLeakTests : PipelineTestBase
{
    public FakeConversationAiOverrideLeakTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task ResetAsync_ClearsFakeConversationAiIntentOverrides()
    {
        var fake = (FakeConversationAi)_fx.Factory.Services.GetRequiredService<IConversationAi>();
        const string trigger = "self-test override";
        fake.OverrideIntent(trigger, new IntentDetectionResult(IntentKind.Confirmation, 0.99, "en", "test"));

        var beforeReset = await fake.DetectIntentAsync(trigger);
        beforeReset.Intent.ShouldBe(IntentKind.Confirmation);

        await _fx.ResetAsync();

        var afterReset = await fake.DetectIntentAsync(trigger);
        afterReset.Intent.ShouldNotBe(IntentKind.Confirmation);
    }
}
