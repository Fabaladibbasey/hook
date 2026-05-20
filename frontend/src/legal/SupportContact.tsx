const raw = (import.meta.env.VITE_SUPPORT_WHATSAPP as string | undefined)?.replace(/\D/g, "") ?? "";
if (raw.length === 0) {
  throw new Error("VITE_SUPPORT_WHATSAPP must be set (digits-only E.164, e.g. 220XXXXXXX)");
}

export const SUPPORT_WHATSAPP = raw;
export const supportWhatsappLink = `https://wa.me/${SUPPORT_WHATSAPP}`;

export function formatSupportWhatsapp(digits: string = SUPPORT_WHATSAPP): string {
  if (!(digits.startsWith("220") && digits.length === 10)) {
    return `+${digits}`;
  }
  return `+220 ${digits.slice(3, 6)} ${digits.slice(6)}`;
}

export const SUPPORT_WHATSAPP_DISPLAY = formatSupportWhatsapp();

export function WhatsAppContact() {
  return (
    <a href={supportWhatsappLink}>
      <code>{SUPPORT_WHATSAPP_DISPLAY}</code>
    </a>
  );
}
