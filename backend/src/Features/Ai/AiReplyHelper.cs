using System.Text.RegularExpressions;
using Hook.Features.Ai.Models;
using Hook.Features.Observability;

namespace Hook.Features.Ai;

public static class AiReplyHelper
{
    // Detect safety-trained refusals (e.g. "I'm sorry, but I can't assist...") so
    // they're never sent to users. Models like qwen2.5:3b can emit these for
    // benign prompts when the system prompt resembles a structured task.
    private static readonly Regex RefusalRx = new(
        @"^\s*(i'?m sorry,?\s+(but\s+)?(i\s+)?(can'?t|cannot)|sorry,?\s+(but\s+)?(i\s+)?(can'?t|cannot)|i\s+(can'?t|cannot)\s+(assist|help|do)|as an ai|i'?m (just )?an ai)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            if (RefusalRx.IsMatch(reply))
            {
                logger.LogWarning("AI returned safety refusal for stage {Stage}; dropping", stage);
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

    // Same as TryGenerateAsync, but returns a deterministic fallback string when
    // the AI fails / blanks / refuses, so the user is never left with no reply.
    // The dropped-metric still increments on failure.
    public static async Task<string> TryGenerateOrFallbackAsync(
        IConversationAi ai,
        ReplyContext ctx,
        string stage,
        string fallback,
        ILogger logger,
        CancellationToken ct)
    {
        var generated = await TryGenerateAsync(ai, ctx, stage, logger, ct);
        return generated ?? fallback;
    }
}
