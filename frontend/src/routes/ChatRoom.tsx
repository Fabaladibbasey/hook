import { useEffect, useMemo, useState } from "react";
import { useParams } from "react-router-dom";
import { useChatHub } from "../signalr/useChatHub";
import MessageList from "../components/MessageList";
import MessageInput from "../components/MessageInput";
import RevokedToast from "../components/RevokedToast";
import WaitingForPeer from "../components/WaitingForPeer";
import LegalFooter from "../components/LegalFooter";
import { fetchJson } from "../api/fetchJson";
import { getOrCreateDeviceId } from "../crypto/deviceId";

type OpenResponse = {
  chatId: string;
  participantId: string;
  role: "Client" | "Provider";
  sessionId: string;
  status: "Active" | "Ended" | "Expired";
  expiresAt: string;
};

export default function ChatRoom() {
  const { token } = useParams<{ chatId: string; token: string }>();
  const [open, setOpen] = useState<OpenResponse | null>(null);
  const [openError, setOpenError] = useState<string | null>(null);

  useEffect(() => {
    if (!token) return;
    fetchJson<OpenResponse>(`/api/chat/open?token=${encodeURIComponent(token)}`)
      .then(setOpen)
      .catch((err) => setOpenError(err instanceof Error ? err.message : String(err)));
  }, [token]);

  const deviceId = useMemo(() => getOrCreateDeviceId(), []);
  const { state, send, endChat } = useChatHub(
    open?.chatId ?? "",
    open?.participantId ?? "",
    token ?? "",
    open?.sessionId ?? "",
    deviceId
  );
  const [ending, setEnding] = useState(false);
  const [confirmEnd, setConfirmEnd] = useState(false);

  useEffect(() => {
    if (!confirmEnd) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setConfirmEnd(false);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [confirmEnd]);

  if (openError) return <CenterMessage>Could not open chat: {openError}</CenterMessage>;
  if (!open) return <CenterMessage>Loading…</CenterMessage>;
  if (open.status !== "Active") return <CenterMessage>This chat has {open.status.toLowerCase()}.</CenterMessage>;

  if (state.kind === "revoked") return <RevokedToast />;
  if (state.kind === "ended") return <CenterMessage>{endedMessage(state.reason, state.endedBy, open.role)}</CenterMessage>;
  if (state.kind === "error") return <CenterMessage>Connection error: {state.reason}</CenterMessage>;
  if (state.kind === "connecting") return <CenterMessage>Connecting…</CenterMessage>;

  const waitingForPeer = state.kind === "waiting-peer";

  const confirmAndEnd = async () => {
    if (ending) return;
    setEnding(true);
    try {
      await endChat();
      setConfirmEnd(false);
    } finally {
      setEnding(false);
    }
  };

  return (
    <div className="flex flex-col h-full w-full md:max-w-2xl md:mx-auto bg-white md:border-x">
      <header
        className="sticky top-0 z-10 px-4 py-3 border-b bg-ink text-white flex items-center justify-between"
        style={{ paddingTop: "max(0.75rem, env(safe-area-inset-top))" }}
      >
        <div className="flex items-center gap-2">
          <img src="/hook-logo.png" alt="Hook" className="h-8 w-auto rounded" />
          <div>
            <div className="text-sm opacity-75">Hook secure chat</div>
            <div className="text-xs opacity-50">{open.role}</div>
          </div>
        </div>
        <button
          type="button"
          onClick={() => setConfirmEnd(true)}
          disabled={ending}
          className="min-h-[44px] text-sm px-4 py-2 rounded-full border border-white/30 hover:bg-white/10 disabled:opacity-50"
        >
          End chat
        </button>
      </header>
      <LegalFooter variant="chat" />
      <MessageList messages={state.messages} myParticipantId={open.participantId} />
      {waitingForPeer && <WaitingForPeer />}
      <MessageInput onSend={send} disabled={waitingForPeer} />

      {confirmEnd && (
        <EndChatConfirm
          ending={ending}
          onCancel={() => !ending && setConfirmEnd(false)}
          onConfirm={confirmAndEnd}
        />
      )}
    </div>
  );
}

function EndChatConfirm({
  ending,
  onCancel,
  onConfirm
}: {
  ending: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="end-chat-title"
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
      style={{
        paddingTop: "max(1rem, env(safe-area-inset-top))",
        paddingBottom: "max(1rem, env(safe-area-inset-bottom))"
      }}
      onClick={onCancel}
    >
      <div
        className="w-full max-w-[min(100%,24rem)] rounded-2xl bg-white shadow-xl p-5"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 id="end-chat-title" className="text-base font-semibold text-ink">End this chat?</h2>
        <p className="mt-2 text-sm text-slate-600">
          Both parties will be disconnected. The conversation cannot be resumed.
        </p>
        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onCancel}
            disabled={ending}
            className="min-h-[44px] px-4 py-2 text-sm rounded-full border border-slate-200 text-slate-700 hover:bg-slate-50 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            disabled={ending}
            autoFocus
            className="min-h-[44px] px-4 py-2 text-sm rounded-full bg-red-600 text-white hover:bg-red-700 disabled:opacity-50"
          >
            {ending ? "Ending…" : "End chat"}
          </button>
        </div>
      </div>
    </div>
  );
}

function endedMessage(reason: string | undefined, endedBy: string | undefined, myRole: string): string {
  switch (reason) {
    case "user":
      return endedBy && endedBy !== myRole
        ? `Chat ended by the ${endedBy.toLowerCase()}.`
        : "Chat ended.";
    case "idle":
      return "Chat ended due to inactivity.";
    case "expired":
      return "Chat expired.";
    default:
      return "Chat ended.";
  }
}

function CenterMessage({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen flex items-center justify-center p-6">
      <div className="text-slate-600 text-center max-w-md">{children}</div>
    </div>
  );
}
