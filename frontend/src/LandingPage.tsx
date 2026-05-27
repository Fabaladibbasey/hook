import { Link } from "react-router-dom";
import LegalFooter from "./components/LegalFooter";
import { clientWhatsappLink, providerWhatsappLink } from "./features/legal/supportLink";

export default function LandingPage() {
  return (
    <div className="h-[100dvh] overflow-hidden grid grid-rows-[1fr_auto] bg-[radial-gradient(ellipse_at_top,_#f1f5f9_0%,_#ffffff_60%)]">
      <main className="max-w-md mx-auto w-full px-6 py-6 flex flex-col justify-center gap-6">
        <div className="text-center">
          <div className="relative inline-block">
            <span
              aria-hidden="true"
              className="absolute inset-0 -z-10 bg-emerald-100/50 blur-2xl rounded-full"
            />
            <img
              src="/hook-logo.svg"
              alt="Hook"
              width={64}
              height={64}
              className="w-16 h-16 mx-auto drop-shadow-lg"
            />
          </div>
          <h1 className="text-3xl font-semibold text-ink leading-tight mt-4">
            Find nearby providers on WhatsApp.
          </h1>
          <p className="text-slate-600 mt-2">
            Tell us what you need. We connect you on WhatsApp.
          </p>
        </div>

        <div className="flex flex-wrap justify-center gap-2">
          <span className="text-xs px-3 py-1 rounded-full bg-slate-100 text-slate-700">
            Free to use
          </span>
          <span className="text-xs px-3 py-1 rounded-full bg-slate-100 text-slate-700">
            No app needed
          </span>
          <span className="text-xs px-3 py-1 rounded-full bg-slate-100 text-slate-700">
            No login
          </span>
          <span className="text-xs px-3 py-1 rounded-full bg-slate-100 text-slate-700">
            24/7 support
          </span>
          <Link
            to="/privacy"
            className="text-xs px-3 py-1 rounded-full bg-slate-100 text-slate-700 hover:bg-slate-200 transition-colors"
          >
            Private by design
          </Link>
        </div>

        <div className="flex flex-col items-center gap-3">
          <a
            href={clientWhatsappLink}
            target="_blank"
            rel="noopener noreferrer"
            aria-label="Open WhatsApp to start"
            data-cta="client"
            className="inline-flex items-center justify-center min-h-[52px] px-8 rounded-full bg-[#25D366] hover:bg-[#1ebd5b] text-white text-base font-medium transition-colors"
          >
            Start on WhatsApp
          </a>
          <p className="text-xs text-slate-500 text-center">
            Ride · Delivery · Plumber · Electrician · anything
          </p>
        </div>

        <ol className="flex justify-between gap-2 text-xs text-slate-600">
          <li>
            <span className="font-semibold text-ink">1.</span> Send
          </li>
          <li>
            <span className="font-semibold text-ink">2.</span> Match
          </li>
          <li>
            <span className="font-semibold text-ink">3.</span> Chat
          </li>
        </ol>

        <a
          href={providerWhatsappLink}
          target="_blank"
          rel="noopener noreferrer"
          data-cta="provider"
          className="text-xs text-slate-500 hover:text-ink hover:underline text-center"
        >
          Offer services? Join as a provider →
        </a>
      </main>
      <LegalFooter />
    </div>
  );
}
