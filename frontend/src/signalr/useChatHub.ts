import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { useEffect, useRef, useState } from "react";
import { decrypt, deriveSharedKey, encrypt, getOrCreateKeypair } from "../crypto/chatCrypto";

export type ChatMessage = {
  id: string;
  participantId: string;
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

type PeerKeyEvent = {
  peerParticipantId: string;
  peerPublicKeyB64: string;
};

export type ChatEndReason = "user" | "idle" | "expired" | "other";

export type ChatState =
  | { kind: "connecting" }
  | { kind: "waiting-peer"; messages: ChatMessage[] }
  | { kind: "ready"; messages: ChatMessage[] }
  | { kind: "revoked" }
  | { kind: "ended"; reason?: ChatEndReason; endedBy?: string }
  | { kind: "error"; reason: string };

export function useChatHub(chatId: string, participantId: string, token: string, sessionId: string) {
  const [state, setState] = useState<ChatState>({ kind: "connecting" });
  const connRef = useRef<HubConnection | null>(null);

  const sharedKeyRef = useRef<CryptoKey | null>(null);
  const seqOutRef = useRef<number>(0);
  const messagesRef = useRef<ChatMessage[]>([]);
  const pendingWireRef = useRef<WireMessage[]>([]);

  useEffect(() => {
    if (!chatId || !participantId || !token || !sessionId) return;

    let cancelled = false;

    const conn = new HubConnectionBuilder()
      .withUrl(`/hubs/chat?token=${encodeURIComponent(token)}&sessionId=${encodeURIComponent(sessionId)}`)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    const decryptOne = async (m: WireMessage): Promise<ChatMessage> => {
      const key = sharedKeyRef.current;
      const aad = { chatId, senderParticipantId: m.participantId, sequence: m.sequence };
      const plaintext = key ? await decrypt(key, m.ciphertextB64, m.nonceB64, aad) : null;
      return { id: m.id, participantId: m.participantId, plaintext, createdAt: m.createdAt };
    };

    const flushPending = async () => {
      if (!sharedKeyRef.current || pendingWireRef.current.length === 0) return;
      const drained = pendingWireRef.current;
      pendingWireRef.current = [];
      const decrypted = await Promise.all(drained.map(decryptOne));
      messagesRef.current = [...messagesRef.current, ...decrypted];
      if (!cancelled) setState({ kind: "ready", messages: messagesRef.current });
    };

    const updateOutSeq = (incoming: number) => {
      if (incoming > seqOutRef.current) seqOutRef.current = incoming;
    };

    conn.on("HistoryLoaded", async (history: WireMessage[]) => {
      const ordered = [...history].sort((a, b) => a.sequence - b.sequence);
      ordered.forEach((m) => updateOutSeq(m.sequence));
      if (!sharedKeyRef.current) {
        pendingWireRef.current.push(...ordered);
        if (!cancelled) setState({ kind: "waiting-peer", messages: messagesRef.current });
        return;
      }
      const decrypted = await Promise.all(ordered.map(decryptOne));
      messagesRef.current = decrypted;
      if (!cancelled) setState({ kind: "ready", messages: messagesRef.current });
    });

    conn.on("MessageReceived", async (msg: WireMessage) => {
      updateOutSeq(msg.sequence);
      if (!sharedKeyRef.current) {
        pendingWireRef.current.push(msg);
        return;
      }
      const decrypted = await decryptOne(msg);
      messagesRef.current = [...messagesRef.current, decrypted];
      if (!cancelled) setState({ kind: "ready", messages: messagesRef.current });
    });

    conn.on("PeerKeyAvailable", async (event: PeerKeyEvent) => {
      if (event.peerParticipantId === participantId) return;
      if (sharedKeyRef.current) return;
      try {
        const { privateKey } = await getOrCreateKeypair(chatId);
        sharedKeyRef.current = await deriveSharedKey(privateKey, event.peerPublicKeyB64, chatId);
        await flushPending();
        if (!cancelled) setState({ kind: "ready", messages: messagesRef.current });
      } catch (err) {
        if (!cancelled) setState({ kind: "error", reason: `Crypto setup failed: ${String(err)}` });
      }
    });

    conn.on("SessionRevoked", () => setState({ kind: "revoked" }));
    conn.on("SessionEnded", () => setState({ kind: "ended" }));
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
      .catch((err) => {
        if (!cancelled) setState({ kind: "error", reason: String(err) });
      });

    return () => {
      cancelled = true;
      connRef.current = null;
      sharedKeyRef.current = null;
      pendingWireRef.current = [];
      messagesRef.current = [];
      if (conn.state === HubConnectionState.Connected) {
        conn.stop().catch(() => {});
      }
    };
  }, [chatId, participantId, token, sessionId]);

  const send = async (text: string) => {
    const conn = connRef.current;
    const key = sharedKeyRef.current;
    if (!conn || conn.state !== HubConnectionState.Connected || !key) return;

    const seq = Math.max(Date.now(), seqOutRef.current + 1);
    seqOutRef.current = seq;

    const { ciphertextB64, nonceB64 } = await encrypt(key, text, {
      chatId,
      senderParticipantId: participantId,
      sequence: seq
    });

    await conn.invoke("SendMessage", { ciphertextB64, nonceB64, sequence: seq });
  };

  const endChat = async () => {
    const conn = connRef.current;
    if (!conn || conn.state !== HubConnectionState.Connected) return;
    await conn.invoke("EndChat");
  };

  return { state, send, endChat };
}
