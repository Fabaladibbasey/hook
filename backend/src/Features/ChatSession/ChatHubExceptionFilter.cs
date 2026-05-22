using Hook.Features.Observability;
using Microsoft.AspNetCore.SignalR;

namespace Hook.Features.ChatSession;

internal sealed class ChatHubExceptionFilter(
    ILogger<ChatHubExceptionFilter> logger) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext ctx,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(ctx);
        }
        catch (HubException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ctx.Context.ConnectionAborted.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            HookMetrics.ChatHubFaults.Add(1,
                new KeyValuePair<string, object?>("method", ctx.HubMethodName ?? "<unknown>"));
            logger.LogError(ex, "ChatHub {Method} faulted connection={ConnectionId}",
                ctx.HubMethodName, ctx.Context.ConnectionId);
            throw;
        }
    }

    public async Task OnConnectedAsync(HubLifetimeContext ctx, Func<HubLifetimeContext, Task> next)
    {
        try { await next(ctx); }
        catch (OperationCanceledException) when (ctx.Context.ConnectionAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            HookMetrics.ChatHubFaults.Add(1,
                new KeyValuePair<string, object?>("method", "OnConnectedAsync"));
            logger.LogError(ex, "ChatHub OnConnectedAsync faulted connection={ConnectionId}",
                ctx.Context.ConnectionId);
            throw;
        }
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext ctx,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        try { await next(ctx, exception); }
        // Client-driven hard-disconnect with an in-flight cleanup OCE is not a fault —
        // skip the counter and the swallow-and-don't-rethrow path matches OnDisconnected
        // semantics (SignalR ignores OnDisconnectedAsync exceptions by design).
        catch (OperationCanceledException) when (ctx.Context.ConnectionAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            HookMetrics.ChatHubFaults.Add(1,
                new KeyValuePair<string, object?>("method", "OnDisconnectedAsync"));
            logger.LogError(ex, "ChatHub OnDisconnectedAsync faulted connection={ConnectionId}",
                ctx.Context.ConnectionId);
        }
    }
}
