using Hook.Features.ChatSession;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.Chat;

public class ChatRepositoryDeviceKeyTests : IClassFixture<DevPipelineFixture>
{
    private readonly DevPipelineFixture _fx;
    public ChatRepositoryDeviceKeyTests(DevPipelineFixture fx) => _fx = fx;

    private async Task<(Guid chatId, Guid clientPid, Guid providerPid)> SeedChatAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<IChatRepository>();

        var session = ChatSession.Create(TimeSpan.FromHours(1), DateTimeOffset.UtcNow);
        var client = ChatParticipant.Create(session.Id, ChatParticipantRole.Client, "+14155550100");
        var provider = ChatParticipant.Create(session.Id, ChatParticipantRole.Provider, "+14155550101");

        await chats.AddSessionAsync(session);
        await chats.AddParticipantsAsync([client, provider]);
        await chats.SaveChangesAsync();

        return (session.Id, client.Id, provider.Id);
    }

    [Fact]
    public async Task UpsertDeviceKeyAsync_inserts_then_updates_existing_row()
    {
        var (chatId, clientPid, _) = await SeedChatAsync();
        var deviceId = Guid.NewGuid();
        var firstKey = new byte[] { 0x01, 0x02 };
        var secondKey = new byte[] { 0x03, 0x04 };

        using var scope = _fx.Factory.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<IChatRepository>();

        await chats.UpsertDeviceKeyAsync(chatId, clientPid, deviceId, firstKey, DateTimeOffset.UtcNow);
        await chats.SaveChangesAsync();

        await chats.UpsertDeviceKeyAsync(chatId, clientPid, deviceId, secondKey, DateTimeOffset.UtcNow);
        await chats.SaveChangesAsync();

        using var verifyScope = _fx.Factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<HookDbContext>();
        var rows = await db.ChatDeviceKeys
            .Where(k => k.ParticipantId == clientPid && k.DeviceId == deviceId)
            .ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].PublicKey.ShouldBe(secondKey);
    }

    [Fact]
    public async Task GetDeviceKeysAsync_returns_all_device_keys_for_chat()
    {
        var (chatId, clientPid, providerPid) = await SeedChatAsync();
        var clientDeviceA = Guid.NewGuid();
        var clientDeviceB = Guid.NewGuid();
        var providerDevice = Guid.NewGuid();

        using var scope = _fx.Factory.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<IChatRepository>();
        await chats.UpsertDeviceKeyAsync(chatId, clientPid, clientDeviceA, [0xA1], DateTimeOffset.UtcNow);
        await chats.UpsertDeviceKeyAsync(chatId, clientPid, clientDeviceB, [0xA2], DateTimeOffset.UtcNow);
        await chats.UpsertDeviceKeyAsync(chatId, providerPid, providerDevice, [0xB1], DateTimeOffset.UtcNow);
        await chats.SaveChangesAsync();

        var keys = await chats.GetDeviceKeysAsync(chatId);
        keys.Count.ShouldBe(3);
        keys.Select(k => k.DeviceId).ShouldBe(
            new[] { clientDeviceA, clientDeviceB, providerDevice }, ignoreOrder: true);
    }

    [Fact]
    public async Task GetMessagesForDeviceAsync_returns_only_recipient_envelopes()
    {
        var (chatId, clientPid, providerPid) = await SeedChatAsync();
        var senderDevice = Guid.NewGuid();
        var recipientA = Guid.NewGuid();
        var recipientB = Guid.NewGuid();

        using var scope = _fx.Factory.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<IChatRepository>();

        var msg = new ChatMessage
        {
            ChatId = chatId,
            ParticipantId = clientPid,
            SenderDeviceId = senderDevice,
            Sequence = 1,
            Recipients =
            [
                new ChatMessageRecipient { MessageId = Guid.Empty /* will be assigned */, RecipientDeviceId = recipientA, Ciphertext = [0xAA], Nonce = new byte[12] },
                new ChatMessageRecipient { MessageId = Guid.Empty, RecipientDeviceId = recipientB, Ciphertext = [0xBB], Nonce = new byte[12] }
            ]
        };
        // Assign MessageId after Id is set (init-only Id default = NewGuid())
        msg = new ChatMessage
        {
            Id = msg.Id,
            ChatId = chatId,
            ParticipantId = clientPid,
            SenderDeviceId = senderDevice,
            Sequence = 1,
            Recipients =
            [
                new ChatMessageRecipient { MessageId = msg.Id, RecipientDeviceId = recipientA, Ciphertext = [0xAA], Nonce = new byte[12] },
                new ChatMessageRecipient { MessageId = msg.Id, RecipientDeviceId = recipientB, Ciphertext = [0xBB], Nonce = new byte[12] }
            ]
        };
        await chats.AddMessageAsync(msg);
        await chats.SaveChangesAsync();

        var rowsForA = await chats.GetMessagesForDeviceAsync(chatId, recipientA, take: 50);
        rowsForA.Count.ShouldBe(1);
        rowsForA[0].Envelope.Ciphertext.ShouldBe(new byte[] { 0xAA });

        var rowsForB = await chats.GetMessagesForDeviceAsync(chatId, recipientB, take: 50);
        rowsForB.Count.ShouldBe(1);
        rowsForB[0].Envelope.Ciphertext.ShouldBe(new byte[] { 0xBB });

        var rowsForUnknown = await chats.GetMessagesForDeviceAsync(chatId, Guid.NewGuid(), take: 50);
        rowsForUnknown.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetDeviceKeyAsync_returns_null_for_unknown_pair()
    {
        var (_, clientPid, _) = await SeedChatAsync();
        using var scope = _fx.Factory.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<IChatRepository>();

        var key = await chats.GetDeviceKeyAsync(clientPid, Guid.NewGuid());
        key.ShouldBeNull();
    }
}
