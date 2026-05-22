using System.Diagnostics.Metrics;
using System.Reflection;
using Hook.Features.ChatSession;
using Hook.Features.Observability;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class ChatHubExceptionFilterTests
{
    [Fact]
    public async Task NoException_Returns_NextResult_NoAbort()
    {
        using var capture = new FaultCounter();
        var (filter, callerCtx, invocation) = Build();

        var result = await filter.InvokeMethodAsync(invocation, _ => new ValueTask<object?>("ok"));

        result.ShouldBe("ok");
        callerCtx.Verify(c => c.Abort(), Times.Never);
    }

    [Fact]
    public async Task TaskOfT_HubMethod_OnNoException_ReturnsTypedResult()
    {
        using var capture = new FaultCounter();
        var (filter, _, invocation) = Build();

        var result = await filter.InvokeMethodAsync(invocation, _ => new ValueTask<object?>(42));

        result.ShouldBe(42);
    }

    [Fact]
    public async Task HubException_Rethrown_NotSwallowed()
    {
        using var capture = new FaultCounter();
        var (filter, callerCtx, invocation) = Build();
        var ex = new HubException("client-facing");

        await Should.ThrowAsync<HubException>(async () =>
            await filter.InvokeMethodAsync(invocation, _ => throw ex));

        callerCtx.Verify(c => c.Abort(), Times.Never);
        capture.Count.ShouldBe(0);
    }

    [Fact]
    public async Task OperationCanceledException_WithoutConnectionAborted_Rethrown()
    {
        // OCE-without-abort takes the catch-all branch → counts as fault + rethrows.
        // Host-stopping OCEs would route here; current intent treats them as faults
        // (visible in metrics + DLQ-equivalent client mask). If that intent changes,
        // broaden the filter to also gate on IHostApplicationLifetime.ApplicationStopping.
        using var capture = new FaultCounter();
        var (filter, callerCtx, invocation) = Build(connectionAborted: false);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await filter.InvokeMethodAsync(invocation, _ => throw new OperationCanceledException()));

        callerCtx.Verify(c => c.Abort(), Times.Never);
        capture.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OperationCanceledException_WithConnectionAborted_Swallowed()
    {
        using var capture = new FaultCounter();
        var (filter, callerCtx, invocation) = Build(connectionAborted: true);

        var result = await filter.InvokeMethodAsync(invocation, _ => throw new OperationCanceledException());

        result.ShouldBeNull();
        callerCtx.Verify(c => c.Abort(), Times.Never);
        capture.Count.ShouldBe(0);
    }

    [Fact]
    public async Task UnexpectedException_Logged_Metric_Rethrown()
    {
        using var capture = new FaultCounter();
        var (filter, callerCtx, invocation) = Build();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await filter.InvokeMethodAsync(invocation, _ => throw new InvalidOperationException("boom")));

        capture.Count.ShouldBe(1);
        callerCtx.Verify(c => c.Abort(), Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_UnexpectedException_Metric_Rethrown()
    {
        using var capture = new FaultCounter();
        var filter = new ChatHubExceptionFilter(NullLogger<ChatHubExceptionFilter>.Instance);
        var lifetimeCtx = BuildLifetimeContext(connectionAborted: false);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await filter.OnConnectedAsync(lifetimeCtx, _ => throw new InvalidOperationException("connect-boom")));

        capture.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OnDisconnectedAsync_UnexpectedException_Metric_NotRethrown()
    {
        using var capture = new FaultCounter();
        var filter = new ChatHubExceptionFilter(NullLogger<ChatHubExceptionFilter>.Instance);
        var lifetimeCtx = BuildLifetimeContext(connectionAborted: false);

        await filter.OnDisconnectedAsync(lifetimeCtx, exception: null,
            (_, _) => throw new InvalidOperationException("disconnect-boom"));

        capture.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OnDisconnectedAsync_OperationCanceled_WithConnectionAborted_NotAFault()
    {
        // Client-driven hard-disconnect with an in-flight cleanup OCE is normal;
        // do not inflate ChatHubFaults.
        using var capture = new FaultCounter();
        var filter = new ChatHubExceptionFilter(NullLogger<ChatHubExceptionFilter>.Instance);
        var lifetimeCtx = BuildLifetimeContext(connectionAborted: true);

        await filter.OnDisconnectedAsync(lifetimeCtx, exception: null,
            (_, _) => throw new OperationCanceledException());

        capture.Count.ShouldBe(0);
    }

    private static (ChatHubExceptionFilter filter, Mock<HubCallerContext> callerCtx, HubInvocationContext invocation) Build(bool connectionAborted = false)
    {
        var filter = new ChatHubExceptionFilter(NullLogger<ChatHubExceptionFilter>.Instance);
        var callerCtx = new Mock<HubCallerContext>();
        callerCtx.SetupGet(c => c.ConnectionId).Returns("conn-1");
        callerCtx.SetupGet(c => c.ConnectionAborted)
            .Returns(connectionAborted ? new CancellationToken(canceled: true) : CancellationToken.None);
        var method = typeof(StubHub).GetMethod(nameof(StubHub.NoOp), BindingFlags.Instance | BindingFlags.Public)!;
        var invocation = new HubInvocationContext(
            callerCtx.Object,
            serviceProvider: new ServiceCollection().BuildServiceProvider(),
            hub: new StubHub(),
            hubMethod: method,
            hubMethodArguments: Array.Empty<object?>());
        return (filter, callerCtx, invocation);
    }

    private static HubLifetimeContext BuildLifetimeContext(bool connectionAborted)
    {
        var callerCtx = new Mock<HubCallerContext>();
        callerCtx.SetupGet(c => c.ConnectionId).Returns("conn-1");
        callerCtx.SetupGet(c => c.ConnectionAborted)
            .Returns(connectionAborted ? new CancellationToken(canceled: true) : CancellationToken.None);
        return new HubLifetimeContext(callerCtx.Object, new ServiceCollection().BuildServiceProvider(), new StubHub());
    }

    private sealed class StubHub : Hub
    {
        public Task NoOp() => Task.CompletedTask;
    }

    private sealed class FaultCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _count;
        public long Count => Interlocked.Read(ref _count);

        public FaultCounter()
        {
            _listener.InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == HookMetrics.MeterName && inst.Name == "hook.chat_hub.faults")
                    l.EnableMeasurementEvents(inst);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _count, value));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
