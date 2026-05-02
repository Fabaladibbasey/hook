using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Whatsapp.Dev;

public sealed class FakeWhatsappClient(
    IDevOutbox outbox,
    ILogger<FakeWhatsappClient> logger) : IWhatsappClient
{
    public Task<string> SendTextAsync(PhoneNumber to, string body, CancellationToken ct = default)
    {
        var messageId = $"wamid.dev.{Guid.NewGuid():N}";
        var msg = new DevOutboxMessage(DateTimeOffset.UtcNow, to.Value, body, messageId);

        outbox.Publish(msg);
        logger.LogInformation(
            "[DEV] Fake WhatsApp send to {To} messageId={MessageId} body={Body}",
            to.Mask(),
            messageId,
            body);

        return Task.FromResult(messageId);
    }
}
