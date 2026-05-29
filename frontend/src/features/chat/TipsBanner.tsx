import { useCallback, useState } from "react";
import { CHAT_TIPS } from "./tips";

function pickIndex(except?: number): number {
  if (CHAT_TIPS.length <= 1) return 0;
  if (except === undefined) return Math.floor(Math.random() * CHAT_TIPS.length);
  let idx = Math.floor(Math.random() * (CHAT_TIPS.length - 1));
  if (idx >= except) idx += 1;
  return idx;
}

export default function TipsBanner() {
  const [index, setIndex] = useState<number>(() => pickIndex());
  const next = useCallback(() => setIndex((prev) => pickIndex(prev)), []);

  return (
    <div className="mx-3 my-2 px-3 py-2 rounded-lg border border-amber-300/40 bg-amber-50/10 text-sm text-amber-100 flex items-start gap-3">
      <span className="font-semibold tracking-wide uppercase text-[10px] mt-0.5">Tip</span>
      <p className="flex-1 leading-snug">{CHAT_TIPS[index]}</p>
      <button
        type="button"
        onClick={next}
        className="shrink-0 text-xs px-2 py-1 rounded border border-amber-300/40 hover:bg-amber-100/10"
        aria-label="Show next tip"
      >
        Next tip
      </button>
    </div>
  );
}
