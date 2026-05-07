import { Link } from "react-router-dom";

type Variant = "page" | "chat";

const styles: Record<Variant, string> = {
  page: "border-t border-slate-200 px-4 py-4 text-center text-xs text-slate-500",
  chat: "border-b border-slate-100 px-4 py-1 text-right text-xs text-slate-500"
};

export default function LegalFooter({ variant = "page" }: { variant?: Variant }) {
  const inner = (
    <>
      <Link to="/terms" className="hover:underline">
        Terms
      </Link>
      <span aria-hidden="true" className="mx-2">
        ·
      </span>
      <Link to="/privacy" className="hover:underline">
        Privacy
      </Link>
    </>
  );
  return variant === "page" ? (
    <footer className={styles.page}>{inner}</footer>
  ) : (
    <div className={styles.chat}>{inner}</div>
  );
}
