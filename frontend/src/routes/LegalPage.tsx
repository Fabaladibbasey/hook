import { Link } from "react-router-dom";
import type { ReactNode } from "react";
import LegalFooter from "../components/LegalFooter";

const env = (import.meta.env.VITE_SUPPORT_WHATSAPP as string | undefined)?.replace(/\D/g, "");
export const SUPPORT_WHATSAPP = env && env.length > 0 ? env : "2206784709";

export const supportWhatsappLink = `https://wa.me/${SUPPORT_WHATSAPP}`;

export function formatSupportWhatsapp(digits: string = SUPPORT_WHATSAPP): string {
  if (digits.startsWith("220") && digits.length === 10) {
    return `+220 ${digits.slice(3, 6)} ${digits.slice(6)}`;
  }
  return `+${digits}`;
}

export const SUPPORT_WHATSAPP_DISPLAY = formatSupportWhatsapp();

export default function LegalPage({
  title,
  effective,
  children
}: {
  title: string;
  effective: string;
  children: ReactNode;
}) {
  return (
    <div className="min-h-screen bg-white text-ink">
      <header className="border-b border-slate-200 px-4 py-3">
        <div className="mx-auto flex max-w-3xl items-center gap-3">
          <Link to="/" className="flex items-center gap-2">
            <img src="/hook-logo.svg" alt="Hook" width={32} height={32} className="rounded" />
            <span className="text-lg font-semibold">Hook</span>
          </Link>
          <span className="ml-auto text-sm text-slate-500">
            <Link to="/" className="hover:underline">
              Back
            </Link>
          </span>
        </div>
      </header>
      <main className="mx-auto max-w-3xl px-4 py-8 leading-relaxed">
        <h1 className="mb-2 text-2xl font-bold">{title}</h1>
        <p className="mb-6 text-sm text-slate-500">Effective {effective}</p>
        <div className="space-y-5 text-[15px] [&_h2]:mt-6 [&_h2]:text-lg [&_h2]:font-semibold [&_ul]:list-disc [&_ul]:pl-6 [&_a]:text-blue-600 [&_a:hover]:underline">
          {children}
        </div>
      </main>
      <LegalFooter />
    </div>
  );
}
