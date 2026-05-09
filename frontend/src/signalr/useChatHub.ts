import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { useEffect, useRef, useState } from "react";
import { decrypt, deriveSharedKey, encrypt, getOrCreateKeypair } from "../crypto/chatCrypto";

export type ChatMessage = {
  id: string;
  participantId: string;
  sequence: number;
  plaintext: string | null;
  createdAt: string;
};

type WireMessage = {
  id: string;
  participantId: string;
  ciphertextB64: string;
  nonceB64: string;
  sequence: number;
  createdAt: string;
};

type PeerKeyDto = { peerParticipantId: string; peerPublicKeyB64: string };
type RejectDto = { messageId: string; reason: string };

const MAX_PENDING_WIRE = 200;

export type ChatEndReason = "user" | "idle" | "expired" | "other";

export type ChatState =
  | { kind: "connecting" }
  | { kind: "waiting-peer"; messages: ChatMessage[] }
  | { kind: "ready"; messages: ChatMessage[] }
  | { kind: "revoked" }
  | { kind: "ended"; reason?: ChatEndReason; endedBy?: string }
  | { kind: "error"; reason: string };

export function useChatHub(
  chatId: string,
  participantId: string,
  token: string,
  sessionId: string
) {
  const [state, setState] = useState<ChatState>({ kind: "connecting" });
  const connRef = useRef<HubConnection | null>(null);

  const sharedKeyRef = useRef<CryptoKey | null>(null);
  const peerParticipantIdRef = useRef<string | null>(null);
  const seqOutRef = useRef<number>(0);
  const messagesRef = useRef<ChatMessage[]>([]);
  const pendingWireRef = useRef<WireMessage[]>([]);

  useEffect(() => {
    if (!chatId || !participantId || !token || !sessionId) return;

    let cancelled = false;
    sharedKeyRef.current = null;
    peerParticipantIdRef.current = null;
    messagesRef.current = [];
    pendingWireRef.current = [];

    const conn = new HubConnectionBuilder()
      .withUrl(
        `/hubs/chat?token=${encodeURIComponent(token)}&sessionId=${encodeURIComponent(sessionId)}`,
        { headers: { "ngrok-skip-browser-warning": "true" } }
      )
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    const decryptOne = async (m: WireMessage): Promise<ChatMessage> => {
      // AAD recipient is whoever the sender encrypted *for*, so flip sides for own messages.
      const isMine = m.participantId === participantId;
      const recipientParticipantId = isMine ? peerParticipantIdRef.current : participantId;
      const plaintext = sharedKeyRef.current && recipientParticipantId
        ? await decrypt(sharedKeyRef.current, m.ciphertextB64, m.nonceB64, {
            chatId,
            senderParticipantId: m.participantId,
            recipientParticipantId,
            messageId: m.id,
            sequence: m.sequence
          })
        : null;
      return {
        id: m.id,
        participantId: m.participantId,
        sequence: m.sequence,
        plaintext,
        createdAt: m.createdAt
      };
    };

    const flushPending = async () => {
      if (!sharedKeyRef.current || pendingWireRef.current.length === 0) return;
      const pending = pendingWireRef.current;
      pendingWireRef.current = [];
      const decrypted = await Promise.all(pending.map(decryptOne));
      messagesRef.current = [...messagesRef.current, ...decrypted];
      if (!cancelled) setState({ kind: "ready", messages: messagesRef.current });
    };

    const updateOutSeq = (incoming: number) => {
      if (incoming > seqOutRef.current) seqOutRef.current = incoming;
    };

    const pushPending = (m: WireMessage) => {
      if (pendingWireRef.current.length >= MAX_PENDING_WIRE) pendingWireRef.current.shift();
      pendingWireRef.current.push(m);
    };

    conn.on("PeerKeyAvailable", async (dto: PeerKeyDto) => {
      if (dto.peerParticipantId === participantId) return;
      try {
        const { privateKey } = await getOrCreateKeypair(chatId);
        sharedKeyRef.current = await deriveSharedKey(privateKey, dto.peerPublicKeyB64, chatId);
        peerParticipantIdRef.current = dto.peerParticipantId;
      } catch {
        return;
      }
      if (pendingWireRef.current.length > 0) await flushPending();
      else if (!cancelled) setState({ kind: "ready", messages: messagesRef.current });
    });

    conn.on("HistoryLoaded", async (history: WireMessage[]) => {
      history.sort((a, b) => a.sequence - b.sequence);
      for (const m of history) updateOutSeq(m.sequence);
      if (sharedKeyRef.current) {
        const decrypted = await Promise.all(history.map(decryptOne));
        messagesRef.current = [...messagesRef.current, ...decrypted];
      } else {
        for (const m of history) pushPending(m);
      }
      if (!cancelled) {
        setState(sharedKeyRef.current
          ? { kind: "ready", messages: messagesRef.current }
          : { kind: "waiting-peer", messages: messagesRef.current });
      }
    });

    conn.on("MessageReceived", async (msg: WireMessage) => {
      updateOutSeq(msg.sequence);
      if (!sharedKeyRef.current) {
        pushPending(msg);
        return;
      }
      const decrypted = await decryptOne(msg);
      messagesRef.current = [...messagesRef.current, decrypted];
      if (!cancelled) setState({ kind: "ready", messages: messagesRef.current });
    });

    conn.on("MessageSendRejected", (dto: RejectDto) => {
      const idx = messagesRef.current.findIndex(m => m.id === dto.messageId);
      if (idx === -1) return;
      const updated = [...messagesRef.current];
      updated[idx] = { ...updated[idx], plaintext: `[failed: ${dto.reason}]` };
      messagesRef.current = updated;
      if (!cancelled) setState({ kind: "ready", messages: messagesRef.current });
    });

    conn.on("SessionRevoked", () => {
      setState({ kind: "revoked" });
      conn.stop().catch(() => {});
    });
    conn.on("SessionEnded", () => {
      setState({ kind: "ended" });
      conn.stop().catch(() => {});
    });
    conn.on("ChatEnded", (e?: { reason?: ChatEndReason; endedBy?: string }) =>
      setState({ kind: "ended", reason: e?.reason ?? "other", endedBy: e?.endedBy })
    );
    conn.on("ChatExpired", () => setState({ kind: "ended", reason: "expired" }));

    conn
      .start()
      .then(async () => {
        if (cancelled) {
          conn.stop().catch(() => {});
          return;
        }
        connRef.current = conn;
        try {
          const { publicSpkiB64 } = await getOrCreateKeypair(chatId);
          await conn.invoke("PublishKey", publicSpkiB64);
          if (!cancelled && !sharedKeyRef.current) {
            setState({ kind: "waiting-peer", messages: messagesRef.current });
          }
        } catch (err) {
          if (!cancelled) setState({ kind: "error", reason: `Key publish failed: ${String(err)}` });
        }
      })
      .catch(err => {
        if (!cancelled) setState({ kind: "error", reason: String(err) });
      });

    return () => {
      cancelled = true;
      connRef.current = null;
      sharedKeyRef.current = null;
      peerParticipantIdRef.current = null;
      pendingWireRef.current = [];
      messagesRef.current = [];
      if (conn.state === HubConnectionState.Connected) {
        conn.stop().catch(() => {});
      }
    };
  }, [chatId, participantId, token, sessionId]);

  const send = async (text: string) => {
    const conn = connRef.current;
    if (!conn || conn.state !== HubConnectionState.Connected) return;
    const sharedKey = sharedKeyRef.current;
    const peerParticipantId = peerParticipantIdRef.current;
    if (!sharedKey || !peerParticipantId) return;

    const seq = Math.max(Date.now(), seqOutRef.current + 1);
    seqOutRef.current = seq;

    const messageId = globalThis.crypto.randomUUID();
    const { ciphertextB64, nonceB64 } = await encrypt(sharedKey, text, {
      chatId,
      senderParticipantId: participantId,
      recipientParticipantId: peerParticipantId,
      messageId,
      sequence: seq
    });

    const optimistic: ChatMessage = {
      id: messageId,
      participantId,
      sequence: seq,
      plaintext: text,
      createdAt: new Date().toISOString()
    };
    messagesRef.current = [...messagesRef.current, optimistic];
    setState({ kind: "ready", messages: messagesRef.current });

    await conn.invoke("SendMessage", { messageId, ciphertextB64, nonceB64, sequence: seq });
  };

  const endChat = async () => {
    const conn = connRef.current;
    if (!conn || conn.state !== HubConnectionState.Connected) return;
    await conn.invoke("EndChat");
  };

  return { state, send, endChat };
}
