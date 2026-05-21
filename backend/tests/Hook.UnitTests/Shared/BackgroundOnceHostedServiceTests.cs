using System.Diagnostics;
using Hook.Shared.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Hook.UnitTests.Shared;

public sealed class BackgroundOnceHostedServiceTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }

    private sealed class Spy(
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        Func<CancellationToken, Task> body,
        bool shouldRun = true)
        : BackgroundOnceHostedService(services, lifetime)
    {
        public int RunCount { get; private set; }
        protected override bool ShouldRun() => shouldRun;
        protected override async Task RunAsync(IServiceProvider services, CancellationToken ct)
        {
            RunCount++;
            await body(ct);
        }
    }

    private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

    [Fact]
    public async Task ShouldRun_False_DoesNotInvokeRunAsync()
    {
        var spy = new Spy(EmptyServices(), new FakeLifetime(), _ => Task.CompletedTask, shouldRun: false);

        await spy.StartAsync(default);
        await Task.Delay(50);
        await spy.StopAsync(default);

        spy.RunCount.ShouldBe(0);
    }

    [Fact]
    public async Task StartAsync_Success_InvokesRunAsyncOnce()
    {
        var done = new TaskCompletionSource();
        var spy = new Spy(EmptyServices(), new FakeLifetime(), _ =>
        {
            done.TrySetResult();
            return Task.CompletedTask;
        });

        await spy.StartAsync(default);
        var winner = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        winner.ShouldBe(done.Task);
        spy.RunCount.ShouldBe(1);
    }

    [Fact]
    public async Task StartAsync_RunAsyncThrows_DoesNotCrashStop()
    {
        var spy = new Spy(EmptyServices(), new FakeLifetime(), _ =>
            throw new InvalidOperationException("boom"));

        await spy.StartAsync(default);
        await Task.Delay(50);
        // Must not throw even though the inner task faulted.
        await spy.StopAsync(default);

        spy.RunCount.ShouldBe(1);
    }

    [Fact]
    public async Task StopAsync_DoubleInvoke_IsNoOpOnSecondCall()
    {
        var spy = new Spy(EmptyServices(), new FakeLifetime(), async ct =>
            await Task.Delay(Timeout.Infinite, ct));

        await spy.StartAsync(default);
        await spy.StopAsync(default);
        // Second StopAsync must not throw ObjectDisposedException on the
        // already-disposed CTS — the Interlocked.Exchange guard makes it a no-op.
        await spy.StopAsync(default);
    }

    [Fact]
    public async Task StopAsync_DuringInFlight_CancelsWithinStopGrace()
    {
        var spy = new Spy(EmptyServices(), new FakeLifetime(), async ct =>
            await Task.Delay(Timeout.Infinite, ct));

        await spy.StartAsync(default);
        var sw = Stopwatch.StartNew();
        await spy.StopAsync(default);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6));
    }
}
