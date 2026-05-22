import { StrictMode, Suspense, lazy } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import LandingPage from "./LandingPage";
import "./styles.css";

const ChatRoom = lazy(() => import("./features/chat/ChatRoom"));
const DevConsole = lazy(() => import("./features/dev/DevConsole"));
const Terms = lazy(() => import("./features/legal/Terms"));
const Privacy = lazy(() => import("./features/legal/Privacy"));

const rootEl = document.getElementById("root");
if (!rootEl) throw new Error("Root element #root not found");
const root = createRoot(rootEl);
root.render(
  <StrictMode>
    <BrowserRouter>
      <Suspense fallback={<div className="p-6 text-slate-500">Loading…</div>}>
        <Routes>
          <Route path="/" element={<LandingPage />} />
          <Route path="/c/:chatId/:token" element={<ChatRoom />} />
          <Route path="/dev" element={<DevConsole />} />
          <Route path="/terms" element={<Terms />} />
          <Route path="/privacy" element={<Privacy />} />
        </Routes>
      </Suspense>
    </BrowserRouter>
  </StrictMode>
);
