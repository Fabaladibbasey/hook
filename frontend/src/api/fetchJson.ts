const NGROK_BYPASS_HEADER = "ngrok-skip-browser-warning";

export async function fetchJson<T>(input: RequestInfo | URL, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (!headers.has(NGROK_BYPASS_HEADER)) headers.set(NGROK_BYPASS_HEADER, "true");

  const response = await fetch(input, { ...init, headers });

  if (!response.ok) {
    throw new Error(`Request failed (${response.status} ${response.statusText})`);
  }

  const contentType = response.headers.get("Content-Type") ?? "";
  if (!contentType.toLowerCase().startsWith("application/json")) {
    throw new Error(
      `Unexpected response (Content-Type: ${contentType || "none"}). Likely an ngrok or proxy interstitial.`
    );
  }

  return (await response.json()) as T;
}
