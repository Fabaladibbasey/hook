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
    <form onSubmit={submit} className="flex gap-2 p-3 border-t bg-white">
      <input
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder={disabled ? "Establishing secure channel…" : "Type a message…"}
        disabled={disabled}
        className="flex-1 border rounded-full px-4 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ink disabled:bg-slate-100 disabled:text-slate-400"
        autoComplete="off"
      />
      <button
        type="submit"
        disabled={busy || disabled || !text.trim()}
        className="px-4 py-2 bg-ink text-white rounded-full text-sm disabled:opacity-50"
      >
        Send
      </button>
    </form>
  );
}
