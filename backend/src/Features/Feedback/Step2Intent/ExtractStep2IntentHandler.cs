using Hook.Features.Ai;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Feedback.Step2Intent;

public sealed class ExtractStep2IntentHandler(
    IConversationAi ai,
    TimeProvider clock)
{
    [NonTransactional]
    public async Task Handle(ExtractStep2IntentCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var parsed = await ai.ExtractStep2IntentAsync(cmd.Text, clock.GetUtcNow(), ct);
        await bus.PublishAsync(new ApplyStep2IntentCommand(
            cmd.PendingId, cmd.MatchId, parsed.Intent, parsed.Eta));
    }
}
