using Hook.Features.ChatSession.ParticipantAggregate;
using Microsoft.Extensions.Options;

namespace Hook.Features.ChatSession;

public sealed class ChatSessionFactory(
    IChatRepository chats,
    IOptions<ChatOptions> options,
    TimeProvider clock)
{
    public sealed record ChatLinks(Guid ChatId, string ClientUrl, string ProviderUrl);

    public Task<ChatLinks> CreateAsync(string clientPhone, string providerPhone, CancellationToken ct = default) =>
        CreateAsync(Guid.CreateVersion7(), clientPhone, providerPhone, ct);

    public async Task<ChatLinks> CreateAsync(
        Guid chatId,
        string clientPhone,
        string providerPhone,
        CancellationToken ct = default)
    {
        var opts = options.Value;
        var session = SessionAggregate.ChatSession.Create(
            chatId,
            TimeSpan.FromHours(opts.HardExpiryHours),
            clock.GetUtcNow());

        var clientParticipant = ChatParticipant.Create(session.Id, ChatParticipantRole.Client, clientPhone);
        var providerParticipant = ChatParticipant.Create(session.Id, ChatParticipantRole.Provider, providerPhone);

        await chats.AddSessionAsync(session, ct);
        await chats.AddParticipantsAsync([clientParticipant, providerParticipant], ct);

        var baseUrl = opts.PublicChatBaseUrl.TrimEnd('/');
        return new ChatLinks(
            session.Id,
            $"{baseUrl}/c/{session.Id:N}/{clientParticipant.Token}",
            $"{baseUrl}/c/{session.Id:N}/{providerParticipant.Token}");
    }
}
