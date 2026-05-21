using System.Diagnostics;
using Hook.Features.Ai;
using Hook.Features.Ai.Warmup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Hook.UnitTests.Ai;

public sealed class AiWarmupHostedServiceTests
{
    private static IServiceProvider BuildProvider(Mock<IConversationAi> aiMock)
    {
        var services = new ServiceCollection();
        services.AddSingleton(aiMock.Object);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new OllamaOptions()));
        services.AddLogging();
        services.AddScoped<AiReadinessProbe>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }

    [Fact]
    public async Task StartAsync_ProbeSucceeds_CompletesWarmupTask()
    {
        var ai = new Mock<IConversationAi>();
        ai.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Features.Ai.Models.IntentDetectionResult(Hook.Features.Ai.Models.IntentKind.ServiceRequest, 1.0, "en", "ok"));
        var svc = new AiWarmupHostedService(BuildProvider(ai), new FakeLifetime(),
            Options.Create(new OllamaOptions()),
            NullLogger<AiWarmupHostedService>.Instance);

        await svc.StartAsync(default);
        var winner = await Task.WhenAny(svc.WarmupCompletion, Task.Delay(TimeSpan.FromSeconds(5)));

        winner.ShouldBe(svc.WarmupCompletion);
    }

    [Fact]
    public async Task StartAsync_ProbeThrows_StillCompletesWarmupTask()
    {
        // Probe failure must not deadlock /readyz gating downstream.
        var ai = new Mock<IConversationAi>();
        ai.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("simulated ollama down"));
        var svc = new AiWarmupHostedService(BuildProvider(ai), new FakeLifetime(),
            Options.Create(new OllamaOptions()),
            NullLogger<AiWarmupHostedService>.Instance);

        await svc.StartAsync(default);
        var winner = await Task.WhenAny(svc.WarmupCompletion, Task.Delay(TimeSpan.FromSeconds(5)));

        winner.ShouldBe(svc.WarmupCompletion);
    }

    [Fact]
    public async Task StopAsync_DuringInFlightProbe_CancelsWithinStopGrace()
    {
        // Probe blocks forever; StopAsync must cancel and return within the 5s
        // grace + small slack so a host shutdown is not held back by Ollama.
        var ai = new Mock<IConversationAi>();
        ai.Setup(x => x.DetectIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return default(Features.Ai.Models.IntentDetectionResult)!;
            });
        var svc = new AiWarmupHostedService(BuildProvider(ai), new FakeLifetime(),
            Options.Create(new OllamaOptions()),
            NullLogger<AiWarmupHostedService>.Instance);

        await svc.StartAsync(default);
        var sw = Stopwatch.StartNew();
        await svc.StopAsync(default);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6));
    }
}
