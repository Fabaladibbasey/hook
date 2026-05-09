import { useEffect, useRef } from "react";
import type { ChatMessage } from "../signalr/useChatHub";

export default function MessageList({
  messages,
  myParticipantId,
  chatId
}: {
  messages: ChatMessage[];
  myParticipantId: string;
  chatId: string;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const initialScrollChatId = useRef<string | null>(null);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;

    if (initialScrollChatId.current !== chatId && messages.length > 0) {
      el.scrollTop = el.scrollHeight;
      initialScrollChatId.current = chatId;
      return;
    }

    const distFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    if (distFromBottom < 120) {
      el.scrollTo({ top: el.scrollHeight, behavior: "auto" });
    }
  }, [messages, chatId]);

  return (
    <div ref={ref} className="flex-1 overflow-y-auto p-4 space-y-2 bg-slate-50">
      {messages.map((m) => {
        const mine = m.participantId === myParticipantId;
        const undecrypted = m.plaintext === null;
        const body = m.plaintext ?? "[encrypted, key lost]";
        return (
          <div key={m.id} className={`flex ${mine ? "justify-end" : "justify-start"}`}>
            <div
              className={`max-w-[80%] rounded-2xl px-3 py-2 text-base ${
                mine
                  ? "bg-ink text-white rounded-br-none"
                  : "bg-white border rounded-bl-none"
              } ${undecrypted ? "italic opacity-60" : ""}`}
            >
              {body}
              <div className="text-xs opacity-60 mt-1">
                {new Date(m.createdAt).toLocaleTimeString()}
              </div>
            </div>
          </div>
        );
      })}
    </div>
  );
}
