namespace Hook.Features.Whatsapp.ReceiveWebhook;

public interface IInboundDedup
{
    bool TryAcquire(string messageId);
    void Reset();
}
