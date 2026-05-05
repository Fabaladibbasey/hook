const STORAGE_KEY = "hook:device:id:v1";

export function getOrCreateDeviceId(): string {
  const existing = localStorage.getItem(STORAGE_KEY);
  if (existing) return existing;
  const id = globalThis.crypto.randomUUID();
  localStorage.setItem(STORAGE_KEY, id);
  return id;
}
