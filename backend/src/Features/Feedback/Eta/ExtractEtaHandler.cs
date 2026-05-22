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
    public async Task Handle(ExtractEtaRequested evt, IMessageBus bus, CancellationToken ct)
    {
        var eta = await ai.ExtractEtaAsync(evt.Text, clock.GetUtcNow(), ct);
        await bus.InvokeAsync(new ApplyEtaOutcome(evt.PendingId, evt.MatchId, eta, evt.From), ct);
    }
}
