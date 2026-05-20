import { Link } from "react-router-dom";
import type { ReactNode } from "react";
import LegalFooter from "../components/LegalFooter";

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
