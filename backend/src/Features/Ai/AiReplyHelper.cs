using Hook.Features.Ai.Models;
using Hook.Features.Observability;

namespace Hook.Features.Ai;

public static class AiReplyHelper
{
    public static async Task<string?> TryGenerateAsync(
        IConversationAi ai,
        ReplyContext ctx,
        string stage,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var reply = await ai.GenerateReplyAsync(ctx, ct);
            if (string.IsNullOrWhiteSpace(reply))
            {
                logger.LogWarning("AI returned blank reply for stage {Stage}", stage);
                HookMetrics.AiOutboundDropped.Add(1, new KeyValuePair<string, object?>("stage", stage));
                return null;
            }
            return reply;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI reply generation failed for stage {Stage}", stage);
            HookMetrics.AiOutboundDropped.Add(1, new KeyValuePair<string, object?>("stage", stage));
            return null;
        }
    }
}
