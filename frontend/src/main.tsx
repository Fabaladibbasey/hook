import { StrictMode, Suspense, lazy } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import App from "./App";
import Terms from "./routes/Terms";
import Privacy from "./routes/Privacy";
import "./styles.css";

const ChatRoom = lazy(() => import("./routes/ChatRoom"));
const DevConsole = lazy(() => import("./routes/DevConsole"));

const root = createRoot(document.getElementById("root")!);
root.render(
  <StrictMode>
    <BrowserRouter>
      <Suspense fallback={<div className="p-6 text-slate-500">Loading…</div>}>
        <Routes>
          <Route path="/" element={<App />} />
          <Route path="/c/:chatId/:token" element={<ChatRoom />} />
          <Route path="/dev" element={<DevConsole />} />
          <Route path="/terms" element={<Terms />} />
          <Route path="/privacy" element={<Privacy />} />
        </Routes>
      </Suspense>
    </BrowserRouter>
  </StrictMode>
);
