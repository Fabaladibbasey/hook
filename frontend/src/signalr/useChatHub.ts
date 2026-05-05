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
  senderDeviceId: string;
  ciphertextB64: string;
  nonceB64: string;
  sequence: number;
  createdAt: string;
};

type DeviceKeyDto = {
  participantId: string;
  deviceId: string;
  publicKeyB64: string;
};

type DeviceKeysSnapshot = { devices: DeviceKeyDto[] };

export type ChatEndReason = "user" | "idle" | "expired" | "other";

export type ChatState =
  | { kind: "connecting" }
  | { kind: "waiting-peer"; messages: ChatMessage[] }
  | { kind: "ready"; messages: ChatMessage[] }
  | { kind: "revoked" }
  | { kind: "ended"; reason?: ChatEndReason; endedBy?: string }
  | { kind: "error"; reason: string };

type DeviceEntry = { participantId: string; sharedKey: CryptoKey };

export function useChatHub(
  chatId: string,
  participantId: string,
  token: string,
  sessionId: string,
  deviceId: string
) {
  const [state, setState] = useState<ChatState>({ kind: "connecting" });
  const connRef = useRef<HubConnection | null>(null);

  const devicesRef = useRef<Map<string, DeviceEntry>>(new Map());
  const seqOutRef = useRef<number>(0);
  const messagesRef = useRef<ChatMessage[]>([]);
  const pendingWireRef = useRef<WireMessage[]>([]);

  useEffect(() => {
    if (!chatId || !participantId || !token || !sessionId || !deviceId) return;

    let cancelled = false;
    devicesRef.current = new Map();
    messagesRef.current = [];
    pendingWireRef.current = [];

    const conn = new HubConnectionBuilder()
      .withUrl(
        `/hubs/chat?token=${encodeURIComponent(token)}` +
          `&sessionId=${encodeURIComponent(sessionId)}` +
          `&deviceId=${encodeURIComponent(deviceId)}`,
        { headers: { "ngrok-skip-browser-warning": "true" } }
      )
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    const decryptOne = async (m: WireMessage): Promise<ChatMessage> => {
      const entry = devicesRef.current.get(m.senderDeviceId);
      const aad = {
        chatId,
        senderParticipantId: m.participantId,
        senderDeviceId: m.senderDeviceId,
        recipientDeviceId: deviceId,
        sequence: m.sequence
      };
      const plaintext = entry ? await decrypt(entry.sharedKey, m.ciphertextB64, m.nonceB64, aad) : null;
      return { id: m.id, participantId: m.participantId, plaintext, createdAt: m.createdAt };
    };

    const flushPending = async () => {
      if (pendingWireRef.current.length === 0) return;
      const ready: WireMessage[] = [];
      const stillPending: WireMessage[] = [];
      for (const m of pendingWireRef.current) {
        (devicesRef.current.has(m.senderDeviceId) ? ready : stillPending).push(m);
      }
      if (ready.length === 0) return;
      pendingWireRef.current = stillPending;
      const decrypted = await Promise.all(ready.map(decryptOne));
      messagesRef.current = [...messagesRef.current, ...decrypted];
      if (!cancelled) setState({ kind: "ready", messages: messagesRef.current });
    };

    const updateOutSeq = (incoming: number) => {
      if (incoming > seqOutRef.current) seqOutRef.current = incoming;
    };

    const ingestDeviceKey = async (dto: DeviceKeyDto) => {
      if (dto.deviceId === deviceId) return;
      if (devicesRef.current.has(dto.deviceId)) return;
      const { privateKey } = await getOrCreateKeypair(chatId);
      const sharedKey = await deriveSharedKey(privateKey, dto.publicKeyB64, chatId);
      devicesRef.current.set(dto.deviceId, { participantId: dto.participantId, sharedKey });
    };

    const peerDeviceCount = () => {
      let n = 0;
      for (const d of devicesRef.current.values()) if (d.participantId !== participantId) n++;
      return n;
    };

    conn.on("DeviceKeysSnapshot", async (snapshot: DeviceKeysSnapshot) => {
      for (const dto of snapshot.devices) {
        try { await ingestDeviceKey(dto); } catch { /* skip bad key */ }
      }
      await flushPending();
      if (!cancelled) {
        setState(peerDeviceCount() > 0
          ? { kind: "ready", messages: messagesRef.current }
          : { kind: "waiting-peer", messages: messagesRef.current });
      }
    });

    conn.on("PeerDeviceKeyAvailable", async (dto: DeviceKeyDto) => {
      try { await ingestDeviceKey(dto); }
      catch { return; }
      await flushPending();
      if (!cancelled && peerDeviceCount() > 0) {
        setState({ kind: "ready", messages: messagesRef.current });
      }
    });

    conn.on("HistoryLoaded", async (history: WireMessage[]) => {
      history.sort((a, b) => a.sequence - b.sequence);
      const decryptable: WireMessage[] = [];
      for (const m of history) {
        updateOutSeq(m.sequence);
        if (devicesRef.current.has(m.senderDeviceId)) decryptable.push(m);
        else pendingWireRef.current.push(m);
      }
      if (decryptable.length > 0) {
        const decrypted = await Promise.all(decryptable.map(decryptOne));
        messagesRef.current = [...messagesRef.current, ...decrypted];
      }
      if (!cancelled) {
        setState(peerDeviceCount() > 0
          ? { kind: "ready", messages: messagesRef.current }
          : { kind: "waiting-peer", messages: messagesRef.current });
      }
    });

    conn.on("MessageReceived", async (msg: WireMessage) => {
      updateOutSeq(msg.sequence);
      if (!devicesRef.current.has(msg.senderDeviceId)) {
        pendingWireRef.current.push(msg);
        return;
      }
      const decrypted = await decryptOne(msg);
      messagesRef.current = [...messagesRef.current, decrypted];
      if (!cancelled) setState({ kind: "ready", messages: messagesRef.current });
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
          if (!cancelled && peerDeviceCount() === 0) {
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
      devicesRef.current = new Map();
      pendingWireRef.current = [];
      messagesRef.current = [];
      if (conn.state === HubConnectionState.Connected) {
        conn.stop().catch(() => {});
      }
    };
  }, [chatId, participantId, token, sessionId, deviceId]);

  const send = async (text: string) => {
    const conn = connRef.current;
    if (!conn || conn.state !== HubConnectionState.Connected) return;

    let hasPeer = false;
    for (const e of devicesRef.current.values()) {
      if (e.participantId !== participantId) { hasPeer = true; break; }
    }
    if (!hasPeer) return;

    const seq = Math.max(Date.now(), seqOutRef.current + 1);
    seqOutRef.current = seq;

    const recipients = await Promise.all(
      [...devicesRef.current.entries()].map(async ([recipientDeviceId, entry]) => {
        const { ciphertextB64, nonceB64 } = await encrypt(entry.sharedKey, text, {
          chatId,
          senderParticipantId: participantId,
          senderDeviceId: deviceId,
          recipientDeviceId,
          sequence: seq
        });
        return { deviceId: recipientDeviceId, ciphertextB64, nonceB64 };
      })
    );

    const optimistic: ChatMessage = {
      id: globalThis.crypto.randomUUID(),
      participantId,
      plaintext: text,
      createdAt: new Date().toISOString()
    };
    messagesRef.current = [...messagesRef.current, optimistic];
    setState({ kind: "ready", messages: messagesRef.current });

    await conn.invoke("SendMessage", { recipients, sequence: seq });
  };

  const endChat = async () => {
    const conn = connRef.current;
    if (!conn || conn.state !== HubConnectionState.Connected) return;
    await conn.invoke("EndChat");
  };

  return { state, send, endChat };
}
