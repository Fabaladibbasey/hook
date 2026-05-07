import LegalFooter from "./components/LegalFooter";

export default function App() {
  return (
    <div className="min-h-screen grid grid-rows-[1fr_auto] p-6">
      <div className="text-center max-w-md justify-self-center self-center">
        <img
          src="/hook-logo.png"
          alt="Hook"
          className="mx-auto mb-4 w-40 h-auto rounded-lg shadow-md"
        />
        <p className="text-slate-600">
          This page hosts secure chat sessions. Open the link sent to you on WhatsApp to start.
        </p>
      </div>
      <LegalFooter />
    </div>
  );
}
