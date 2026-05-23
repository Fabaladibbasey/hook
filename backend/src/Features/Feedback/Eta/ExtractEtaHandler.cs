using Hook.Features.Ai;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Feedback.Eta;

public sealed class ExtractEtaHandler(
    IConversationAi ai,
    TimeProvider clock)
{
    // [NonTransactional]: AI inference takes 60-150s; opt out so the handler
    // doesn't pin an Npgsql connection across the Ollama window.
    [NonTransactional]
    public async Task Handle(ExtractEtaCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var eta = await ai.ExtractEtaAsync(cmd.Text, clock.GetUtcNow(), ct);
        await bus.PublishAsync(new ApplyEtaCommand(cmd.PendingId, cmd.MatchId, eta, cmd.From));
    }
}
