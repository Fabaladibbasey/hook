import { useState } from "react";

export default function MessageInput({
  onSend,
  disabled = false
}: {
  onSend: (text: string) => Promise<void>;
  disabled?: boolean;
}) {
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    const trimmed = text.trim();
    if (!trimmed || busy || disabled) return;
    setBusy(true);
    try {
      await onSend(trimmed);
      setText("");
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      onSubmit={submit}
      className="sticky bottom-0 flex gap-2 p-3 border-t bg-white"
      style={{ paddingBottom: "max(0.75rem, env(safe-area-inset-bottom))" }}
    >
      <input
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder={disabled ? "Establishing secure channel…" : "Type a message…"}
        disabled={disabled}
        inputMode="text"
        enterKeyHint="send"
        autoComplete="off"
        className="flex-1 min-h-[44px] border rounded-full px-4 py-2 text-base focus:outline-none focus:ring-2 focus:ring-ink disabled:bg-slate-100 disabled:text-slate-400"
      />
      <button
        type="submit"
        disabled={busy || disabled || !text.trim()}
        className="min-h-[44px] px-5 py-2 bg-ink text-white rounded-full text-base disabled:opacity-50"
      >
        Send
      </button>
    </form>
  );
}
