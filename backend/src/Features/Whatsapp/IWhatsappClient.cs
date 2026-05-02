using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Whatsapp;

public interface IWhatsappClient
{
    Task<string> SendTextAsync(PhoneNumber to, string body, CancellationToken ct = default);
}
