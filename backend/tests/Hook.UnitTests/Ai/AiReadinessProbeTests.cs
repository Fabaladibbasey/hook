using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class AiReadinessProbeTests
{
    [Fact]
    public async Task ProbeAsync_ShouldReturnHealthy_WhenAiSucceeds()
    {
        var ai = new CountingAi();
        var clock = new FrozenClock(DateTimeOffset.UtcNow);
        var probe = new AiReadinessProbe(ai, clock, NullLogger<AiReadinessProbe>.Instance);

        var result = await probe.ProbeAsync();

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task ProbeAsync_ShouldReturnUnhealthy_WhenAiThrows()
    {
        var ai = new ThrowingAi(new InvalidOperationException("ollama down"));
        var clock = new FrozenClock(DateTimeOffset.UtcNow);
        var probe = new AiReadinessProbe(ai, clock, NullLogger<AiReadinessProbe>.Instance);

        var result = await probe.ProbeAsync();

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("ollama down");
    }

    [Fact]
    public async Task ProbeAsync_ShouldNotCallAi_WhenWithinTenSecondTtl()
    {
        var ai = new CountingAi();
        var start = DateTimeOffset.UtcNow;
        var clock = new FrozenClock(start);
        var probe = new AiReadinessProbe(ai, clock, NullLogger<AiReadinessProbe>.Instance);

        for (var i = 0; i < 5; i++)
        {
            await probe.ProbeAsync();
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        ai.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ProbeAsync_ShouldRefreshAi_AfterTenSecondTtlElapses()
    {
        var ai = new CountingAi();
        var clock = new FrozenClock(DateTimeOffset.UtcNow);
        var probe = new AiReadinessProbe(ai, clock, NullLogger<AiReadinessProbe>.Instance);

        await probe.ProbeAsync();
        clock.Advance(TimeSpan.FromSeconds(11));
        await probe.ProbeAsync();

        ai.Calls.ShouldBe(2);
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class CountingAi : IConversationAi
    {
        public int Calls { get; private set; }

        public Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new IntentDetectionResult(IntentKind.Unknown, 0.5, "en", null));
        }

        public Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceJudgeResult> JudgeServiceMatchAsync(string proposedSlug, IReadOnlyList<string> candidateSlugs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingAi(Exception toThrow) : IConversationAi
    {
        public Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default) => throw toThrow;
        public Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceJudgeResult> JudgeServiceMatchAsync(string proposedSlug, IReadOnlyList<string> candidateSlugs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
