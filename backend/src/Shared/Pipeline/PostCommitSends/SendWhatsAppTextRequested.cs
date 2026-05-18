using Hook.Features.Whatsapp.Phone;

namespace Hook.Shared.Pipeline.PostCommitSends;

public sealed record SendWhatsAppTextRequested(PhoneNumber To, string Text);
