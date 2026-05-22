import { useEffect, useState } from "react";
import { fetchJson } from "@/api/fetchJson";

type OutboxMessage = {
  at: string;
  to: string;
  body: string;
  messageId: string;
};

const TYPES = ["text", "interactive", "location"] as const;
type InboundType = (typeof TYPES)[number];

export default function DevConsole() {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [from, setFrom] = useState("+2207000000");
  const [type, setType] = useState<InboundType>("text");
  const [text, setText] = useState("hi");
  const [interactiveTitle, setInteractiveTitle] = useState("Yes");
  const [lat, setLat] = useState("13.4549");
  const [lng, setLng] = useState("-16.5790");
  const [outbox, setOutbox] = useState<OutboxMessage[]>([]);
  const [status, setStatus] = useState<string>("");

  useEffect(() => {
    fetchJson<{ enabled: boolean }>("/dev/whatsapp/status")
      .then((d) => setEnabled(d.enabled))
      .catch(() => setEnabled(false));
  }, []);

  useEffect(() => {
    if (!enabled) return;

    fetchJson<OutboxMessage[]>("/dev/whatsapp/outbox").then((d) => setOutbox(d.slice().reverse()));

    const es = new EventSource("/dev/whatsapp/outbox/stream");
    es.onmessage = (ev) => {
      const msg = JSON.parse(ev.data) as OutboxMessage;
      setOutbox((prev) => [msg, ...prev].slice(0, 200));
    };
    es.onerror = () => setStatus("SSE disconnected; will retry on reload");
    return () => es.close();
  }, [enabled]);

  async function send() {
    setStatus("");
    const body: Record<string, unknown> = { from, type };
    if (type === "text") body.text = text;
    if (type === "interactive") body.interactiveTitle = interactiveTitle;
    if (type === "location") {
      body.latitude = parseFloat(lat);
      body.longitude = parseFloat(lng);
    }
    try {
      const data = await fetchJson<{ messageId?: string }>("/dev/whatsapp/inbound", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      setStatus(`Injected ${data.messageId ?? ""}`);
    } catch (err) {
      setStatus(err instanceof Error ? err.message : String(err));
    }
  }

  if (enabled === null) return <div className="p-6">Loading…</div>;

  if (!enabled) {
    return (
      <div className="p-6 max-w-xl mx-auto">
        <h1 className="text-xl font-semibold">Dev console disabled</h1>
        <p className="text-slate-600 mt-2">
          Set <code>Dev:Whatsapp:Enabled = true</code> in backend appsettings to enable.
        </p>
      </div>
    );
  }

  return (
    <div className="min-h-screen p-6 max-w-5xl mx-auto grid md:grid-cols-2 gap-6">
      <section className="space-y-3">
        <h1 className="text-xl font-semibold">Inject inbound (as user)</h1>
        <label className="block">
          <span className="text-sm text-slate-600">From (E.164)</span>
          <input
            className="mt-1 w-full border rounded p-2"
            value={from}
            onChange={(e) => setFrom(e.target.value)}
          />
        </label>
        <label className="block">
          <span className="text-sm text-slate-600">Type</span>
          <select
            className="mt-1 w-full border rounded p-2"
            value={type}
            onChange={(e) => setType(e.target.value as InboundType)}
          >
            {TYPES.map((t) => (
              <option key={t} value={t}>{t}</option>
            ))}
          </select>
        </label>
        {type === "text" && (
          <label className="block">
            <span className="text-sm text-slate-600">Text body</span>
            <textarea
              className="mt-1 w-full border rounded p-2"
              rows={3}
              value={text}
              onChange={(e) => setText(e.target.value)}
            />
          </label>
        )}
        {type === "interactive" && (
          <label className="block">
            <span className="text-sm text-slate-600">Button/list title</span>
            <input
              className="mt-1 w-full border rounded p-2"
              value={interactiveTitle}
              onChange={(e) => setInteractiveTitle(e.target.value)}
            />
          </label>
        )}
        {type === "location" && (
          <div className="grid grid-cols-2 gap-2">
            <label className="block">
              <span className="text-sm text-slate-600">Latitude</span>
              <input className="mt-1 w-full border rounded p-2" value={lat} onChange={(e) => setLat(e.target.value)} />
            </label>
            <label className="block">
              <span className="text-sm text-slate-600">Longitude</span>
              <input className="mt-1 w-full border rounded p-2" value={lng} onChange={(e) => setLng(e.target.value)} />
            </label>
          </div>
        )}
        <button
          onClick={send}
          className="bg-slate-900 text-white px-4 py-2 rounded hover:bg-slate-700"
        >
          Send
        </button>
        {status && <p className="text-sm text-slate-700">{status}</p>}
      </section>

      <section>
        <h1 className="text-xl font-semibold">Outbox (fake WhatsApp send)</h1>
        <p className="text-xs text-slate-500 mb-2">Live via SSE. Newest first.</p>
        <div className="space-y-2 max-h-[70vh] overflow-auto">
          {outbox.length === 0 && <p className="text-slate-500 text-sm">No outbound yet.</p>}
          {outbox.map((m) => (
            <div key={m.messageId} className="border rounded p-3 bg-white">
              <div className="text-xs text-slate-500 flex justify-between">
                <span>to {m.to}</span>
                <span>{new Date(m.at).toLocaleTimeString()}</span>
              </div>
              <div className="mt-1 whitespace-pre-wrap">{m.body}</div>
              <div className="text-[10px] text-slate-400 mt-1">{m.messageId}</div>
            </div>
          ))}
        </div>
      </section>
    </div>
  );
}
