import LegalFooter from "./components/LegalFooter";

export default function LandingPage() {
  return (
    <div className="min-h-screen grid grid-rows-[1fr_auto] p-6">
      <main className="text-center max-w-md justify-self-center self-center">
        <img
          src="/hook-logo.svg"
          alt="Hook"
          width={96}
          height={96}
          className="mx-auto mb-4"
        />
        <h1 className="text-2xl font-semibold text-ink mb-2">Hook</h1>
        <p className="text-slate-600">
          This page hosts secure chat sessions. Open the link sent to you on WhatsApp to start.
        </p>
      </main>
      <LegalFooter />
    </div>
  );
}
