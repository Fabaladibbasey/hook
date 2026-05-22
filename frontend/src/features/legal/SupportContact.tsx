import { supportWhatsappLink, SUPPORT_WHATSAPP_DISPLAY } from "./supportLink";

export function WhatsAppContact() {
  return (
    <a href={supportWhatsappLink}>
      <code>{SUPPORT_WHATSAPP_DISPLAY}</code>
    </a>
  );
}
