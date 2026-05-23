using Hook.Features.Whatsapp.Phone;

namespace Hook.Shared.Pipeline.PostCommitSends;

public sealed record SendWhatsAppTextCommand(PhoneNumber To, string Text);
