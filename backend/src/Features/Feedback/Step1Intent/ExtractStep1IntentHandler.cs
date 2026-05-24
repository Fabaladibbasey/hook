using Hook.Features.Ai;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Feedback.Step1Intent;

public sealed class ExtractStep1IntentHandler(
    IConversationAi ai,
    TimeProvider clock)
{
    [NonTransactional]
    public async Task Handle(ExtractStep1IntentCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var parsed = await ai.ExtractStep1IntentAsync(cmd.Text, clock.GetUtcNow(), ct);
        await bus.PublishAsync(new ApplyStep1IntentCommand(
            cmd.PendingId, cmd.MatchId, parsed.Intent, parsed.Eta, cmd.PromptedAt));
    }
}
