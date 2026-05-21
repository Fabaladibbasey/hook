using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class AiReadinessProbeTests
{
    private static IOptions<OllamaOptions> Opts(int probeTimeoutSeconds = 2, int cacheSeconds = 10) =>
        Options.Create(new OllamaOptions { ReadinessProbeTimeoutSeconds = probeTimeoutSeconds, ReadinessCacheSeconds = cacheSeconds });

    [Fact]
    public async Task ProbeAsync_ShouldReturnHealthy_WhenAiSucceeds()
    {
        var aiMock = new Mock<IConversationAi>();
        aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentDetectionResult(IntentKind.Unknown, 0.5, "en", string.Empty));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var probe = new AiReadinessProbe(aiMock.Object, clock, Opts(), NullLogger<AiReadinessProbe>.Instance);

        var result = await probe.ProbeAsync();

        result.Healthy.ShouldBeTrue();
        result.Error.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProbeAsync_ShouldReturnUnhealthy_WhenAiThrows()
    {
        var aiMock = new Mock<IConversationAi>();
        aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ollama down"));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var probe = new AiReadinessProbe(aiMock.Object, clock, Opts(), NullLogger<AiReadinessProbe>.Instance);

        var result = await probe.ProbeAsync();

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("ollama down");
    }

    [Fact]
    public async Task ProbeAsync_ShouldNotCallAi_WhenWithinTenSecondTtl()
    {
        var aiMock = new Mock<IConversationAi>();
        aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentDetectionResult(IntentKind.Unknown, 0.5, "en", string.Empty));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var probe = new AiReadinessProbe(aiMock.Object, clock, Opts(), NullLogger<AiReadinessProbe>.Instance);

        for (var i = 0; i < 5; i++)
        {
            await probe.ProbeAsync();
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        aiMock.Verify(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProbeAsync_ShouldRefreshAi_AfterTenSecondTtlElapses()
    {
        var aiMock = new Mock<IConversationAi>();
        aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentDetectionResult(IntentKind.Unknown, 0.5, "en", string.Empty));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var probe = new AiReadinessProbe(aiMock.Object, clock, Opts(), NullLogger<AiReadinessProbe>.Instance);

        await probe.ProbeAsync();
        clock.Advance(TimeSpan.FromSeconds(11));
        await probe.ProbeAsync();

        aiMock.Verify(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProbeAsync_ShouldReturnUnhealthy_WhenAiSlowerThanConfiguredTimeout()
    {
        var aiMock = new Mock<IConversationAi>();
        aiMock.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new IntentDetectionResult(IntentKind.Unknown, 0.5, "en", string.Empty);
            });
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var probe = new AiReadinessProbe(aiMock.Object, clock, Opts(probeTimeoutSeconds: 1), NullLogger<AiReadinessProbe>.Instance);

        var result = await probe.ProbeAsync();

        result.Healthy.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }
}
