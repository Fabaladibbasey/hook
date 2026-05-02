import { Workbox } from "workbox-window";

export function registerSW() {
  if (import.meta.env.DEV) return;
  if (typeof window === "undefined" || !("serviceWorker" in navigator)) return;

  const wb = new Workbox("/sw.js");
  wb.addEventListener("waiting", () => {
    wb.messageSkipWaiting();
  });
  wb.register().catch((err) => console.warn("SW registration failed", err));
}
