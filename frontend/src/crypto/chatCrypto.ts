const STORAGE_PREFIX = "hook:chat:";
const STORAGE_SUFFIX = ":keypair:v2";

const ECDH_PARAMS: EcKeyGenParams & EcKeyImportParams = { name: "ECDH", namedCurve: "P-256" };
const HKDF_HASH = "SHA-256";
// v3 binds recipientParticipantId + messageId into AAD. Old keypairs are evicted by the STORAGE_SUFFIX bump.
const HKDF_INFO_TEXT = "hook-chat-v3";
const AES_KEY_LENGTH = 256;
const NONCE_BYTES = 12;
const TAG_BITS = 128;

type StoredKeypair = {
  privateJwk: JsonWebKey;
  publicSpkiB64: string;
};

export type AadParts = {
  chatId: string;
  senderParticipantId: string;
  recipientParticipantId: string;
  messageId: string;
  sequence: number;
};

const subtle = () => globalThis.crypto.subtle;

const storageKey = (chatId: string) => `${STORAGE_PREFIX}${chatId}${STORAGE_SUFFIX}`;

const toBuffer = (bytes: Uint8Array): ArrayBuffer => {
  const out = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(out).set(bytes);
  return out;
};

const guidToBytes = (guid: string): Uint8Array => {
  const hex = guid.replace(/-/g, "").toLowerCase();
  if (hex.length !== 32) throw new Error(`invalid guid: ${guid}`);
  const bytes = new Uint8Array(16);
  for (let i = 0; i < 16; i++) bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
  return bytes;
};

const concatBytes = (...parts: Uint8Array[]): Uint8Array => {
  const total = parts.reduce((sum, p) => sum + p.length, 0);
  const out = new Uint8Array(total);
  let offset = 0;
  for (const p of parts) {
    out.set(p, offset);
    offset += p.length;
  }
  return out;
};

const bytesToBase64 = (bytes: Uint8Array): string => {
  let bin = "";
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin);
};

const base64ToBytes = (b64: string): Uint8Array => {
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return bytes;
};

export async function getOrCreateKeypair(chatId: string): Promise<{ privateKey: CryptoKey; publicSpkiB64: string }> {
  const stored = localStorage.getItem(storageKey(chatId));
  if (stored) {
    try {
      const parsed = JSON.parse(stored) as StoredKeypair;
      const privateKey = await subtle().importKey("jwk", parsed.privateJwk, ECDH_PARAMS, true, ["deriveKey", "deriveBits"]);
      return { privateKey, publicSpkiB64: parsed.publicSpkiB64 };
    } catch {
      localStorage.removeItem(storageKey(chatId));
    }
  }

  const pair = await subtle().generateKey(ECDH_PARAMS, true, ["deriveKey", "deriveBits"]);
  const privateJwk = await subtle().exportKey("jwk", pair.privateKey);
  const spki = new Uint8Array(await subtle().exportKey("spki", pair.publicKey));
  const publicSpkiB64 = bytesToBase64(spki);

  const toStore: StoredKeypair = { privateJwk, publicSpkiB64 };
  localStorage.setItem(storageKey(chatId), JSON.stringify(toStore));
  return { privateKey: pair.privateKey, publicSpkiB64 };
}

export async function deriveSharedKey(privateKey: CryptoKey, peerSpkiB64: string, chatId: string): Promise<CryptoKey> {
  const peerBytes = base64ToBytes(peerSpkiB64);
  const peerKey = await subtle().importKey("spki", toBuffer(peerBytes), ECDH_PARAMS, false, []);

  const sharedBits = await subtle().deriveBits({ name: "ECDH", public: peerKey }, privateKey, 256);

  const baseKey = await subtle().importKey("raw", sharedBits, "HKDF", false, ["deriveKey"]);
  const salt = toBuffer(guidToBytes(chatId));
  const info = new TextEncoder().encode(HKDF_INFO_TEXT);

  return subtle().deriveKey(
    { name: "HKDF", hash: HKDF_HASH, salt, info },
    baseKey,
    { name: "AES-GCM", length: AES_KEY_LENGTH },
    false,
    ["encrypt", "decrypt"]
  );
}

const buildAad = (parts: AadParts): Uint8Array => {
  const chatBytes = guidToBytes(parts.chatId);
  const senderBytes = guidToBytes(parts.senderParticipantId);
  const recipientBytes = guidToBytes(parts.recipientParticipantId);
  const messageBytes = guidToBytes(parts.messageId);
  const seqBytes = new Uint8Array(8);
  new DataView(seqBytes.buffer).setBigUint64(0, BigInt(parts.sequence), false);
  return concatBytes(chatBytes, senderBytes, recipientBytes, messageBytes, seqBytes);
};

export async function encrypt(
  key: CryptoKey,
  plaintext: string,
  aad: AadParts
): Promise<{ ciphertextB64: string; nonceB64: string }> {
  const nonceBytes = globalThis.crypto.getRandomValues(new Uint8Array(NONCE_BYTES));
  const nonce = toBuffer(nonceBytes);
  const ciphertext = new Uint8Array(
    await subtle().encrypt(
      { name: "AES-GCM", iv: nonce, additionalData: toBuffer(buildAad(aad)), tagLength: TAG_BITS },
      key,
      new TextEncoder().encode(plaintext)
    )
  );
  return { ciphertextB64: bytesToBase64(ciphertext), nonceB64: bytesToBase64(nonceBytes) };
}

export async function decrypt(
  key: CryptoKey,
  ciphertextB64: string,
  nonceB64: string,
  aad: AadParts
): Promise<string | null> {
  try {
    const ciphertext = toBuffer(base64ToBytes(ciphertextB64));
    const nonce = toBuffer(base64ToBytes(nonceB64));
    const plaintext = await subtle().decrypt(
      { name: "AES-GCM", iv: nonce, additionalData: toBuffer(buildAad(aad)), tagLength: TAG_BITS },
      key,
      ciphertext
    );
    return new TextDecoder().decode(plaintext);
  } catch {
    return null;
  }
}
