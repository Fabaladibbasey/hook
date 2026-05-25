using Hook.Features.Ai;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.ServiceRequest.Create.ConfirmIntent;

public sealed class ExtractConfirmIntentHandler(IConversationAi ai)
{
    // [NonTransactional]: Ollama inference is 60-150s; pinning an Npgsql
    // connection across that window deadlocks the pool. The apply sibling is
    // transactional and owns the durable state mutation. ai.ExtractConfirmIntent
    // absorbs all non-OCE failures (returns ConfirmReplyIntent.Unsure); the
    // handler therefore always publishes ApplyConfirmIntentCommand — never gates
    // on an exception. Any future change to throw-on-failure in the AI layer
    // must add a try/catch here.
    [NonTransactional]
    public async Task Handle(ExtractConfirmIntentCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var intent = await ai.ExtractConfirmIntentAsync(cmd.SlugAsked, cmd.Text, ct);
        await bus.PublishAsync(new ApplyConfirmIntentCommand(
            cmd.Phone, intent, cmd.DraftStampedAt));
    }
}
